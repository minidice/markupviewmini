[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $ArtifactPath = '.superpowers\sdd\2026-08-12-markup-view-mini-phase-5-windows-release\task-1-smoke.json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-ForMainWindow {
    param(
        [System.Diagnostics.Process] $Process,
        [TimeSpan] $Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "The primary exited before creating a WPF window."
        }

        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return [long] $Process.MainWindowHandle
        }

        Start-Sleep -Milliseconds 50
    }

    throw "The primary did not create a WPF window within $($Timeout.TotalSeconds) seconds."
}

function Wait-ForWpfTabCount {
    param(
        [long] $MainWindowHandle,
        [int] $ExpectedCount,
        [TimeSpan] $Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    while ([DateTime]::UtcNow -lt $deadline) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle(
            [IntPtr] $MainWindowHandle)
        $tabs = $root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                'TabsList'))
        if ($null -ne $tabs) {
            $items = $tabs.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::ListItem))
            if ($items.Count -eq $ExpectedCount) {
                return $items.Count
            }
        }

        Start-Sleep -Milliseconds 50
    }

    throw "The primary did not expose $ExpectedCount WPF tabs within $($Timeout.TotalSeconds) seconds."
}

function Stop-OwnedProcess {
    param([System.Diagnostics.Process] $Process)

    try {
        $Process.Refresh()
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force
            $Process.WaitForExit(5000) | Out-Null
        }
    }
    catch {
        # Cleanup is best effort for a process that may have exited between Refresh and Stop-Process.
    }
}

function Write-SmokeArtifact {
    param(
        [System.Collections.IDictionary] $Value,
        [string] $Path
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $Value | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
Import-Module (Join-Path $repositoryRoot 'scripts\ReleasePathSafety.psm1') -Force
$repositoryRoot = Get-VerifiedPhysicalRoot -Root $repositoryRoot
$applicationProject = Join-Path $repositoryRoot 'src\MarkUpViewMini.App\MarkUpViewMini.App.csproj'
$artifactFullPath = if ([IO.Path]::IsPathRooted($ArtifactPath)) {
    [IO.Path]::GetFullPath($ArtifactPath)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ArtifactPath))
}
$tempRoot = Get-VerifiedPhysicalRoot -Root ([IO.Path]::GetTempPath())
$runRoot = Assert-ControlledMutationPath `
    -Path (Join-Path $tempRoot "MarkUpViewMini-ActivationSmoke-$([Guid]::NewGuid().ToString('N'))") `
    -TrustedRoot $tempRoot
if (-not $runRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetFileName($runRoot).StartsWith('MarkUpViewMini-ActivationSmoke-', [StringComparison]::Ordinal)) {
    throw "The smoke run directory is outside the verified temporary root."
}
$buildOutput = $runRoot
$sourceExecutable = Join-Path $buildOutput 'MarkUpViewMini.App.exe'

