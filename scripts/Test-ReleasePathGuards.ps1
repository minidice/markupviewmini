[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-CheckedGit {
    param(
        [Parameter(Mandatory)] [string] $Repository,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    & git -C $Repository @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture Git command failed: git $($Arguments -join ' ')"
    }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$runRoot = [IO.Path]::GetFullPath(
    (Join-Path $tempRoot "MarkUpViewMini-ReleasePathGuard-$([Guid]::NewGuid().ToString('N'))"))
$relativeRunRoot = [IO.Path]::GetRelativePath($tempRoot, $runRoot)
if ([IO.Path]::IsPathRooted($relativeRunRoot) -or
    -not [IO.Path]::GetFileName($runRoot).StartsWith(
        'MarkUpViewMini-ReleasePathGuard-',
        [StringComparison]::Ordinal)) {
    throw 'The release-path guard fixture escaped its controlled temp root.'
}

$fixtureRepository = Join-Path $runRoot 'repository'
$fixtureScripts = Join-Path $fixtureRepository 'scripts'
$externalRoot = Join-Path $runRoot 'external'
$junctionPath = Join-Path $fixtureRepository 'artifacts'
$sentinelDirectory = Join-Path $externalRoot 'portable\MarkUpViewMini'
$sentinelPath = Join-Path $sentinelDirectory 'external-sentinel.txt'
$dummyProjectDirectory = Join-Path $fixtureRepository 'src\Dummy'
$buildExternalRoot = Join-Path $externalRoot 'build-output'
$buildSentinelPath = Join-Path $buildExternalRoot 'build-sentinel.txt'
$buildJunctionPath = Join-Path $dummyProjectDirectory 'bin'
$webExternalRoot = Join-Path $externalRoot 'web-dist'
$webSentinelPath = Join-Path $webExternalRoot 'web-sentinel.txt'
$webDistJunctionPath = Join-Path $fixtureRepository 'web\document-surface\dist'
$junctionCreated = $false
$buildJunctionCreated = $false
$webJunctionCreated = $false
$failure = $null
$npmInvoked = $false
try {
    New-Item -ItemType Directory -Path $fixtureScripts | Out-Null
    New-Item -ItemType Directory -Path $sentinelDirectory | Out-Null
    New-Item -ItemType Directory -Path $dummyProjectDirectory | Out-Null
    New-Item -ItemType Directory -Path $buildExternalRoot | Out-Null
    New-Item -ItemType Directory -Path $webExternalRoot | Out-Null
    Set-Content -LiteralPath $sentinelPath -Value 'external sentinel must survive' -Encoding utf8NoBOM
    Set-Content -LiteralPath $buildSentinelPath -Value 'build sentinel must survive' -Encoding utf8NoBOM
    Set-Content -LiteralPath $webSentinelPath -Value 'web sentinel must survive' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $dummyProjectDirectory 'Dummy.csproj') `
        -Value '<Project Sdk="Microsoft.NET.Sdk" />' `
        -Encoding utf8NoBOM
    foreach ($entrypoint in @('publish-portable.ps1', 'benchmark.ps1', 'smoke-portable.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $entrypoint) -Destination $fixtureScripts
    }
    $safetyModule = Join-Path $PSScriptRoot 'ReleasePathSafety.psm1'
    if (Test-Path -LiteralPath $safetyModule) {
        Copy-Item -LiteralPath $safetyModule -Destination $fixtureScripts
    }
    Set-Content -LiteralPath (Join-Path $fixtureRepository '.gitignore') `
        -Value @('artifacts/', '**/bin/', 'web/**/dist/', 'web/**/node_modules/') `
        -Encoding utf8NoBOM

    Invoke-CheckedGit -Repository $fixtureRepository -Arguments @('init', '--quiet')
    Invoke-CheckedGit -Repository $fixtureRepository -Arguments @('config', 'user.name', 'Release Path Guard')
    Invoke-CheckedGit -Repository $fixtureRepository -Arguments @('config', 'user.email', 'release-path-guard.invalid')
    Invoke-CheckedGit -Repository $fixtureRepository -Arguments @('add', 'scripts', 'src', '.gitignore')
    Invoke-CheckedGit -Repository $fixtureRepository -Arguments @('commit', '--quiet', '-m', 'fixture')

    New-Item -ItemType Junction -Path $junctionPath -Target $externalRoot | Out-Null
    $junctionCreated = $true
    function npm {
        $script:npmInvoked = $true
        throw 'npm must not run after a reparse-backed artifact root is detected.'
    }

    try {
        & (Join-Path $fixtureScripts 'publish-portable.ps1')
    }
    catch {
        $failure = $_
    }

    if ($null -eq $failure) {
        throw 'The publish script accepted a reparse-backed artifact root.'
    }
    if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)) {
        throw 'The publish script traversed the artifact junction and removed the external sentinel.'
    }
    if ($npmInvoked) {
        throw 'The publish script reached npm before rejecting the artifact junction.'
    }
    if (-not $failure.Exception.Message.Contains('reparse', [StringComparison]::OrdinalIgnoreCase)) {
        throw "The publish script did not report the reparse boundary: $($failure.Exception.Message)"
    }

    $guardedEntrypoints = [ordered]@{}
    foreach ($entrypoint in @('benchmark.ps1', 'smoke-portable.ps1')) {
        $entrypointFailure = $null
        try {
            & (Join-Path $fixtureScripts $entrypoint)
        }
        catch {
            $entrypointFailure = $_
        }
        if ($null -eq $entrypointFailure -or
            -not $entrypointFailure.Exception.Message.Contains(
                'reparse',
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "$entrypoint did not reject the reparse-backed artifact root."
        }
        if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)) {
            throw "$entrypoint mutated the external sentinel through the artifact junction."
        }

        $guardedEntrypoints[$entrypoint] = $true
    }

    [IO.Directory]::Delete($junctionPath, $false)
    $junctionCreated = $false
    Import-Module (Join-Path $fixtureScripts 'ReleasePathSafety.psm1') -Force

    New-Item -ItemType Junction -Path $buildJunctionPath -Target $buildExternalRoot | Out-Null
    $buildJunctionCreated = $true
    $buildFailure = $null
    try {
        Assert-RepositoryBuildMutationPaths -RepositoryRoot $fixtureRepository
    }
    catch {
        $buildFailure = $_
    }
    if ($null -eq $buildFailure -or
        -not $buildFailure.Exception.Message.Contains('reparse', [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $buildSentinelPath -PathType Leaf)) {
        throw 'The shared build preflight did not safely reject the project bin junction.'
    }
    [IO.Directory]::Delete($buildJunctionPath, $false)
    $buildJunctionCreated = $false

    New-Item -ItemType Directory -Path (Split-Path -Parent $webDistJunctionPath) | Out-Null
    New-Item -ItemType Junction -Path $webDistJunctionPath -Target $webExternalRoot | Out-Null
    $webJunctionCreated = $true
    $webFailure = $null
    try {
        Assert-RepositoryBuildMutationPaths -RepositoryRoot $fixtureRepository
    }
    catch {
        $webFailure = $_
    }
    if ($null -eq $webFailure -or
        -not $webFailure.Exception.Message.Contains('reparse', [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $webSentinelPath -PathType Leaf)) {
        throw 'The shared build preflight did not safely reject the web dist junction.'
    }

    [ordered]@{
        publishJunctionRejected = $true
        benchmarkJunctionRejected = $guardedEntrypoints['benchmark.ps1']
        smokeJunctionRejected = $guardedEntrypoints['smoke-portable.ps1']
        buildOutputJunctionRejected = $true
        webDistJunctionRejected = $true
        externalSentinelPreserved = $true
        npmInvoked = $false
    } | ConvertTo-Json
}
finally {
    if ($webJunctionCreated -and (Test-Path -LiteralPath $webDistJunctionPath)) {
        [IO.Directory]::Delete($webDistJunctionPath, $false)
    }
    if ($buildJunctionCreated -and (Test-Path -LiteralPath $buildJunctionPath)) {
        [IO.Directory]::Delete($buildJunctionPath, $false)
    }
    if ($junctionCreated -and (Test-Path -LiteralPath $junctionPath)) {
        $junction = Get-Item -LiteralPath $junctionPath -Force
        if (-not ($junction.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'The fixture artifact path stopped being a junction before cleanup.'
        }

        [IO.Directory]::Delete($junctionPath, $false)
    }

    if (Test-Path -LiteralPath $runRoot) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    }
}
