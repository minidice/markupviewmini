Set-StrictMode -Version Latest

function Get-VerifiedPhysicalRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Root
    )

    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $rootItem = Get-Item -LiteralPath $fullRoot -Force -ErrorAction Stop
    if (-not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'A controlled mutation root must be an existing physical directory, not a reparse point.'
    }

    $rootItem.FullName.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}

function Assert-ControlledMutationPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $TrustedRoot,

        [switch] $AllowRoot,

        [switch] $RejectReparseDescendants
    )

    $physicalRoot = Get-VerifiedPhysicalRoot -Root $TrustedRoot
    $fullPath = [IO.Path]::GetFullPath($Path)
    $relative = [IO.Path]::GetRelativePath($physicalRoot, $fullPath)
    $escapesRoot = [IO.Path]::IsPathRooted($relative) -or
        $relative -eq '..' -or
        $relative.StartsWith("..$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal) -or
        $relative.StartsWith("..$([IO.Path]::AltDirectorySeparatorChar)", [StringComparison]::Ordinal)
    if ($escapesRoot -or ($relative -eq '.' -and -not $AllowRoot)) {
        throw 'A controlled mutation path escaped or selected its trusted physical root.'
    }

    $current = $physicalRoot
    if ($relative -ne '.') {
        foreach ($component in $relative.Split(
                     [char[]] @([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
                     [StringSplitOptions]::RemoveEmptyEntries)) {
            $current = Join-Path $current $component
            try {
                $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            }
            catch [System.Management.Automation.ItemNotFoundException] {
                break
            }

            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing a controlled mutation through reparse path '$current'."
            }
        }
    }

    if ($RejectReparseDescendants) {
        $target = $null
        try {
            $target = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
        }
        catch [System.Management.Automation.ItemNotFoundException] {
        }

        if ($null -ne $target -and $target.PSIsContainer) {
            $directories = [Collections.Generic.Queue[string]]::new()
            $directories.Enqueue($target.FullName)
            while ($directories.Count -ne 0) {
                $directory = $directories.Dequeue()
                foreach ($entry in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
                    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                        throw "Refusing a recursive mutation containing reparse path '$($entry.FullName)'."
                    }
                    if ($entry.PSIsContainer) {
                        $directories.Enqueue($entry.FullName)
                    }
                }
            }
        }
    }

    $fullPath
}

function New-ControlledDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $TrustedRoot
    )

    $fullPath = Assert-ControlledMutationPath -Path $Path -TrustedRoot $TrustedRoot
    New-Item -ItemType Directory -Force -Path $fullPath | Out-Null
    Assert-ControlledMutationPath -Path $fullPath -TrustedRoot $TrustedRoot
}

