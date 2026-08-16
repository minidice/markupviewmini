[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleasePathSafety.psm1') -Force

function Invoke-AppPublish {
    param(
        [Parameter(Mandatory)] [string] $OutputDirectory,
        [string] $DistributionKind
    )

    $arguments = [Collections.Generic.List[string]]::new()
    $arguments.Add('publish')
    $arguments.Add($applicationProject)
    $arguments.Add('--configuration')
    $arguments.Add($Configuration)
    $arguments.Add('--no-restore')
    $arguments.Add('--nologo')
    $arguments.Add('-m:1')
    $arguments.Add('-nr:false')
    $arguments.Add('-p:PublishSingleFile=false')
    $arguments.Add('-p:PublishReadyToRun=false')
    $arguments.Add('-p:SelfContained=false')
    if (-not [string]::IsNullOrWhiteSpace($DistributionKind)) {
        $arguments.Add("-p:DistributionKind=$DistributionKind")
    }
    $arguments.Add('-o')
    $arguments.Add($OutputDirectory)

    Assert-RepositoryBuildMutationPaths -RepositoryRoot $repositoryRoot
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "The $DistributionKind distribution artifact publish failed with exit code $LASTEXITCODE."
    }
}

$repositoryRoot = Get-VerifiedPhysicalRoot -Root (Join-Path $PSScriptRoot '..')
$applicationProject = Assert-ControlledMutationPath `
    -Path (Join-Path $repositoryRoot 'src\MarkUpViewMini.App\MarkUpViewMini.App.csproj') `
    -TrustedRoot $repositoryRoot
$defaultBuildOutput = Assert-ControlledMutationPath `
    -Path (Join-Path $repositoryRoot "src\MarkUpViewMini.App\bin\$Configuration\net10.0-windows") `
    -TrustedRoot $repositoryRoot
$tempRoot = Get-VerifiedPhysicalRoot -Root ([IO.Path]::GetTempPath())
$runRoot = Assert-ControlledMutationPath `
    -Path (Join-Path $tempRoot "MarkUpViewMini-DistributionArtifacts-$([Guid]::NewGuid().ToString('N'))") `
    -TrustedRoot $tempRoot
$defaultPublish = Assert-ControlledMutationPath `
    -Path (Join-Path $runRoot 'default') `
    -TrustedRoot $tempRoot
$transitionPublish = Assert-ControlledMutationPath `
    -Path (Join-Path $runRoot 'transition') `
    -TrustedRoot $tempRoot

try {
    New-ControlledDirectory -Path $runRoot -TrustedRoot $tempRoot | Out-Null
    Invoke-AppPublish -OutputDirectory $defaultPublish
    if (Test-Path -LiteralPath (Join-Path $defaultPublish 'portable.marker')) {
        throw 'An ordinary publish incorrectly contains portable.marker.'
    }
    if (Test-Path -LiteralPath (Join-Path $defaultBuildOutput 'portable.marker')) {
        throw 'A default build output incorrectly contains portable.marker.'
    }

    Invoke-AppPublish -OutputDirectory $transitionPublish -DistributionKind 'Portable'
    if (-not (Test-Path -LiteralPath (Join-Path $transitionPublish 'portable.marker') -PathType Leaf)) {
        throw 'The explicit Portable publish is missing portable.marker.'
    }

    Invoke-AppPublish -OutputDirectory $transitionPublish -DistributionKind 'Installed'
    if (Test-Path -LiteralPath (Join-Path $transitionPublish 'portable.marker')) {
        throw 'An Installed republish left a stale portable.marker in the publish output.'
    }
    if (Test-Path -LiteralPath (Join-Path $defaultBuildOutput 'portable.marker')) {
        throw 'An Installed republish left a stale portable.marker in the build output.'
    }

    [ordered]@{
        defaultBuildMarkerAbsent = $true
        defaultPublishMarkerAbsent = $true
        explicitPortableMarkerPresent = $true
        installedRepublishRemovedStaleMarker = $true
    } | ConvertTo-Json
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        Remove-ControlledItem -Path $runRoot -TrustedRoot $tempRoot -Recurse
    }
}
