[CmdletBinding()]
param(
    [string] $EvidencePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleasePathSafety.psm1') -Force
$repositoryRoot = Get-VerifiedPhysicalRoot -Root (Join-Path $PSScriptRoot '..')
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Join-Path $repositoryRoot '.superpowers\sdd\2026-08-12-markup-view-mini-phase-5-windows-release\task-3-smoke-evidence.json'
}

$applicationProject = Join-Path $repositoryRoot 'src\MarkUpViewMini.App\MarkUpViewMini.App.csproj'
$testProject = Join-Path $repositoryRoot 'tests\MarkUpViewMini.Infrastructure.Tests\MarkUpViewMini.Infrastructure.Tests.csproj'
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$tempRoot = Get-VerifiedPhysicalRoot -Root $tempRoot
$buildRoot = Assert-ControlledMutationPath `
    -Path (Join-Path $tempRoot "MarkUpViewMini-ShortcutBuild-$([Guid]::NewGuid().ToString('N'))") `
    -TrustedRoot $tempRoot
if (-not [IO.Path]::GetFileName($buildRoot).StartsWith(
        'MarkUpViewMini-ShortcutBuild-',
        [StringComparison]::Ordinal)) {
    throw 'The shortcut build directory does not have the owned prefix.'
}
$applicationExe = Join-Path $buildRoot 'MarkUpViewMini.App.exe'
$failures = [System.Collections.Generic.List[System.Exception]]::new()
try {
    New-ControlledDirectory -Path $buildRoot -TrustedRoot $tempRoot | Out-Null
    Assert-RepositoryBuildMutationPaths -RepositoryRoot $repositoryRoot
    dotnet build $applicationProject `
        -c Release `
        --no-restore `
        --nologo `
        -m:1 `
        -nr:false `
        -o $buildRoot `
        -p:DistributionKind=Portable
    if ($LASTEXITCODE -ne 0) {
        throw 'The Release Portable application build failed.'
    }

    if (-not (Test-Path -LiteralPath $applicationExe -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $buildRoot 'portable.marker') -PathType Leaf)) {
        throw 'The Release Portable application build is incomplete.'
    }
    Assert-ControlledMutationPath `
        -Path $buildRoot `
        -TrustedRoot $tempRoot `
        -RejectReparseDescendants | Out-Null

    $programsPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
    $desktopPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    $startMenuLink = Join-Path $programsPath 'MarkUpViewMini.lnk'
    $desktopLink = Join-Path $desktopPath 'MarkUpViewMini.lnk'
    if ((Test-Path -LiteralPath $startMenuLink) -or (Test-Path -LiteralPath $desktopLink)) {
        throw 'A pre-existing exact MarkUpViewMini shortcut path prevents the guarded smoke test.'
    }

    if (@(Get-Process -Name 'MarkUpViewMini.App' -ErrorAction SilentlyContinue).Count -ne 0) {
        throw 'Close existing MarkUpViewMini.App processes before running the guarded shortcut smoke test.'
    }

    $preexistingSmokeTempPaths = @(
        Get-ChildItem -LiteralPath $tempRoot -Directory -Filter 'MarkUpViewMini-ShortcutSmoke-*' |
            ForEach-Object { $_.FullName }
    )

    $env:MARKUPVIEWMINI_RUN_SHORTCUT_SMOKE = '1'
    $env:MARKUPVIEWMINI_SHORTCUT_EXE = $applicationExe
    $env:MARKUPVIEWMINI_SHORTCUT_EVIDENCE = [System.IO.Path]::GetFullPath($EvidencePath)
    try {
        Assert-RepositoryBuildMutationPaths -RepositoryRoot $repositoryRoot
        dotnet test $testProject `
            -c Release `
            --no-restore `
            --filter 'FullyQualifiedName~ShellLinkRealShortcutSmokeTests' `
            --nologo `
            -m:1 `
            -nr:false
        if ($LASTEXITCODE -ne 0) {
            $failures.Add([System.InvalidOperationException]::new(
                "The real current-user shortcut smoke failed with exit code $LASTEXITCODE."))
        }
    }
    catch {
        $failures.Add($_.Exception)
    }
    finally {
        Remove-Item Env:MARKUPVIEWMINI_RUN_SHORTCUT_SMOKE -ErrorAction SilentlyContinue
        Remove-Item Env:MARKUPVIEWMINI_SHORTCUT_EXE -ErrorAction SilentlyContinue
        Remove-Item Env:MARKUPVIEWMINI_SHORTCUT_EVIDENCE -ErrorAction SilentlyContinue
    }

    try {
        $residue = [System.Collections.Generic.List[string]]::new()
        if (Test-Path -LiteralPath $startMenuLink) {
            $residue.Add('Start Menu shortcut')
        }

        if (Test-Path -LiteralPath $desktopLink) {
            $residue.Add('Desktop shortcut')
        }

        if (@(Get-Process -Name 'MarkUpViewMini.App' -ErrorAction SilentlyContinue).Count -ne 0) {
            $residue.Add('MarkUpViewMini.App process')
        }

        $newSmokeTempPaths = @(
            Get-ChildItem -LiteralPath $tempRoot -Directory -Filter 'MarkUpViewMini-ShortcutSmoke-*' |
                Where-Object { $preexistingSmokeTempPaths -notcontains $_.FullName }
        )
        if ($newSmokeTempPaths.Count -ne 0) {
            $residue.Add('shortcut smoke temp directory')
        }

        if ($residue.Count -ne 0) {
            $failures.Add([System.InvalidOperationException]::new(
                "Shortcut smoke cleanup verification failed: $($residue -join ', ')."))
        }
    }
    catch {
        $failures.Add($_.Exception)
    }
}
catch {
    $failures.Add($_.Exception)
}
finally {
    Remove-Item Env:MARKUPVIEWMINI_RUN_SHORTCUT_SMOKE -ErrorAction SilentlyContinue
    Remove-Item Env:MARKUPVIEWMINI_SHORTCUT_EXE -ErrorAction SilentlyContinue
    Remove-Item Env:MARKUPVIEWMINI_SHORTCUT_EVIDENCE -ErrorAction SilentlyContinue
    try {
        if (Test-Path -LiteralPath $buildRoot) {
            Remove-ControlledItem -Path $buildRoot -TrustedRoot $tempRoot -Recurse
        }
    }
    catch {
        $failures.Add([InvalidOperationException]::new(
            'The owned shortcut build directory could not be removed.',
            $_.Exception))
    }
}

if (Test-Path -LiteralPath $buildRoot) {
    $failures.Add([InvalidOperationException]::new(
        'The owned shortcut build directory remains after cleanup.'))
}

if ($failures.Count -eq 1) {
    throw $failures[0]
}

if ($failures.Count -gt 1) {
    throw [System.AggregateException]::new(
        'The shortcut smoke and/or its cleanup verification failed.',
        $failures)
}
