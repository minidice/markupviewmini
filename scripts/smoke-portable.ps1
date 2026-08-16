[CmdletBinding()]
param(
    [string] $PublishDirectory = '.\artifacts\portable\MarkUpViewMini'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleasePathSafety.psm1') -Force
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-AppProcesses {
    @(Get-Process -Name 'MarkUpViewMini.App' -ErrorAction SilentlyContinue)
}

function Get-OwnedWebViewProcesses {
    param([string] $UserDataDirectory)

    @(Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            -not [string]::IsNullOrEmpty($_.CommandLine) -and
            $_.CommandLine.Contains($UserDataDirectory, [StringComparison]::OrdinalIgnoreCase)
        })
}

function Wait-ForActivationPipe {
    param(
        [string] $PipeName,
        [TimeSpan] $Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    while ([DateTime]::UtcNow -lt $deadline) {
        $pipe = [IO.Pipes.NamedPipeClientStream]::new(
            '.',
            $PipeName,
            [IO.Pipes.PipeDirection]::InOut,
            [IO.Pipes.PipeOptions]::Asynchronous)
        try {
            $pipe.Connect(200)
            return
        }
        catch [TimeoutException] {
        }
        catch [IO.IOException] {
        }
        finally {
            $pipe.Dispose()
        }

        Start-Sleep -Milliseconds 50
    }

    throw "The primary did not expose its activation pipe within $($Timeout.TotalSeconds) seconds."
}

function Wait-ForMainWindow {
    param(
        [System.Diagnostics.Process] $Process,
        [TimeSpan] $Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw 'The primary exited before creating its WPF window.'
        }

        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return [long] $Process.MainWindowHandle
        }

        Start-Sleep -Milliseconds 50
    }

    throw "The primary did not create its WPF window within $($Timeout.TotalSeconds) seconds."
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

    throw "The primary did not expose $ExpectedCount tabs within $($Timeout.TotalSeconds) seconds."
}

function Wait-ForWebViewData {
    param(
        [string] $Directory,
        [TimeSpan] $Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path -LiteralPath $Directory) -and
            @(Get-ChildItem -LiteralPath $Directory -Force -ErrorAction SilentlyContinue).Count -gt 0) {
            return
        }

        Start-Sleep -Milliseconds 100
    }

    throw "The production WebView did not initialize portable data within $($Timeout.TotalSeconds) seconds."
}

function Get-DirectorySnapshot {
    param([string] $Directory)

    if (-not (Test-Path -LiteralPath $Directory)) {
        return @('<absent>')
    }

    $root = Get-Item -LiteralPath $Directory -Force
    $entries = @($root) + @(Get-ChildItem -LiteralPath $Directory -Recurse -Force)
    @($entries | Sort-Object FullName | ForEach-Object {
        $relative = if ($_.FullName -eq $root.FullName) {
            '.'
        } else {
            [IO.Path]::GetRelativePath($root.FullName, $_.FullName)
        }
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            "L|$relative|$($_.Attributes)|$($_.LinkTarget -join ',')"
        } elseif ($_.PSIsContainer) {
            "D|$relative|$($_.Attributes)"
        } else {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            "F|$relative|$($_.Length)|$hash"
        }
    })
}

function Get-SourceSnapshot {
    param([string] $RepositoryRoot)

    Get-DirectorySnapshot -Directory $RepositoryRoot
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
        # Cleanup is best effort for an exact process handle owned by this run.
    }
}