$primary = $null
$secondary = $null
$result = $null
$failure = $null
$cleanupFailed = $false
try {
    New-ControlledDirectory -Path $runRoot -TrustedRoot $tempRoot | Out-Null
    Assert-RepositoryBuildMutationPaths -RepositoryRoot $repositoryRoot
    & dotnet build $applicationProject `
        --configuration $Configuration `
        --no-restore `
        --nologo `
        -m:1 `
        -nr:false `
        -o $buildOutput `
        -p:DistributionKind=Portable
    if ($LASTEXITCODE -ne 0) {
        throw "The $Configuration Portable application build failed."
    }

    if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $buildOutput 'portable.marker') -PathType Leaf)) {
        throw "The $Configuration Portable application build is incomplete."
    }
    Assert-ControlledMutationPath `
        -Path $buildOutput `
        -TrustedRoot $tempRoot `
        -RejectReparseDescendants | Out-Null

    $existingApplications = @(Get-Process -Name 'MarkUpViewMini.App' -ErrorAction SilentlyContinue)
    if ($existingApplications.Count -ne 0) {
        throw "Close existing MarkUpViewMini.App processes before running the isolated smoke test."
    }

    $copiedData = Assert-ControlledMutationPath `
        -Path (Join-Path $runRoot 'data') `
        -TrustedRoot $tempRoot
    if (Test-Path -LiteralPath $copiedData) {
        Remove-ControlledItem -Path $copiedData -TrustedRoot $tempRoot -Recurse
    }
    $copiedPortableDataRemoved = -not (Test-Path -LiteralPath $copiedData)
    $fixtures = Assert-ControlledMutationPath `
        -Path (Join-Path $runRoot 'fixtures') `
        -TrustedRoot $tempRoot
    New-ControlledDirectory -Path $fixtures -TrustedRoot $tempRoot | Out-Null
    $initialPath = Assert-ControlledMutationPath -Path (Join-Path $fixtures 'initial-clean.md') -TrustedRoot $tempRoot
    $firstForwardedPath = Assert-ControlledMutationPath -Path (Join-Path $fixtures 'forwarded-one.md') -TrustedRoot $tempRoot
    $secondForwardedPath = Assert-ControlledMutationPath -Path (Join-Path $fixtures 'forwarded-two.md') -TrustedRoot $tempRoot
    Set-Content -LiteralPath $initialPath -Value '# initial' -Encoding utf8NoBOM
    Set-Content -LiteralPath $firstForwardedPath -Value '# forwarded one' -Encoding utf8NoBOM
    Set-Content -LiteralPath $secondForwardedPath -Value '# forwarded two' -Encoding utf8NoBOM
    $expectedPaths = @($initialPath, $firstForwardedPath, $secondForwardedPath)
    $executable = $sourceExecutable

    $primary = Start-Process -FilePath $executable -ArgumentList @("`"$initialPath`"") `
        -WorkingDirectory $runRoot -WindowStyle Minimized -PassThru
    $primaryWindowHandle = Wait-ForMainWindow -Process $primary -Timeout ([TimeSpan]::FromSeconds(20))

    $secondary = Start-Process -FilePath $executable `
        -ArgumentList @("`"$firstForwardedPath`"", "`"$secondForwardedPath`"") `
        -WorkingDirectory $runRoot -WindowStyle Minimized -PassThru
    $secondaryWindowObserved = $false
    $secondaryDeadline = [DateTime]::UtcNow + [TimeSpan]::FromSeconds(10)
    while (-not $secondary.HasExited -and [DateTime]::UtcNow -lt $secondaryDeadline) {
        $secondary.Refresh()
        $secondaryWindowObserved = $secondaryWindowObserved -or
            $secondary.MainWindowHandle -ne [IntPtr]::Zero
        Start-Sleep -Milliseconds 50
    }

    if (-not $secondary.HasExited) {
        throw "The secondary did not exit within 10 seconds."
    }

    $secondary.WaitForExit()
    $secondaryExitCode = $secondary.ExitCode
    $sessionFile = Join-Path $runRoot 'data\session.json'
    $observedWpfTabCount = Wait-ForWpfTabCount -MainWindowHandle $primaryWindowHandle `
        -ExpectedCount 3 -Timeout ([TimeSpan]::FromSeconds(20))

    if (-not $primary.CloseMainWindow()) {
        throw "The primary WPF window did not accept a clean close request."
    }

    if (-not $primary.WaitForExit(15000)) {
        throw "The primary did not exit cleanly within 15 seconds."
    }

    $session = Get-Content -Raw -LiteralPath $sessionFile | ConvertFrom-Json
    $tabs = @($session.windows | ForEach-Object { @($_.tabs) })
    $persistedPaths = @($tabs | ForEach-Object { $_.path })
    $tabFileNames = @($persistedPaths | ForEach-Object { [IO.Path]::GetFileName($_) })
    $initialRetained = $initialPath -in $persistedPaths
    $forwardedRetained = $firstForwardedPath -in $persistedPaths -and
        $secondForwardedPath -in $persistedPaths
    $exactTabSet = $tabs.Count -eq 3 -and
        @($expectedPaths | Where-Object { $_ -notin $persistedPaths }).Count -eq 0
    $passed = $primaryWindowHandle -ne 0 -and
        $secondaryExitCode -eq 0 -and
        -not $secondaryWindowObserved -and
        $primary.ExitCode -eq 0 -and
        $initialRetained -and
        $forwardedRetained -and
        $exactTabSet
    if (-not $passed) {
        throw "The two-process activation assertions did not all pass."
    }

    $result = [ordered]@{
        schemaVersion = 1
        passed = $true
        configuration = $Configuration
        primaryProcessId = $primary.Id
        primaryWindowCreated = $true
        primaryMainWindowHandle = $primaryWindowHandle
        primaryExitCode = $primary.ExitCode
        secondaryProcessId = $secondary.Id
        secondaryExited = $true
        secondaryExitCode = $secondaryExitCode
        secondaryWindowObserved = $secondaryWindowObserved
        secondaryWindowObservation = 'No main window handle observed at 50 ms polling until process exit.'
        copiedPortableDataRemoved = $copiedPortableDataRemoved
        tabCount = $tabs.Count
        observedWpfTabCount = $observedWpfTabCount
        tabFileNames = $tabFileNames
        initialCleanTabRetained = $initialRetained
        bothForwardedTabsRetained = $forwardedRetained
        exactThreeTabSet = $exactTabSet
    }
}
catch {
    $failure = $_
}
finally {
    if ($null -ne $secondary) {
        Stop-OwnedProcess -Process $secondary
    }

    if ($null -ne $primary) {
        Stop-OwnedProcess -Process $primary
    }

    if (Test-Path -LiteralPath $runRoot) {
        $cleanupDeadline = [DateTime]::UtcNow + [TimeSpan]::FromSeconds(15)
        do {
            try {
                Remove-ControlledItem -Path $runRoot -TrustedRoot $tempRoot -Recurse
            }
            catch { }
            if (-not (Test-Path -LiteralPath $runRoot)) {
                break
            }

            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $cleanupDeadline)
    }
}

if (Test-Path -LiteralPath $runRoot) {
    $cleanupFailed = $true
    if ($null -eq $failure) {
        $failure = [InvalidOperationException]::new('The isolated smoke run directory could not be removed.')
    }
}

if ($null -ne $failure) {
    $exception = if ($failure -is [System.Management.Automation.ErrorRecord]) {
        $failure.Exception
    } elseif ($failure -is [Exception]) {
        $failure
    } else {
        [InvalidOperationException]::new([string] $failure)
    }
    $message = $exception.Message.Replace(
        $buildOutput,
        '<buildOutput>',
        [StringComparison]::OrdinalIgnoreCase)
    $message = $message.Replace(
        $repositoryRoot,
        '<repositoryRoot>',
        [StringComparison]::OrdinalIgnoreCase)
    $result = [ordered]@{
        schemaVersion = 1
        passed = $false
        configuration = $Configuration
        errorType = $exception.GetType().FullName
        error = $message
        cleanupFailed = $cleanupFailed
    }
    Write-SmokeArtifact -Value $result -Path $artifactFullPath
    throw $message
}

Write-SmokeArtifact -Value $result -Path $artifactFullPath
$result | ConvertTo-Json -Depth 5
