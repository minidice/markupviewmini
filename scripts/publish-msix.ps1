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

function Get-EvaluatedMSBuildProperty {
    param(
        [Parameter(Mandatory)] [string] $ProjectPath,
        [Parameter(Mandatory)] [string] $Name
    )

    $output = & dotnet msbuild $ProjectPath "-getProperty:$Name"
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild could not evaluate property '$Name' for '$ProjectPath'."
    }

    $value = ($output -join '').Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "MSBuild evaluated property '$Name' to an empty value for '$ProjectPath'."
    }

    return $value
}

function Get-XmlSourceOffset {
    param(
        [Parameter(Mandatory)] [string] $Text,
        [Parameter(Mandatory)] [int] $LineNumber,
        [Parameter(Mandatory)] [int] $LinePosition
    )

    $offset = 0
    for ($line = 1; $line -lt $LineNumber; $line++) {
        $breakIndex = $Text.IndexOfAny([char[]] @("`r", "`n"), $offset)
        if ($breakIndex -lt 0) {
            throw 'The XML source ended before the line reported by the parser.'
        }

        $offset = $breakIndex + 1
        if ($Text[$breakIndex] -eq "`r" -and
            $offset -lt $Text.Length -and
            $Text[$offset] -eq "`n") {
            $offset++
        }
    }

    if ($offset + $LinePosition - 1 -gt $Text.Length) {
        throw 'The XML source ended before the position reported by the parser.'
    }

    return $offset + $LinePosition - 1
}

