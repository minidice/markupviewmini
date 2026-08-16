[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $Output = '.\artifacts\benchmark.json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleasePathSafety.psm1') -Force

function Assert-ArtifactOutputPath {
    param(
        [Parameter(Mandatory)] [string] $Path
    )

    $fullPath = Assert-ControlledMutationPath -Path $Path -TrustedRoot $repositoryRoot
    $relative = [IO.Path]::GetRelativePath($artifactRoot, $fullPath)
    if ([IO.Path]::IsPathRooted($relative) -or
        $relative -eq '..' -or
        $relative.StartsWith("..$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal)) {
        throw 'A controlled benchmark path escaped its verified parent.'
    }

    $fullPath
}

function Get-OwnedWebViewProcesses {
    param([Parameter(Mandatory)] [string] $UserDataDirectory)

    @(Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            -not [string]::IsNullOrEmpty($_.CommandLine) -and
            $_.CommandLine.Contains($UserDataDirectory, [StringComparison]::OrdinalIgnoreCase)
        })
}

function Get-CheckedGitOutput {
    param([Parameter(Mandatory)] [string[]] $ArgumentList)

    $output = & git -C $repositoryRoot @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Git provenance query failed: git $($ArgumentList -join ' ')"
    }

    ($output -join [Environment]::NewLine).Trim()
}

$repositoryRoot = Get-VerifiedPhysicalRoot -Root (Join-Path $PSScriptRoot '..')
$artifactRoot = Assert-ControlledMutationPath `
    -Path (Join-Path $repositoryRoot 'artifacts') `
    -TrustedRoot $repositoryRoot
$outputPath = if ([IO.Path]::IsPathRooted($Output)) {
    Assert-ArtifactOutputPath -Path $Output
} else {
    Assert-ArtifactOutputPath -Path (Join-Path $repositoryRoot $Output)
}
$applicationRoot = Assert-ControlledMutationPath `
    -Path (Join-Path $artifactRoot 'portable\MarkUpViewMini') `
    -TrustedRoot $repositoryRoot `
    -RejectReparseDescendants
$applicationExe = Join-Path $applicationRoot 'MarkUpViewMini.App.exe'
$portableMarker = Join-Path $applicationRoot 'portable.marker'
$applicationData = Assert-ControlledMutationPath `
    -Path (Join-Path $applicationRoot 'data') `
    -TrustedRoot $repositoryRoot `
    -RejectReparseDescendants
if (-not (Test-Path -LiteralPath $applicationExe) -or
    -not (Test-Path -LiteralPath $portableMarker)) {
    throw 'Publish the controlled portable release before running the benchmark.'
}
if (@(Get-Process -Name 'MarkUpViewMini.App' -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'Close existing MarkUpViewMini.App processes before running the benchmark.'
}
if (Test-Path -LiteralPath $applicationData) {
    throw 'The controlled portable publish data directory must be clean before benchmarking.'
}
if (@(Get-OwnedWebViewProcesses -UserDataDirectory $applicationData).Count -ne 0) {
    throw 'A WebView process already owns the controlled portable benchmark data directory.'
}

$provenancePath = Assert-ControlledMutationPath `
    -Path (Join-Path $applicationRoot 'release-provenance.json') `
    -TrustedRoot $repositoryRoot
if (-not (Test-Path -LiteralPath $provenancePath)) {
    throw 'The controlled portable publish has no release provenance.'
}
$provenance = Get-Content -Raw -LiteralPath $provenancePath | ConvertFrom-Json
if ($provenance.schemaVersion -ne 1 -or
    $provenance.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
    $provenance.sourceTree -notmatch '^[0-9a-f]{40}$') {
    throw 'The portable release provenance schema or Git object IDs are invalid.'
}
$trackedStatus = Get-CheckedGitOutput @('status', '--porcelain', '--untracked-files=all')
$currentCommit = Get-CheckedGitOutput @('rev-parse', 'HEAD')
$currentTree = Get-CheckedGitOutput @('rev-parse', 'HEAD^{tree}')
if (-not [string]::IsNullOrWhiteSpace($trackedStatus) -or
    $provenance.sourceCommit -ne $currentCommit -or
    $provenance.sourceTree -ne $currentTree) {
    throw 'The portable publish is not bound to the exact clean benchmark source commit.'
}
$applicationProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
    (Join-Path $applicationRoot 'MarkUpViewMini.App.dll')).ProductVersion
