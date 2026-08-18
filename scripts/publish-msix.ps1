[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleasePathSafety.psm1') -Force

function Get-CheckedGitOutput {
    param([Parameter(Mandatory)] [string[]] $ArgumentList)

    $output = & git -C $repositoryRoot @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Git provenance query failed: git $($ArgumentList -join ' ')"
    }

    return ($output -join [Environment]::NewLine).Trim()
}

function Find-MSBuildPath {
    $onPath = Get-Command 'msbuild.exe' -ErrorAction SilentlyContinue
    if ($null -ne $onPath) {
        return $onPath.Source
    }

    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'MSBuild.exe was not found on PATH and vswhere.exe is not present to locate a Visual Studio install.'
    }

    $installationPath = (& $vswhere -latest -prerelease -products '*' `
        -requires Microsoft.Component.MSBuild -property installationPath) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($installationPath)) {
        throw 'vswhere.exe could not find a Visual Studio installation with the MSBuild component.'
    }

    $candidate = Join-Path $installationPath 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "MSBuild.exe was not found at the expected path '$candidate'."
    }

    return $candidate
}

function Get-OrCreateMsixTestCertificate {
    param([Parameter(Mandatory)] [string] $Subject)

    $existing = Get-ChildItem -Path 'Cert:\CurrentUser\My' |
        Where-Object {
            $_.Subject -eq $Subject -and
            $_.FriendlyName -eq 'MarkUpViewMini MSIX sideload test certificate' -and
            $_.NotAfter -gt (Get-Date)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if ($null -ne $existing) {
        return $existing
    }

    return New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Subject `
        -KeyUsage DigitalSignature `
        -FriendlyName 'MarkUpViewMini MSIX sideload test certificate' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(1) `
        -TextExtension @(
            '2.5.29.37={text}1.3.6.1.5.5.7.3.3',
            '2.5.29.19={text}Subject Type:End Entity')
}

$repositoryRoot = Get-VerifiedPhysicalRoot -Root (Join-Path $PSScriptRoot '..')
$artifactRoot = Assert-ControlledMutationPath `
    -Path (Join-Path $repositoryRoot 'artifacts') `
    -TrustedRoot $repositoryRoot
$msixOutputDirectory = Assert-ControlledMutationPath `
    -Path (Join-Path $artifactRoot 'msix') `
    -TrustedRoot $repositoryRoot
$msixPath = Assert-ControlledMutationPath `
    -Path (Join-Path $msixOutputDirectory 'MarkUpViewMini-win-x64.msix') `
    -TrustedRoot $repositoryRoot
$wapprojPath = Assert-ControlledMutationPath `
    -Path (Join-Path $repositoryRoot 'src\MarkUpViewMini.App.Package\MarkUpViewMini.App.Package.wapproj') `
    -TrustedRoot $repositoryRoot
$manifestPath = Join-Path $repositoryRoot 'src\MarkUpViewMini.App.Package\Package.appxmanifest'

$trackedStatus = Get-CheckedGitOutput @('status', '--porcelain', '--untracked-files=all')
if (-not [string]::IsNullOrWhiteSpace($trackedStatus)) {
    throw 'MSIX release provenance requires a clean source worktree.'
}
$sourceCommit = Get-CheckedGitOutput @('rev-parse', 'HEAD')
$sourceTree = Get-CheckedGitOutput @('rev-parse', 'HEAD^{tree}')
if ($sourceCommit -notmatch '^[0-9a-f]{40}$' -or $sourceTree -notmatch '^[0-9a-f]{40}$') {
    throw 'MSIX release provenance requires exact Git commit and tree object IDs.'
}

$manifestXml = [xml] (Get-Content -LiteralPath $manifestPath -Raw)
$identitySubject = $manifestXml.Package.Identity.Publisher
if ([string]::IsNullOrWhiteSpace($identitySubject)) {
    throw 'Package.appxmanifest is missing an Identity/Publisher value to sign the test certificate with.'
}

Push-Location $repositoryRoot
try {
    Remove-ControlledItem -Path $msixOutputDirectory -TrustedRoot $repositoryRoot -Recurse
    New-ControlledDirectory -Path $msixOutputDirectory -TrustedRoot $repositoryRoot | Out-Null

    $msbuildPath = Find-MSBuildPath
    $certificate = Get-OrCreateMsixTestCertificate -Subject $identitySubject

    Assert-RepositoryBuildMutationPaths -RepositoryRoot $repositoryRoot
    Assert-ControlledMutationPath `
        -Path $msixOutputDirectory `
        -TrustedRoot $repositoryRoot `
        -RejectReparseDescendants | Out-Null
    & $msbuildPath $wapprojPath `
        '/restore' `
        '/p:Configuration=Release' `
        '/p:Platform=x64' `
        '/p:AppxBundle=Never' `
        '/p:UapAppxPackageBuildMode=SideLoadOnly' `
        '/p:AppxPackageSigningEnabled=true' `
        "/p:PackageCertificateThumbprint=$($certificate.Thumbprint)" `
        "/p:AppxPackageOutput=$msixPath" `
        "-p:SourceRevisionId=$sourceCommit"
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed to produce the MSIX package with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $msixPath -PathType Leaf)) {
        throw 'The MSIX package was not created.'
    }

    [ordered]@{
        sourceCommit = $sourceCommit
        sourceTree = $sourceTree
        msixPath = $msixPath
        msixSha256 = (Get-FileHash -LiteralPath $msixPath -Algorithm SHA256).Hash.ToLowerInvariant()
        signingCertificateThumbprint = $certificate.Thumbprint
        signingCertificateSubject = $identitySubject
    } | ConvertTo-Json
}
finally {
    Pop-Location
}
