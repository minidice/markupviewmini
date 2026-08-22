[CmdletBinding()]
param(
    [switch] $FrameworkDependent
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleasePathSafety.psm1') -Force

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath $($ArgumentList -join ' ')' failed with exit code $LASTEXITCODE."
    }
}

function Get-CheckedGitOutput {
    param([Parameter(Mandatory)] [string[]] $ArgumentList)

    $output = & git -C $repositoryRoot @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Git provenance query failed: git $($ArgumentList -join ' ')"
    }

    return ($output -join [Environment]::NewLine).Trim()
}

$repositoryRoot = Get-VerifiedPhysicalRoot -Root (Join-Path $PSScriptRoot '..')
$artifactRoot = Assert-ControlledMutationPath `
    -Path (Join-Path $repositoryRoot 'artifacts') `
    -TrustedRoot $repositoryRoot
$publishSubdirectory = if ($FrameworkDependent) { 'portable-fxdependent\MarkUpViewMini' } else { 'portable\MarkUpViewMini' }
$zipFileName = if ($FrameworkDependent) { 'MarkUpViewMini-win-x64-fxdependent.zip' } else { 'MarkUpViewMini-win-x64.zip' }
$publishDirectory = Assert-ControlledMutationPath `
    -Path (Join-Path $artifactRoot $publishSubdirectory) `
    -TrustedRoot $repositoryRoot
$zipPath = Assert-ControlledMutationPath `
    -Path (Join-Path $artifactRoot $zipFileName) `
    -TrustedRoot $repositoryRoot
$documentNodeModules = Join-Path $repositoryRoot 'web\document-surface\node_modules'
$documentDist = Join-Path $repositoryRoot 'web\document-surface\dist'
$mermaidNodeModules = Join-Path $repositoryRoot 'web\mermaid-editor\node_modules'
$mermaidDist = Join-Path $repositoryRoot 'web\mermaid-editor\dist'
$controlledWebPaths = @($documentNodeModules, $documentDist, $mermaidNodeModules, $mermaidDist)
foreach ($controlledWebPath in $controlledWebPaths) {
    Assert-ControlledMutationPath -Path $controlledWebPath -TrustedRoot $repositoryRoot | Out-Null
}
$trackedStatus = Get-CheckedGitOutput @('status', '--porcelain', '--untracked-files=all')
if (-not [string]::IsNullOrWhiteSpace($trackedStatus)) {
    throw 'Portable release provenance requires a clean source worktree.'
}
$sourceCommit = Get-CheckedGitOutput @('rev-parse', 'HEAD')
$sourceTree = Get-CheckedGitOutput @('rev-parse', 'HEAD^{tree}')
if ($sourceCommit -notmatch '^[0-9a-f]{40}$' -or $sourceTree -notmatch '^[0-9a-f]{40}$') {
    throw 'Portable release provenance requires exact Git commit and tree object IDs.'
}
$previousPrePublish = [Environment]::GetEnvironmentVariable(
    'MARKUPVIEWMINI_PREPUBLISH_TESTS',
    [EnvironmentVariableTarget]::Process)
$previousPackageAuditRequired = [Environment]::GetEnvironmentVariable(
    'MARKUPVIEWMINI_PACKAGE_AUDIT_REQUIRED',
    [EnvironmentVariableTarget]::Process)