function Remove-ControlledItem {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $TrustedRoot,

        [switch] $Recurse
    )

    $fullPath = Assert-ControlledMutationPath `
        -Path $Path `
        -TrustedRoot $TrustedRoot `
        -RejectReparseDescendants:$Recurse
    try {
        Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop | Out-Null
    }
    catch [System.Management.Automation.ItemNotFoundException] {
        return
    }

    Remove-Item -LiteralPath $fullPath -Force -Recurse:$Recurse -ErrorAction Stop
}

function Assert-PortableArchiveMatchesDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $PublishDirectory,

        [Parameter(Mandatory)]
        [string] $ArchivePath,

        [Parameter(Mandatory)]
        [string] $TrustedRoot
    )

    $publishFullPath = Assert-ControlledMutationPath `
        -Path $PublishDirectory `
        -TrustedRoot $TrustedRoot `
        -RejectReparseDescendants
    $archiveFullPath = Assert-ControlledMutationPath `
        -Path $ArchivePath `
        -TrustedRoot $TrustedRoot
    if (-not (Test-Path -LiteralPath $publishFullPath -PathType Container) -or
        -not (Test-Path -LiteralPath $archiveFullPath -PathType Leaf)) {
        throw 'The portable publish directory and ZIP must exist before archive auditing.'
    }

    $expectedFiles = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    foreach ($publishedFile in @(
            Get-ChildItem -LiteralPath $publishFullPath -Recurse -File -Force)) {
        $relativePath = [IO.Path]::GetRelativePath(
            $publishFullPath,
            $publishedFile.FullName).Replace('\', '/')
        $expectedFiles.Add($relativePath, $publishedFile)
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($archiveFullPath)
    $seenFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $portableMarkerCount = 0
    try {
        foreach ($entry in $archive.Entries) {
            $normalizedPath = $entry.FullName.Replace('\', '/')
            $pathWithoutTrailingSlash = $normalizedPath.TrimEnd('/')
            $segments = $pathWithoutTrailingSlash.Split('/')
            if ([string]::IsNullOrWhiteSpace($pathWithoutTrailingSlash) -or
                $normalizedPath.StartsWith('/', [StringComparison]::Ordinal) -or
                $normalizedPath -match '^[A-Za-z]:' -or
                $segments -contains '' -or
                $segments -contains '.' -or
                $segments -contains '..') {
                throw "The portable ZIP contains an unsafe entry path '$normalizedPath'."
            }

            if ([string]::IsNullOrEmpty($entry.Name)) {
                continue
            }
            if (-not $seenFiles.Add($normalizedPath)) {
                throw "The portable ZIP contains duplicate entry '$normalizedPath'."
            }

            $publishedFile = $null
            if (-not $expectedFiles.TryGetValue($normalizedPath, [ref] $publishedFile)) {
                throw "The portable ZIP contains unexpected entry '$normalizedPath'."
            }
            if ([long] $entry.Length -ne [long] $publishedFile.Length) {
                throw "The portable ZIP length does not match '$normalizedPath'."
            }

            $entryStream = $entry.Open()
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try {
                $entryHash = ([BitConverter]::ToString(
                    $sha256.ComputeHash($entryStream))).Replace('-', '').ToLowerInvariant()
            }
            finally {
                $sha256.Dispose()
                $entryStream.Dispose()
            }
            $publishedHash = (Get-FileHash `
                -LiteralPath $publishedFile.FullName `
                -Algorithm SHA256).Hash.ToLowerInvariant()
            if (-not $entryHash.Equals($publishedHash, [StringComparison]::Ordinal)) {
                throw "The portable ZIP hash does not match '$normalizedPath'."
            }

            if ($normalizedPath.Equals('portable.marker', [StringComparison]::Ordinal)) {
                $portableMarkerCount++
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    if ($seenFiles.Count -ne $expectedFiles.Count) {
        $missingFiles = @($expectedFiles.Keys | Where-Object { -not $seenFiles.Contains($_) })
        throw "The portable ZIP is missing published files: $($missingFiles -join ', ')."
    }
    if ($portableMarkerCount -ne 1) {
        throw "The portable ZIP must contain exactly one portable.marker; found $portableMarkerCount."
    }

    [pscustomobject] [ordered]@{
        fileCount = $seenFiles.Count
        portableMarkerCount = $portableMarkerCount
        sha256 = (Get-FileHash -LiteralPath $archiveFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Assert-RepositoryBuildMutationPaths {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot
    )

    $physicalRoot = Get-VerifiedPhysicalRoot -Root $RepositoryRoot
    $projectPaths = @(& git -C $physicalRoot ls-files -- '*.csproj')
    if ($LASTEXITCODE -ne 0) {
        throw 'Git could not enumerate controlled project build paths.'
    }

    foreach ($projectPath in $projectPaths) {
        $projectFile = Assert-ControlledMutationPath `
            -Path (Join-Path $physicalRoot $projectPath) `
            -TrustedRoot $physicalRoot
        $projectDirectory = Split-Path -Parent $projectFile
        foreach ($outputName in @('bin', 'obj')) {
            Assert-ControlledMutationPath `
                -Path (Join-Path $projectDirectory $outputName) `
                -TrustedRoot $physicalRoot `
                -RejectReparseDescendants | Out-Null
        }
    }

    foreach ($workspace in @('web\document-surface', 'web\mermaid-editor')) {
        Assert-ControlledMutationPath `
            -Path (Join-Path $physicalRoot "$workspace\node_modules") `
            -TrustedRoot $physicalRoot | Out-Null
        Assert-ControlledMutationPath `
            -Path (Join-Path $physicalRoot "$workspace\dist") `
            -TrustedRoot $physicalRoot `
            -RejectReparseDescendants | Out-Null
    }
}

Export-ModuleMember -Function `
    Get-VerifiedPhysicalRoot, `
    Assert-ControlledMutationPath, `
    New-ControlledDirectory, `
    Remove-ControlledItem, `
    Assert-PortableArchiveMatchesDirectory, `
    Assert-RepositoryBuildMutationPaths
