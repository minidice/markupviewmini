[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-CheckedGitOutput {
    param([Parameter(Mandatory)] [string[]] $ArgumentList)

    $output = & git -C $repositoryRoot @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Git guard probe failed: git $($ArgumentList -join ' ')"
    }

    ($output -join [Environment]::NewLine).Trim()
}

function Invoke-NativeProbe {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $ArgumentList
    )

    $output = & $FilePath @ArgumentList 2>&1 | Out-String
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

function Assert-GuardRejected {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [pscustomobject] $Probe,
        [Parameter(Mandatory)] [string] $ExpectedOutput
    )

    if ($Probe.ExitCode -eq 0 -or
        -not $Probe.Output.Contains($ExpectedOutput, [StringComparison]::Ordinal)) {
        throw "$Name did not reject the controlled untracked build input before build. Exit=$($Probe.ExitCode)`n$($Probe.Output)"
    }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sentinelPath = [IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'src\MarkUpViewMini.Core\Task6UntrackedBuildInputSentinel.cs'))
$provenancePath = Join-Path $repositoryRoot 'artifacts\portable\MarkUpViewMini\release-provenance.json'
$probeOutputPath = Join-Path $repositoryRoot 'artifacts\benchmark-r2-guard-probe.json'
$initialStatus = Get-CheckedGitOutput @('status', '--porcelain', '--untracked-files=all')
if (-not [string]::IsNullOrWhiteSpace($initialStatus)) {
    throw 'Run the untracked-source guard probe from an exact clean worktree.'
}
if (Test-Path -LiteralPath $sentinelPath) {
    throw 'The controlled untracked build-input sentinel already exists.'
}
if (Test-Path -LiteralPath $probeOutputPath) {
    throw 'The controlled guard-probe benchmark output already exists.'
}
if (-not (Test-Path -LiteralPath $provenancePath)) {
    throw 'Publish the exact clean code commit before running the untracked-source guard probe.'
}

$provenance = Get-Content -Raw -LiteralPath $provenancePath | ConvertFrom-Json
$currentCommit = Get-CheckedGitOutput @('rev-parse', 'HEAD')
$currentTree = Get-CheckedGitOutput @('rev-parse', 'HEAD^{tree}')
if ($provenance.sourceCommit -ne $currentCommit -or $provenance.sourceTree -ne $currentTree) {
    throw 'The portable artifact must be bound to the exact clean probe commit.'
}

$previousPackageAuditRequired = $env:MARKUPVIEWMINI_PACKAGE_AUDIT_REQUIRED
try {
    Set-Content -LiteralPath $sentinelPath `
        -Value '#error TASK6_UNTRACKED_BUILD_INPUT_SENTINEL_MUST_BE_REJECTED_BEFORE_BUILD' `
        -Encoding utf8NoBOM
    $sentinelStatus = Get-CheckedGitOutput @('status', '--porcelain', '--untracked-files=all')
    if (-not $sentinelStatus.Contains(
        '?? src/MarkUpViewMini.Core/Task6UntrackedBuildInputSentinel.cs',
        [StringComparison]::Ordinal)) {
        throw 'Git did not report the controlled sentinel as an untracked build input.'
    }

    $publish = Invoke-NativeProbe 'pwsh' @(
        '-NoProfile',
        '-File',
        (Join-Path $repositoryRoot 'scripts\publish-portable.ps1'))
    Assert-GuardRejected `
        -Name 'publish-portable.ps1' `
        -Probe $publish `
        -ExpectedOutput 'Portable release provenance requires a clean source worktree.'

    $benchmark = Invoke-NativeProbe 'pwsh' @(
        '-NoProfile',
        '-File',
        (Join-Path $repositoryRoot 'scripts\benchmark.ps1'),
        '-Configuration',
        'Release',
        '-Output',
        '.\artifacts\benchmark-r2-guard-probe.json')
    Assert-GuardRejected `
        -Name 'benchmark.ps1' `
        -Probe $benchmark `
        -ExpectedOutput 'The portable publish is not bound to the exact clean benchmark source commit.'

    $env:MARKUPVIEWMINI_PACKAGE_AUDIT_REQUIRED = '1'
    $offlineAudit = Invoke-NativeProbe 'dotnet' @(
        'test',
        (Join-Path $repositoryRoot 'tests\MarkUpViewMini.App.Tests\MarkUpViewMini.App.Tests.csproj'),
        '-c',
        'Release',
        '--no-build',
        '--no-restore',
        '--filter',
        'FullyQualifiedName~Portable_publish_provenance_binds_every_file_and_embedded_revision_to_the_clean_commit',
        '-m:1',
        '-nr:false')
    Assert-GuardRejected `
        -Name 'OfflineAssetTests provenance audit' `
        -Probe $offlineAudit `
        -ExpectedOutput 'Portable provenance requires an exact clean source worktree.'
}
finally {
    $env:MARKUPVIEWMINI_PACKAGE_AUDIT_REQUIRED = $previousPackageAuditRequired
    if (Test-Path -LiteralPath $sentinelPath) {
        Remove-Item -LiteralPath $sentinelPath -Force
    }
    if (Test-Path -LiteralPath $probeOutputPath) {
        Remove-Item -LiteralPath $probeOutputPath -Force
    }
}

$finalStatus = Get-CheckedGitOutput @('status', '--porcelain', '--untracked-files=all')
if ($finalStatus -ne $initialStatus) {
    throw "The untracked-source guard probe left worktree residue.`n$finalStatus"
}
if (Test-Path -LiteralPath $sentinelPath) {
    throw 'The controlled untracked build-input sentinel remained after the guard probe.'
}

[ordered]@{
    publishGuard = 'PASS'
    benchmarkGuard = 'PASS'
    offlineAuditGuard = 'PASS'
    sentinelResidue = $false
} | ConvertTo-Json