Push-Location $repositoryRoot
try {
    Remove-ControlledItem -Path $publishDirectory -TrustedRoot $repositoryRoot -Recurse
    Remove-ControlledItem -Path $zipPath -TrustedRoot $repositoryRoot
    New-ControlledDirectory -Path $artifactRoot -TrustedRoot $repositoryRoot | Out-Null
    New-ControlledDirectory `
        -Path (Split-Path -Parent $publishDirectory) `
        -TrustedRoot $repositoryRoot | Out-Null

    Assert-ControlledMutationPath -Path $documentNodeModules -TrustedRoot $repositoryRoot | Out-Null
    npm --prefix .\web\document-surface ci
    if ($LASTEXITCODE -ne 0) { throw 'document-surface npm ci failed.' }
    Assert-ControlledMutationPath -Path $documentNodeModules -TrustedRoot $repositoryRoot | Out-Null
    Assert-ControlledMutationPath -Path $documentDist -TrustedRoot $repositoryRoot -RejectReparseDescendants | Out-Null
    npm --prefix .\web\document-surface run check
    if ($LASTEXITCODE -ne 0) { throw 'document-surface check failed.' }
    Assert-ControlledMutationPath -Path $mermaidNodeModules -TrustedRoot $repositoryRoot | Out-Null
    npm --prefix .\web\mermaid-editor ci
    if ($LASTEXITCODE -ne 0) { throw 'mermaid-editor npm ci failed.' }
    Assert-ControlledMutationPath -Path $mermaidNodeModules -TrustedRoot $repositoryRoot | Out-Null
    Assert-ControlledMutationPath -Path $mermaidDist -TrustedRoot $repositoryRoot -RejectReparseDescendants | Out-Null
    npm --prefix .\web\mermaid-editor run check
    if ($LASTEXITCODE -ne 0) { throw 'mermaid-editor check failed.' }

    Assert-ControlledMutationPath -Path $documentNodeModules -TrustedRoot $repositoryRoot | Out-Null
    Assert-ControlledMutationPath -Path $documentDist -TrustedRoot $repositoryRoot -RejectReparseDescendants | Out-Null
    Invoke-CheckedNative npm @('--prefix', '.\web\document-surface', 'run', 'build')
    Assert-ControlledMutationPath -Path $mermaidNodeModules -TrustedRoot $repositoryRoot | Out-Null
    Assert-ControlledMutationPath -Path $mermaidDist -TrustedRoot $repositoryRoot -RejectReparseDescendants | Out-Null
    Invoke-CheckedNative npm @('--prefix', '.\web\mermaid-editor', 'run', 'build')

    [Environment]::SetEnvironmentVariable(
        'MARKUPVIEWMINI_PACKAGE_AUDIT_REQUIRED',
        $null,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        'MARKUPVIEWMINI_PREPUBLISH_TESTS',
        '1',
        [EnvironmentVariableTarget]::Process)
    Assert-RepositoryBuildMutationPaths -RepositoryRoot $repositoryRoot
    # Msix_package_contains_the_document_surface_startup_assets asserts artifacts/msix/*.msix
    # exists; that's publish-msix.ps1's own audit, not something a portable-only build produces.
    dotnet test .\MarkUpViewMini.slnx -c Release -m:1 -nr:false `
        --filter 'FullyQualifiedName!~Msix_package_contains_the_document_surface_startup_assets'
    if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }

    Assert-RepositoryBuildMutationPaths -RepositoryRoot $repositoryRoot
    Assert-ControlledMutationPath `
        -Path $publishDirectory `
        -TrustedRoot $repositoryRoot `
        -RejectReparseDescendants | Out-Null
    $publishArguments = [Collections.Generic.List[string]]::new()
    $publishArguments.AddRange([string[]] @(
        'publish',
        '.\src\MarkUpViewMini.App',
        '-c',
        'Release',
        '-r',
        'win-x64',
        '--self-contained',
        $(if ($FrameworkDependent) { 'false' } else { 'true' }),
        '-p:PublishSingleFile=false'))
    if (-not $FrameworkDependent) {
        $publishArguments.Add('-p:PublishReadyToRun=true')
    }
    $publishArguments.AddRange([string[]] @(
        '-p:DistributionKind=Portable',
        "-p:SourceRevisionId=$sourceCommit",
        '-p:ContinuousIntegrationBuild=true',
        '-o',
        $publishDirectory))
    Invoke-CheckedNative dotnet $publishArguments
    Invoke-CheckedNative node @(
        '.\scripts\generate-third-party-notices.mjs',
        '--check',
        '--deps',
        (Join-Path $publishDirectory 'MarkUpViewMini.App.deps.json'))

    Assert-ControlledMutationPath `
        -Path $publishDirectory `
        -TrustedRoot $repositoryRoot `
        -RejectReparseDescendants | Out-Null

    foreach ($debugFile in @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Force |
            Where-Object { $_.Extension -in '.map', '.pdb' })) {
        Remove-ControlledItem -Path $debugFile.FullName -TrustedRoot $repositoryRoot
    }

    $forbiddenFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Force |
        Where-Object {
            $_.Name -in 'package.json', 'package-lock.json' -or
            $_.FullName -match '[\\/](?:node_modules|src|tests)[\\/]'
        })
    if ($forbiddenFiles.Count -ne 0) {
        throw 'The portable publish contains source, tests, node_modules, or package metadata.'
    }

    $applicationDll = Join-Path $publishDirectory 'MarkUpViewMini.App.dll'
    $applicationProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
        $applicationDll).ProductVersion
    if ([string]::IsNullOrWhiteSpace($applicationProductVersion) -or
        -not $applicationProductVersion.EndsWith(
            "+$sourceCommit",
            [StringComparison]::Ordinal)) {
        throw 'The published application assembly does not embed the exact source commit.'
    }

    $provenancePath = Assert-ControlledMutationPath `
        -Path (Join-Path $publishDirectory 'release-provenance.json') `
        -TrustedRoot $repositoryRoot
    Assert-ControlledMutationPath `
        -Path $publishDirectory `
        -TrustedRoot $repositoryRoot `
        -RejectReparseDescendants | Out-Null
    $provenanceRelativePaths = [string[]] @(
        Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Force |
            Where-Object { -not $_.FullName.Equals($provenancePath, [StringComparison]::OrdinalIgnoreCase) } |
            ForEach-Object {
                [IO.Path]::GetRelativePath($publishDirectory, $_.FullName).Replace('\', '/')
            })
    [Array]::Sort($provenanceRelativePaths, [StringComparer]::Ordinal)
    $provenanceFiles = @($provenanceRelativePaths | ForEach-Object {
        $publishedFile = Get-Item -LiteralPath (Join-Path $publishDirectory $_)
        [ordered]@{
            path = $_
            length = [long] $publishedFile.Length
            sha256 = (Get-FileHash -LiteralPath $publishedFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    $provenance = [ordered]@{
        schemaVersion = 1
        sourceCommit = $sourceCommit
        sourceTree = $sourceTree
        applicationProductVersion = $applicationProductVersion
        files = $provenanceFiles
    }
    $provenance | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $provenancePath -Encoding utf8NoBOM

    [Environment]::SetEnvironmentVariable(
        'MARKUPVIEWMINI_PREPUBLISH_TESTS',
        $null,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        'MARKUPVIEWMINI_PACKAGE_AUDIT_REQUIRED',
        '1',
        [EnvironmentVariableTarget]::Process)
    Assert-RepositoryBuildMutationPaths -RepositoryRoot $repositoryRoot
    Assert-ControlledMutationPath `
        -Path $publishDirectory `
        -TrustedRoot $repositoryRoot `
        -RejectReparseDescendants | Out-Null
    Invoke-CheckedNative dotnet @(
        'test',
        '.\tests\MarkUpViewMini.App.Tests',
        '-c',
        'Release',
        '--no-restore',
        '--filter',
        'FullyQualifiedName~OfflineAssetTests&FullyQualifiedName!~Published_surfaces_render_in_production_WebViews_without_network_and_route_external_clicks&FullyQualifiedName!~Msix_package_contains_the_document_surface_startup_assets',
        '-m:1',
        '-nr:false')

    Assert-ControlledMutationPath `
        -Path $publishDirectory `
        -TrustedRoot $repositoryRoot `
        -RejectReparseDescendants | Out-Null
    Assert-ControlledMutationPath -Path $zipPath -TrustedRoot $repositoryRoot | Out-Null
    Compress-Archive `
        -Path (Join-Path $publishDirectory '*') `
        -DestinationPath $zipPath `
        -Force
    if (-not (Test-Path -LiteralPath $zipPath)) {
        throw 'The portable ZIP was not created.'
    }
    $archiveAudit = Assert-PortableArchiveMatchesDirectory `
        -PublishDirectory $publishDirectory `
        -ArchivePath $zipPath `
        -TrustedRoot $repositoryRoot

    [ordered]@{
        sourceCommit = $sourceCommit
        sourceTree = $sourceTree
        provenanceSha256 = (Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256).Hash.ToLowerInvariant()
        portableFileCount = $provenanceFiles.Count
        zipFileCount = $archiveAudit.fileCount
        zipSha256 = $archiveAudit.sha256
        zipPortableMarkerCount = $archiveAudit.portableMarkerCount
    } | ConvertTo-Json
}
finally {
    [Environment]::SetEnvironmentVariable(
        'MARKUPVIEWMINI_PREPUBLISH_TESTS',
        $previousPrePublish,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        'MARKUPVIEWMINI_PACKAGE_AUDIT_REQUIRED',
        $previousPackageAuditRequired,
        [EnvironmentVariableTarget]::Process)
    Pop-Location
}