$repositoryRoot = Get-VerifiedPhysicalRoot -Root (Join-Path $PSScriptRoot '..')
$expectedPublish = [IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts\portable\MarkUpViewMini'))
$publishFullPath = if ([IO.Path]::IsPathRooted($PublishDirectory)) {
    [IO.Path]::GetFullPath($PublishDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $PublishDirectory))
}
if (-not $publishFullPath.Equals($expectedPublish, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The smoke test only operates on the controlled portable publish directory.'
}
$publishFullPath = Assert-ControlledMutationPath `
    -Path $publishFullPath `
    -TrustedRoot $repositoryRoot `
    -RejectReparseDescendants

$executable = Join-Path $publishFullPath 'MarkUpViewMini.App.exe'
$marker = Join-Path $publishFullPath 'portable.marker'
$dataDirectory = Assert-ControlledMutationPath -Path (Join-Path $publishFullPath 'data') -TrustedRoot $repositoryRoot
$webViewDirectory = Assert-ControlledMutationPath -Path (Join-Path $dataDirectory 'webview2') -TrustedRoot $repositoryRoot
$sessionFile = Assert-ControlledMutationPath -Path (Join-Path $dataDirectory 'session.json') -TrustedRoot $repositoryRoot
$settingsFile = Assert-ControlledMutationPath -Path (Join-Path $dataDirectory 'settings.json') -TrustedRoot $repositoryRoot
$recoveryDirectory = Assert-ControlledMutationPath -Path (Join-Path $dataDirectory 'recovery') -TrustedRoot $repositoryRoot
$logsDirectory = Assert-ControlledMutationPath -Path (Join-Path $dataDirectory 'logs') -TrustedRoot $repositoryRoot
if (-not (Test-Path -LiteralPath $executable) -or -not (Test-Path -LiteralPath $marker)) {
    throw 'Publish the portable release before running the smoke test.'
}

$existingApplications = @(Get-AppProcesses)
if ($existingApplications.Count -ne 0) {
    throw 'Close existing MarkUpViewMini.App processes before running the isolated smoke test.'
}
if (@(Get-OwnedWebViewProcesses -UserDataDirectory $webViewDirectory).Count -ne 0) {
    throw 'A WebView process already owns the controlled portable data directory.'
}

if (Test-Path -LiteralPath $dataDirectory) {
    throw 'Refusing to run while the portable publish already contains data.'
}

$tempRoot = Get-VerifiedPhysicalRoot -Root ([IO.Path]::GetTempPath())
$runRoot = Assert-ControlledMutationPath `
    -Path (Join-Path $tempRoot "MarkUpViewMini-PortableSmoke-$([Guid]::NewGuid().ToString('N'))") `
    -TrustedRoot $tempRoot
if (-not [IO.Path]::GetFileName($runRoot).StartsWith(
        'MarkUpViewMini-PortableSmoke-',
        [StringComparison]::Ordinal)) {
    throw 'The smoke fixture directory does not use the controlled prefix.'
}

$installedData = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
    'MarkUpViewMini\data'
$sourceBefore = Get-SourceSnapshot -RepositoryRoot $repositoryRoot
$installedBefore = Get-DirectorySnapshot -Directory $installedData
$primary = $null
$secondary = $null
$failure = $null
$cleanupErrors = [Collections.Generic.List[string]]::new()
$result = $null
$runRootCreatedBySmoke = $false
$dataCreatedBySmoke = $false
try {
    New-ControlledDirectory -Path $runRoot -TrustedRoot $tempRoot | Out-Null
    $runRootCreatedBySmoke = $true
    New-ControlledDirectory -Path $dataDirectory -TrustedRoot $repositoryRoot | Out-Null
    $dataCreatedBySmoke = $true
    $initialPath = Assert-ControlledMutationPath `
        -Path (Join-Path $runRoot 'initial.md') `
        -TrustedRoot $tempRoot
    $forwardedPath = Assert-ControlledMutationPath `
        -Path (Join-Path $runRoot 'forwarded.md') `
        -TrustedRoot $tempRoot
    Set-Content -LiteralPath $initialPath -Value "# Portable initial`n`nLocal WebView readiness." -Encoding utf8NoBOM
    Set-Content -LiteralPath $forwardedPath -Value '# Portable forwarded' -Encoding utf8NoBOM

    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $suffix = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sid))).ToLowerInvariant()
    $pipeName = "MarkUpViewMini.App-$suffix"

    $primary = Start-Process -FilePath $executable `
        -ArgumentList @("`"$initialPath`"") `
        -WorkingDirectory $publishFullPath `
        -WindowStyle Minimized `
        -PassThru
    Wait-ForActivationPipe -PipeName $pipeName -Timeout ([TimeSpan]::FromSeconds(20))
    $primaryWindowHandle = Wait-ForMainWindow -Process $primary -Timeout ([TimeSpan]::FromSeconds(20))
    Wait-ForWebViewData -Directory $webViewDirectory -Timeout ([TimeSpan]::FromSeconds(30))
    $initialTabCount = Wait-ForWpfTabCount `
        -MainWindowHandle $primaryWindowHandle `
        -ExpectedCount 1 `
        -Timeout ([TimeSpan]::FromSeconds(20))

    $secondary = Start-Process -FilePath $executable `
        -ArgumentList @("`"$forwardedPath`"") `
        -WorkingDirectory $publishFullPath `
        -WindowStyle Minimized `
        -PassThru
    $secondaryWindowObserved = $false
    $secondaryDeadline = [DateTime]::UtcNow + [TimeSpan]::FromSeconds(10)
    while (-not $secondary.HasExited -and [DateTime]::UtcNow -lt $secondaryDeadline) {
        $secondary.Refresh()
        $secondaryWindowObserved = $secondaryWindowObserved -or
            $secondary.MainWindowHandle -ne [IntPtr]::Zero
        Start-Sleep -Milliseconds 50
    }
    if (-not $secondary.HasExited) {
        throw 'The secondary activation process did not exit within 10 seconds.'
    }

    $secondary.WaitForExit()
    $secondaryExitCode = $secondary.ExitCode
    $secondary.Dispose()
    $secondary = $null
    $forwardedTabCount = Wait-ForWpfTabCount `
        -MainWindowHandle $primaryWindowHandle `
        -ExpectedCount 2 `
        -Timeout ([TimeSpan]::FromSeconds(20))
    if ($secondaryExitCode -ne 0 -or $secondaryWindowObserved) {
        throw 'The second activation did not forward cleanly to the existing instance.'
    }

    if (-not $primary.CloseMainWindow()) {
        throw 'The primary WPF window did not accept a clean close request.'
    }
    if (-not $primary.WaitForExit(15000)) {
        throw 'The primary did not exit cleanly within 15 seconds.'
    }
    $primaryExitCode = $primary.ExitCode
    $primary.Dispose()
    $primary = $null
    if ($primaryExitCode -ne 0) {
        throw "The primary exited with code $primaryExitCode."
    }

    if (-not (Test-Path -LiteralPath $sessionFile)) {
        throw 'The portable session file was not written.'
    }
    $session = Get-Content -Raw -LiteralPath $sessionFile | ConvertFrom-Json
    $persistedPaths = @($session.windows | ForEach-Object { @($_.tabs) } | ForEach-Object { $_.path })
    if ($persistedPaths.Count -ne 2 -or
        $initialPath -notin $persistedPaths -or
        $forwardedPath -notin $persistedPaths) {
        throw 'The portable session did not retain the exact two activation paths.'
    }
    if (-not (Test-Path -LiteralPath $webViewDirectory)) {
        throw 'Portable WebView2 data was not retained through clean shutdown.'
    }

    $result = [ordered]@{
        schemaVersion = 1
        passed = $true
        activationPipeReady = $true
        primaryWindowCreated = $true
        initialTabCount = $initialTabCount
        secondaryExited = $true
        secondaryExitCode = $secondaryExitCode
        secondaryWindowObserved = $secondaryWindowObserved
        forwardedTabCount = $forwardedTabCount
        exactSessionPaths = $true
        portableSessionCreated = $true
        portableWebViewDataCreated = $true
        settingsPathContained = $settingsFile.StartsWith($dataDirectory, [StringComparison]::OrdinalIgnoreCase)
        recoveryPathContained = $recoveryDirectory.StartsWith($dataDirectory, [StringComparison]::OrdinalIgnoreCase)
        logsPathContained = $logsDirectory.StartsWith($dataDirectory, [StringComparison]::OrdinalIgnoreCase)
        sourceTreeUnchanged = $true
        installedDataUnchanged = $true
    }
}
catch {
    $failure = $_
}
finally {
    if ($null -ne $secondary) {
        try { Stop-OwnedProcess -Process $secondary } catch { $cleanupErrors.Add($_.Exception.Message) }
        try { $secondary.Dispose() } catch { $cleanupErrors.Add($_.Exception.Message) }
    }
    if ($null -ne $primary) {
        try { Stop-OwnedProcess -Process $primary } catch { $cleanupErrors.Add($_.Exception.Message) }
        try { $primary.Dispose() } catch { $cleanupErrors.Add($_.Exception.Message) }
    }

    foreach ($ownedWebView in @(Get-OwnedWebViewProcesses -UserDataDirectory $webViewDirectory)) {
        try { Stop-Process -Id $ownedWebView.ProcessId -Force -ErrorAction Stop }
        catch {
            if ($null -ne (Get-Process -Id $ownedWebView.ProcessId -ErrorAction SilentlyContinue)) {
                $cleanupErrors.Add($_.Exception.Message)
            }
        }
    }

    if ($runRootCreatedBySmoke -and (Test-Path -LiteralPath $runRoot)) {
        try {
            Remove-ControlledItem -Path $runRoot -TrustedRoot $tempRoot -Recurse
        }
        catch { $cleanupErrors.Add($_.Exception.Message) }
    }
    if ($dataCreatedBySmoke -and (Test-Path -LiteralPath $dataDirectory)) {
        try {
            Remove-ControlledItem -Path $dataDirectory -TrustedRoot $repositoryRoot -Recurse
        }
        catch { $cleanupErrors.Add($_.Exception.Message) }
    }

    try {
        $sourceAfter = Get-SourceSnapshot -RepositoryRoot $repositoryRoot
        if (@(Compare-Object $sourceBefore $sourceAfter -SyncWindow 0).Count -ne 0) {
            $cleanupErrors.Add('The portable runtime modified the source tree.')
        }
    }
    catch { $cleanupErrors.Add("The source-tree cleanup snapshot failed: $($_.Exception.Message)") }

    try {
        $installedAfter = Get-DirectorySnapshot -Directory $installedData
        if (@(Compare-Object $installedBefore $installedAfter -SyncWindow 0).Count -ne 0) {
            $cleanupErrors.Add('The portable runtime modified the installed LocalAppData root.')
        }
    }
    catch { $cleanupErrors.Add("The installed-data cleanup snapshot failed: $($_.Exception.Message)") }
}

if (@(Get-AppProcesses).Count -ne 0) {
    $cleanupErrors.Add('A MarkUpViewMini.App process remained after smoke cleanup.')
}
if (@(Get-OwnedWebViewProcesses -UserDataDirectory $webViewDirectory).Count -ne 0) {
    $cleanupErrors.Add('A controlled WebView process remained after smoke cleanup.')
}
if (Test-Path -LiteralPath $runRoot) {
    $cleanupErrors.Add('The controlled fixture directory remained after smoke cleanup.')
}
if (Test-Path -LiteralPath $dataDirectory) {
    $cleanupErrors.Add('The controlled portable data directory remained after smoke cleanup.')
}

if ($null -ne $failure -or $cleanupErrors.Count -ne 0) {
    $messages = [Collections.Generic.List[string]]::new()
    if ($null -ne $failure) {
        $messages.Add($failure.Exception.Message)
    }
    $messages.AddRange($cleanupErrors)
    throw ($messages -join [Environment]::NewLine)
}

$result | ConvertTo-Json -Depth 4
