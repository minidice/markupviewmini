[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleasePathSafety.psm1') -Force

$tempRoot = Get-VerifiedPhysicalRoot -Root ([IO.Path]::GetTempPath())
$runRoot = Assert-ControlledMutationPath `
    -Path (Join-Path $tempRoot "MarkUpViewMini-PortableArchiveAudit-$([Guid]::NewGuid().ToString('N'))") `
    -TrustedRoot $tempRoot
$publishDirectory = Join-Path $runRoot 'publish'
$archivePath = Join-Path $runRoot 'portable.zip'

try {
    New-ControlledDirectory -Path $publishDirectory -TrustedRoot $tempRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $publishDirectory 'portable.marker') `
        -Value 'portable' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $publishDirectory 'payload.txt') `
        -Value 'original payload' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $publishDirectory 'release-provenance.json') `
        -Value '{"schemaVersion":1}' -Encoding utf8NoBOM

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath
    $validAudit = Assert-PortableArchiveMatchesDirectory `
        -PublishDirectory $publishDirectory `
        -ArchivePath $archivePath `
        -TrustedRoot $tempRoot
    if ($validAudit.fileCount -ne 3 -or $validAudit.portableMarkerCount -ne 1) {
        throw 'The valid portable archive audit returned unexpected counts.'
    }

    Set-Content -LiteralPath (Join-Path $publishDirectory 'payload.txt') `
        -Value 'tampered after compression' -Encoding utf8NoBOM
    $tamperRejected = $false
    try {
        Assert-PortableArchiveMatchesDirectory `
            -PublishDirectory $publishDirectory `
            -ArchivePath $archivePath `
            -TrustedRoot $tempRoot | Out-Null
    }
    catch {
        $tamperRejected = $true
    }
    if (-not $tamperRejected) {
        throw 'The portable archive audit accepted a hash-mismatched published file.'
    }

    [ordered]@{
        exactArchiveAccepted = $true
        payloadTamperRejected = $true
        exactPortableMarkerCount = $true
    } | ConvertTo-Json
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        Remove-ControlledItem -Path $runRoot -TrustedRoot $tempRoot -Recurse
    }
}