function Set-AppxManifestIdentityVersion {
    <#
        Rewrites only Identity/@Version and leaves every other byte of the manifest alone.

        The attribute is located with the XML reader rather than a text pattern, and the
        reader's own source coordinates decide which characters are replaced. Saving a
        parsed DOM back over the file is not an option: the DOM does not model the
        whitespace inside a start tag, so a round trip collapses the hand-formatted
        multi-line attributes into one long line and produces a large spurious diff.
        Splicing at the parsed coordinates keeps the XML declaration, the UTF-8 encoding,
        the indentation and the line endings exactly as authored.

        Returns $true when the file was rewritten, $false when it already carried $Version.
    #>
    param(
        [Parameter(Mandatory)] [string] $ManifestPath,
        [Parameter(Mandatory)] [string] $Version
    )

    $manifestNamespace = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10'
    $manifestBytes = [IO.File]::ReadAllBytes($ManifestPath)
    $preamble = [Text.Encoding]::UTF8.GetPreamble()
    $hasPreamble = $manifestBytes.Length -ge $preamble.Length
    if ($hasPreamble) {
        for ($index = 0; $index -lt $preamble.Length; $index++) {
            if ($manifestBytes[$index] -ne $preamble[$index]) {
                $hasPreamble = $false
                break
            }
        }
    }

    $preambleLength = if ($hasPreamble) { $preamble.Length } else { 0 }
    $manifestText = [Text.Encoding]::UTF8.GetString(
        $manifestBytes,
        $preambleLength,
        $manifestBytes.Length - $preambleLength)

    $currentVersion = $null
    $attributeOffset = -1
    $reader = [Xml.XmlReader]::Create([IO.StringReader]::new($manifestText))
    try {
        $lineInfo = [Xml.IXmlLineInfo] $reader
        if (-not $lineInfo.HasLineInfo()) {
            throw 'The XML reader cannot report source positions for the app package manifest.'
        }

        while ($reader.Read()) {
            if ($reader.NodeType -ne [Xml.XmlNodeType]::Element -or
                $reader.Depth -ne 1 -or
                $reader.LocalName -ne 'Identity' -or
                $reader.NamespaceURI -ne $manifestNamespace) {
                continue
            }

            if (-not $reader.MoveToAttribute('Version')) {
                throw 'Package.appxmanifest is missing an Identity/Version attribute to stamp.'
            }

            $currentVersion = $reader.Value
            $attributeOffset = Get-XmlSourceOffset `
                -Text $manifestText `
                -LineNumber $lineInfo.LineNumber `
                -LinePosition $lineInfo.LinePosition
            break
        }
    }
    finally {
        $reader.Dispose()
    }

    if ($attributeOffset -lt 0) {
        throw 'Package.appxmanifest is missing the Package/Identity element to stamp.'
    }
    if ($currentVersion -eq $Version) {
        return $false
    }

    $equalsIndex = $manifestText.IndexOf('=', $attributeOffset)
    if ($equalsIndex -lt 0) {
        throw 'Package.appxmanifest has a malformed Identity/Version attribute.'
    }

    $quoteIndex = $equalsIndex + 1
    while ($quoteIndex -lt $manifestText.Length -and
        [char]::IsWhiteSpace($manifestText[$quoteIndex])) {
        $quoteIndex++
    }
    $quote = if ($quoteIndex -lt $manifestText.Length) {
        $manifestText[$quoteIndex]
    }
    else {
        [char] 0
    }
    if ($quote -ne [char] 0x22 -and $quote -ne [char] 0x27) {
        throw 'Package.appxmanifest has an unquoted Identity/Version attribute.'
    }

    $valueStart = $quoteIndex + 1
    $valueEnd = $manifestText.IndexOf($quote, $valueStart)
    if ($valueEnd -lt 0) {
        throw 'Package.appxmanifest has an unterminated Identity/Version attribute.'
    }
    if (-not $manifestText.Substring($valueStart, $valueEnd - $valueStart).Equals(
            $currentVersion,
            [StringComparison]::Ordinal)) {
        throw 'The parsed Identity/Version value does not match the manifest source text.'
    }

    $updatedText = $manifestText.Remove(
        $valueStart,
        $valueEnd - $valueStart).Insert($valueStart, $Version)
    $updatedBytes = [Text.Encoding]::UTF8.GetBytes($updatedText)
    if ($hasPreamble) {
        $updatedBytes = $preamble + $updatedBytes
    }
    [IO.File]::WriteAllBytes($ManifestPath, $updatedBytes)

    return $true
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
$applicationProjectPath = Assert-ControlledMutationPath `
    -Path (Join-Path $repositoryRoot 'src\MarkUpViewMini.App') `
    -TrustedRoot $repositoryRoot
$manifestPath = Assert-ControlledMutationPath `
    -Path (Join-Path $repositoryRoot 'src\MarkUpViewMini.App.Package\Package.appxmanifest') `
    -TrustedRoot $repositoryRoot

$trackedStatus = Get-CheckedGitOutput @('status', '--porcelain', '--untracked-files=all')
if (-not [string]::IsNullOrWhiteSpace($trackedStatus)) {
    throw 'MSIX release provenance requires a clean source worktree.'
}
$sourceCommit = Get-CheckedGitOutput @('rev-parse', 'HEAD')
$sourceTree = Get-CheckedGitOutput @('rev-parse', 'HEAD^{tree}')
if ($sourceCommit -notmatch '^[0-9a-f]{40}$' -or $sourceTree -notmatch '^[0-9a-f]{40}$') {
    throw 'MSIX release provenance requires exact Git commit and tree object IDs.'
}

# Directory.Build.props is the single version source, so the MSIX Identity version is
# evaluated from MSBuild instead of being authored a second time in the manifest. The
# stamp happens before the package build, and a stamp that actually changed the manifest
# has to be committed first: the provenance recorded above describes the source tree the
# package is built from, and it has to stay true.
$msixVersion = Get-EvaluatedMSBuildProperty `
    -ProjectPath $applicationProjectPath `
    -Name 'MarkUpViewMiniMsixVersion'
if ($msixVersion -notmatch '^\d+\.\d+\.\d+\.0$') {
    throw "MarkUpViewMiniMsixVersion '$msixVersion' must be a four-part version whose revision is 0."
}
if (Set-AppxManifestIdentityVersion -ManifestPath $manifestPath -Version $msixVersion) {
    throw ("Package.appxmanifest was stale and has been stamped with Identity version " +
        "'$msixVersion'. Commit the manifest and re-run so the package carries exact " +
        'source provenance.')
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

    & dotnet test (Join-Path $repositoryRoot 'tests\MarkUpViewMini.App.Tests') `
        -c Release `
        --no-restore `
        --filter 'FullyQualifiedName~Msix_package_contains_the_document_surface_startup_assets' `
        -m:1 `
        -nr:false
    if ($LASTEXITCODE -ne 0) {
        throw "MSIX package asset audit failed with exit code $LASTEXITCODE."
    }

    [ordered]@{
        sourceCommit = $sourceCommit
        sourceTree = $sourceTree
        identityVersion = $msixVersion
        msixPath = $msixPath
        msixSha256 = (Get-FileHash -LiteralPath $msixPath -Algorithm SHA256).Hash.ToLowerInvariant()
        signingCertificateThumbprint = $certificate.Thumbprint
        signingCertificateSubject = $identitySubject
    } | ConvertTo-Json
}
finally {
    Pop-Location
}
