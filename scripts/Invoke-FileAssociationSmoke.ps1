[CmdletBinding()]
param(
    [string] $EvidencePath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleasePathSafety.psm1') -Force
$repositoryRoot = Get-VerifiedPhysicalRoot -Root (Join-Path $PSScriptRoot '..')
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Join-Path $repositoryRoot ".superpowers\sdd\2026-08-12-markup-view-mini-phase-5-windows-release\task-2-smoke-evidence.json"
}
$applicationProject = Join-Path $repositoryRoot "src\MarkUpViewMini.App\MarkUpViewMini.App.csproj"
$testProject = Join-Path $repositoryRoot "tests\MarkUpViewMini.Infrastructure.Tests\MarkUpViewMini.Infrastructure.Tests.csproj"
$applicationExe = Join-Path $repositoryRoot "src\MarkUpViewMini.App\bin\Release\net10.0-windows\MarkUpViewMini.App.exe"

Assert-RepositoryBuildMutationPaths -RepositoryRoot $repositoryRoot
dotnet build $applicationProject -c Release --no-restore --nologo -m:1 -nr:false
if ($LASTEXITCODE -ne 0) {
    throw "The Release application build failed."
}

$env:MARKUPVIEWMINI_RUN_FILE_ASSOC_SMOKE = "1"
$env:MARKUPVIEWMINI_FILE_ASSOC_EXE = $applicationExe
$env:MARKUPVIEWMINI_FILE_ASSOC_EVIDENCE = [System.IO.Path]::GetFullPath($EvidencePath)
try {
    Assert-RepositoryBuildMutationPaths -RepositoryRoot $repositoryRoot
    dotnet test $testProject `
        -c Release `
        --no-restore `
        --filter "FullyQualifiedName~FileAssociationRealRegistrySmokeTests" `
        --nologo `
        -m:1 `
        -nr:false
    if ($LASTEXITCODE -ne 0) {
        throw "The real HKCU file-association smoke failed. Inspect the test output; cleanup ran in finally."
    }
}
finally {
    Remove-Item Env:MARKUPVIEWMINI_RUN_FILE_ASSOC_SMOKE -ErrorAction SilentlyContinue
    Remove-Item Env:MARKUPVIEWMINI_FILE_ASSOC_EXE -ErrorAction SilentlyContinue
    Remove-Item Env:MARKUPVIEWMINI_FILE_ASSOC_EVIDENCE -ErrorAction SilentlyContinue
}
