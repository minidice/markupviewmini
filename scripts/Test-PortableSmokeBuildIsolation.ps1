[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleasePathSafety.psm1') -Force

function Get-MarkerState {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{
            exists = $false
            bytes = $null
            lastWriteTimeUtc = $null
            attributes = $null
        }
    }

    $item = Get-Item -LiteralPath $Path -Force
    [pscustomobject]@{
        exists = $true
        bytes = [IO.File]::ReadAllBytes($Path)
        lastWriteTimeUtc = $item.LastWriteTimeUtc
        attributes = $item.Attributes
    }
}

function Set-MarkerState {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)] $State,
        [Parameter(Mandatory)][string] $RepositoryRoot
    )

    $controlledPath = Assert-ControlledMutationPath -Path $Path -TrustedRoot $RepositoryRoot
    if (-not $State.exists) {
        if (Test-Path -LiteralPath $controlledPath) {
            Remove-ControlledItem -Path $controlledPath -TrustedRoot $RepositoryRoot
        }
        return
    }

    [IO.File]::WriteAllBytes($controlledPath, [byte[]] $State.bytes)
    [IO.File]::SetLastWriteTimeUtc($controlledPath, [DateTime] $State.lastWriteTimeUtc)
    [IO.File]::SetAttributes($controlledPath, [IO.FileAttributes] $State.attributes)
}

function Assert-MarkerState {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)] $Expected,
        [Parameter(Mandatory)][string] $CaseName
    )

    $actual = Get-MarkerState -Path $Path
    if ($actual.exists -ne $Expected.exists) {
        throw "$CaseName changed the shared marker's existence."
    }
    if (-not $Expected.exists) {
        return
    }

    if (-not [Linq.Enumerable]::SequenceEqual(
            [byte[]] $actual.bytes,
            [byte[]] $Expected.bytes)) {
        throw "$CaseName changed the shared marker's bytes."
    }
    if ($actual.lastWriteTimeUtc -ne $Expected.lastWriteTimeUtc) {
        throw "$CaseName changed the shared marker's last-write time."
    }
    if ($actual.attributes -ne $Expected.attributes) {
        throw "$CaseName changed the shared marker's attributes."
    }
}

function Get-SmokeRoots {
    param([Parameter(Mandatory)][string] $TempRoot)

    @(
        Get-ChildItem -LiteralPath $TempRoot -Directory |
            Where-Object {
                $_.Name.StartsWith('MarkUpViewMini-ShortcutBuild-', [StringComparison]::Ordinal) -or
                $_.Name.StartsWith('MarkUpViewMini-ActivationSmoke-', [StringComparison]::Ordinal)
            } |
            ForEach-Object { $_.FullName }
    )
}

