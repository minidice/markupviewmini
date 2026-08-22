<#
    Clears every portable-build artifact (both channels, both zips) up front so the
    "포터블 빌드" sequence (publish-portable.ps1, then again with -FrameworkDependent)
    starts from a known-empty state. artifacts/msix is intentionally left untouched —
    the MSIX build owns that folder and cleans it independently in publish-msix.ps1.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleasePathSafety.psm1') -Force

$repositoryRoot = Get-VerifiedPhysicalRoot -Root (Join-Path $PSScriptRoot '..')
$artifactRoot = Assert-ControlledMutationPath `
    -Path (Join-Path $repositoryRoot 'artifacts') `
    -TrustedRoot $repositoryRoot

Remove-ControlledItem -Path (Join-Path $artifactRoot 'portable') -TrustedRoot $repositoryRoot -Recurse
Remove-ControlledItem -Path (Join-Path $artifactRoot 'portable-fxdependent') -TrustedRoot $repositoryRoot -Recurse
Remove-ControlledItem -Path (Join-Path $artifactRoot 'MarkUpViewMini-win-x64.zip') -TrustedRoot $repositoryRoot
Remove-ControlledItem -Path (Join-Path $artifactRoot 'MarkUpViewMini-win-x64-fxdependent.zip') -TrustedRoot $repositoryRoot

Write-Host 'Portable artifacts cleared (artifacts/msix left untouched).' -ForegroundColor Green