if ([string]::IsNullOrWhiteSpace($applicationProductVersion) -or
    $applicationProductVersion -ne $provenance.applicationProductVersion -or
    -not $applicationProductVersion.EndsWith(
        "+$($provenance.sourceCommit)",
        [StringComparison]::Ordinal)) {
    throw 'The published application assembly revision does not match its provenance.'
}
$actualPortableRelativePaths = [string[]] @(
    Get-ChildItem -LiteralPath $applicationRoot -Recurse -File |
        Where-Object { -not $_.FullName.Equals($provenancePath, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            [IO.Path]::GetRelativePath($applicationRoot, $_.FullName).Replace('\', '/')
        })
[Array]::Sort($actualPortableRelativePaths, [StringComparer]::Ordinal)
$actualPortableFiles = @($actualPortableRelativePaths | ForEach-Object {
    $portableFile = Get-Item -LiteralPath (Join-Path $applicationRoot $_)
    [ordered]@{
        path = $_
        length = [long] $portableFile.Length
        sha256 = (Get-FileHash -LiteralPath $portableFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$expectedPortableFiles = @($provenance.files)
if ($actualPortableFiles.Count -ne $expectedPortableFiles.Count) {
    throw 'The portable file count does not match its release provenance.'
}
for ($index = 0; $index -lt $actualPortableFiles.Count; $index++) {
    $actual = $actualPortableFiles[$index]
    $expected = $expectedPortableFiles[$index]
    if ($actual.path -cne $expected.path -or
        $actual.length -ne $expected.length -or
        $actual.sha256 -cne $expected.sha256) {
        throw "Portable provenance mismatch at manifest index $index."
    }
}
$provenanceSha256 = (Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256).Hash.ToLowerInvariant()

$tempRoot = Get-VerifiedPhysicalRoot -Root ([IO.Path]::GetTempPath())
$runRoot = Assert-ControlledMutationPath `
    -Path (Join-Path $tempRoot "MarkUpViewMini-Benchmark-$([Guid]::NewGuid().ToString('N'))") `
    -TrustedRoot $tempRoot
if (-not [IO.Path]::GetFileName($runRoot).StartsWith(
        'MarkUpViewMini-Benchmark-',
        [StringComparison]::Ordinal)) {
    throw 'The controlled benchmark directory does not use the expected prefix.'
}

$previousRun = $env:MARKUPVIEWMINI_RUN_PERF
$previousApp = $env:MARKUPVIEWMINI_PERF_APP_DIR
$previousResults = $env:MARKUPVIEWMINI_PERF_RESULT_DIR
$testFailure = $null
try {
    New-ControlledDirectory -Path $runRoot -TrustedRoot $tempRoot | Out-Null
    $resultDirectory = Assert-ControlledMutationPath `
        -Path (Join-Path $runRoot 'results') `
        -TrustedRoot $tempRoot
    New-ControlledDirectory -Path $resultDirectory -TrustedRoot $tempRoot | Out-Null

    $env:MARKUPVIEWMINI_RUN_PERF = '1'
    $env:MARKUPVIEWMINI_PERF_APP_DIR = $applicationRoot
    $env:MARKUPVIEWMINI_PERF_RESULT_DIR = $resultDirectory
    Push-Location $repositoryRoot
    try {
        Assert-RepositoryBuildMutationPaths -RepositoryRoot $repositoryRoot
        dotnet test .\tests\MarkUpViewMini.PerformanceTests\MarkUpViewMini.PerformanceTests.csproj `
            --configuration $Configuration `
            --nologo `
            -m:1 `
            -nr:false
        if ($LASTEXITCODE -ne 0) {
            throw "The gated performance tests failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    $reader = Get-Content -Raw -LiteralPath (Join-Path $resultDirectory 'reader.json') | ConvertFrom-Json
    $searchFirst = Get-Content -Raw -LiteralPath (Join-Path $resultDirectory 'searchFirstResult.json') | ConvertFrom-Json
    $searchCancellation = Get-Content -Raw -LiteralPath (Join-Path $resultDirectory 'searchCancellation.json') | ConvertFrom-Json
    $metrics = @($reader, $searchFirst, $searchCancellation)
    if (@($metrics | Where-Object { -not $_.passed }).Count -ne 0) {
        throw 'One or more benchmark metrics did not pass its exact release threshold.'
    }
    if ($searchFirst.fixtureSha256 -ne $searchCancellation.fixtureSha256) {
        throw 'Search benchmark fixture hashes did not match.'
    }

    $operatingSystem = Get-CimInstance Win32_OperatingSystem
    $processorNames = @(
        Get-CimInstance Win32_Processor |
            ForEach-Object { $_.Name.Trim() } |
            Sort-Object -Unique)
    $computerSystem = Get-CimInstance Win32_ComputerSystem
    $dotnetVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet --version failed.'
    }
    $result = [ordered]@{
        schemaVersion = 1
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        environment = [ordered]@{
            windowsVersion = $operatingSystem.Version
            windowsBuild = $operatingSystem.BuildNumber
            cpuModel = ($processorNames -join '; ')
            totalMemoryBytes = [long] $computerSystem.TotalPhysicalMemory
            dotnetVersion = $dotnetVersion
        }
        appCommit = $provenance.sourceCommit
        appTree = $provenance.sourceTree
        portableProvenanceSha256 = $provenanceSha256
        deterministicSeed = 20260812
        warmup = 'none; one representative measured run per metric'
        fixtures = [ordered]@{
            reader = [ordered]@{
                bytes = 5 * 1024 * 1024
                sha256 = $reader.fixtureSha256
            }
            search = [ordered]@{
                fileCount = 1000
                matchEvery = 10
                knownMatches = 100
                sha256 = $searchFirst.fixtureSha256
            }
        }
        metrics = [ordered]@{
            processStartToDocumentRendered = [ordered]@{
                elapsedMilliseconds = [double] $reader.elapsedMilliseconds
                thresholdMilliseconds = [double] $reader.thresholdMilliseconds
                passed = [bool] $reader.passed
            }
            searchStartToFirstResult = [ordered]@{
                elapsedMilliseconds = [double] $searchFirst.elapsedMilliseconds
                thresholdMilliseconds = [double] $searchFirst.thresholdMilliseconds
                passed = [bool] $searchFirst.passed
            }
            cancellationToLastYield = [ordered]@{
                elapsedMilliseconds = [double] $searchCancellation.elapsedMilliseconds
                thresholdMilliseconds = [double] $searchCancellation.thresholdMilliseconds
                passed = [bool] $searchCancellation.passed
            }
        }
        passed = [bool] (@($metrics | Where-Object { -not $_.passed }).Count -eq 0)
    }

    $outputDirectory = Split-Path -Parent $outputPath
    New-ControlledDirectory -Path $outputDirectory -TrustedRoot $repositoryRoot | Out-Null
    Assert-ControlledMutationPath -Path $outputPath -TrustedRoot $repositoryRoot | Out-Null
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputPath -Encoding utf8NoBOM
    $result | ConvertTo-Json -Depth 8
}
catch {
    $testFailure = $_
}
finally {
    if ($null -eq $previousRun) { Remove-Item Env:MARKUPVIEWMINI_RUN_PERF -ErrorAction SilentlyContinue }
    else { $env:MARKUPVIEWMINI_RUN_PERF = $previousRun }
    if ($null -eq $previousApp) { Remove-Item Env:MARKUPVIEWMINI_PERF_APP_DIR -ErrorAction SilentlyContinue }
    else { $env:MARKUPVIEWMINI_PERF_APP_DIR = $previousApp }
    if ($null -eq $previousResults) { Remove-Item Env:MARKUPVIEWMINI_PERF_RESULT_DIR -ErrorAction SilentlyContinue }
    else { $env:MARKUPVIEWMINI_PERF_RESULT_DIR = $previousResults }

    if (Test-Path -LiteralPath $runRoot) {
        try {
            Remove-ControlledItem -Path $runRoot -TrustedRoot $tempRoot -Recurse
        }
        catch {
            if ($null -eq $testFailure) { $testFailure = $_ }
            else { $testFailure = [AggregateException]::new('Benchmark and cleanup failed.', @($testFailure.Exception, $_.Exception)) }
        }
    }
}

if (@(Get-Process -Name 'MarkUpViewMini.App' -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'A MarkUpViewMini.App process remained after benchmark cleanup.'
}
if (@(Get-OwnedWebViewProcesses -UserDataDirectory $applicationData).Count -ne 0) {
    throw 'A controlled WebView process remained after benchmark cleanup.'
}
if (Test-Path -LiteralPath $applicationData) {
    throw 'The controlled portable data directory remained after benchmark cleanup.'
}
if (Test-Path -LiteralPath $runRoot) {
    throw 'The controlled benchmark fixture directory remained after cleanup.'
}
if ($null -ne $testFailure) {
    throw $testFailure
}