function Start-OwnedBlockingAppProcess {
    param(
        [Parameter(Mandatory)][string] $TestRoot,
        [Parameter(Mandatory)][string] $TempRoot
    )

    $dummyExecutable = Assert-ControlledMutationPath `
        -Path (Join-Path $TestRoot 'MarkUpViewMini.App.exe') `
        -TrustedRoot $TempRoot
    Copy-Item -LiteralPath "$env:SystemRoot\System32\ping.exe" -Destination $dummyExecutable -Force
    $process = Start-Process `
        -FilePath $dummyExecutable `
        -ArgumentList @('127.0.0.1', '-n', '120') `
        -WorkingDirectory $TestRoot `
        -WindowStyle Hidden `
        -PassThru
    Start-Sleep -Milliseconds 250
    if (@(Get-Process -Name 'MarkUpViewMini.App' -ErrorAction Stop |
            Where-Object Id -eq $process.Id).Count -ne 1) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw 'The owned blocking-process precondition failed.'
    }

    $process
}

function Stop-OwnedBlockingAppProcess {
    param([Diagnostics.Process] $Process)

    if ($null -eq $Process) {
        return
    }

    $Process.Refresh()
    if (-not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        $Process.WaitForExit(5000) | Out-Null
    }
}

$repositoryRoot = Get-VerifiedPhysicalRoot -Root (Join-Path $PSScriptRoot '..')
$tempRoot = Get-VerifiedPhysicalRoot -Root ([IO.Path]::GetTempPath())
$testRoot = Assert-ControlledMutationPath `
    -Path (Join-Path $tempRoot "MarkUpViewMini-SmokeIsolationTest-$([Guid]::NewGuid().ToString('N'))") `
    -TrustedRoot $tempRoot
if (-not [IO.Path]::GetFileName($testRoot).StartsWith(
        'MarkUpViewMini-SmokeIsolationTest-',
        [StringComparison]::Ordinal)) {
    throw 'The smoke-isolation test root does not have the owned prefix.'
}

$sharedMarker = Assert-ControlledMutationPath `
    -Path (Join-Path $repositoryRoot 'src\MarkUpViewMini.App\bin\Release\net10.0-windows\portable.marker') `
    -TrustedRoot $repositoryRoot
$shortcutScript = Join-Path $repositoryRoot 'scripts\Invoke-ShortcutSmoke.ps1'
$activationScript = Join-Path $repositoryRoot 'tests\MarkUpViewMini.App.Tests\Activation\Run-TwoProcessActivationSmoke.ps1'
$originalMarker = Get-MarkerState -Path $sharedMarker
$absentMarker = [pscustomobject]@{
    exists = $false
    bytes = $null
    lastWriteTimeUtc = $null
    attributes = $null
}
$sentinelMarker = [pscustomobject]@{
    exists = $true
    bytes = [Text.Encoding]::UTF8.GetBytes('owned shared-marker sentinel')
    lastWriteTimeUtc = [DateTime]::SpecifyKind([DateTime]::new(2000, 1, 2, 3, 4, 5), [DateTimeKind]::Utc)
    attributes = [IO.FileAttributes]::Normal
}
$cases = @(
    [pscustomobject]@{ name = 'shortcut success'; smoke = 'shortcut'; marker = $absentMarker; expectFailure = $false },
    [pscustomobject]@{ name = 'shortcut failure'; smoke = 'shortcut'; marker = $sentinelMarker; expectFailure = $true },
    [pscustomobject]@{ name = 'activation success'; smoke = 'activation'; marker = $sentinelMarker; expectFailure = $false },
    [pscustomobject]@{ name = 'activation failure'; smoke = 'activation'; marker = $absentMarker; expectFailure = $true }
)

$failures = [Collections.Generic.List[Exception]]::new()
try {
    New-ControlledDirectory -Path $testRoot -TrustedRoot $tempRoot | Out-Null
    foreach ($case in $cases) {
        $blockingProcess = $null
        try {
            Set-MarkerState -Path $sharedMarker -State $case.marker -RepositoryRoot $repositoryRoot
            $expectedMarker = Get-MarkerState -Path $sharedMarker
            $preexistingSmokeRoots = @(Get-SmokeRoots -TempRoot $tempRoot)
            if ($case.expectFailure) {
                $blockingProcess = Start-OwnedBlockingAppProcess -TestRoot $testRoot -TempRoot $tempRoot
            }

            $caught = $null
            try {
                if ($case.smoke -eq 'shortcut') {
                    & $shortcutScript -EvidencePath (Join-Path $testRoot "$($case.name.Replace(' ', '-')).json")
                } else {
                    & $activationScript `
                        -Configuration Release `
                        -ArtifactPath (Join-Path $testRoot "$($case.name.Replace(' ', '-')).json")
                }
            }
            catch {
                $caught = $_.Exception
            }

            if ($case.expectFailure) {
                if ($null -eq $caught -or
                    -not $caught.Message.StartsWith('Close existing MarkUpViewMini.App processes', [StringComparison]::Ordinal)) {
                    throw "$($case.name) did not reach the controlled post-build failure."
                }
            } elseif ($null -ne $caught) {
                throw $caught
            }

            Assert-MarkerState -Path $sharedMarker -Expected $expectedMarker -CaseName $case.name
            $newSmokeRoots = @(
                Get-SmokeRoots -TempRoot $tempRoot |
                    Where-Object { $preexistingSmokeRoots -notcontains $_ }
            )
            if ($newSmokeRoots.Count -ne 0) {
                throw "$($case.name) left owned build residue: $($newSmokeRoots -join ', ')."
            }

            Write-Host "PASS: $($case.name) preserved the shared marker and cleaned owned output."
        }
        catch {
            $failures.Add([InvalidOperationException]::new(
                "$($case.name): $($_.Exception.Message)",
                $_.Exception))
        }
        finally {
            Stop-OwnedBlockingAppProcess -Process $blockingProcess
        }
    }
}
finally {
    Set-MarkerState -Path $sharedMarker -State $originalMarker -RepositoryRoot $repositoryRoot
    if (Test-Path -LiteralPath $testRoot) {
        Remove-ControlledItem -Path $testRoot -TrustedRoot $tempRoot -Recurse
    }
}

Assert-MarkerState -Path $sharedMarker -Expected $originalMarker -CaseName 'test cleanup'

if ($failures.Count -ne 0) {
    throw [AggregateException]::new(
        "$($failures.Count) portable smoke build-isolation cases failed.",
        $failures)
}

Write-Host "Portable smoke build isolation passed: $($cases.Count) cases."
