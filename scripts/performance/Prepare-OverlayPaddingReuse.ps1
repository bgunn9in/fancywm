param(
    [Parameter(Mandatory)][string]$SnapshotId,
    [ValidateSet(0,1)][int]$ExpectedFailed = 0
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
Require (!(Test-Path -LiteralPath $snapshotRoot)) "Snapshot exists: $SnapshotId"
& "$PSScriptRoot/Save-ImplementationSnapshot.ps1" -SnapshotId $SnapshotId | Out-Null
$validationRoot = Join-Path $snapshotRoot 'validation'
New-Item -ItemType Directory -Path $validationRoot | Out-Null
& dotnet test (Join-Path $repositoryRoot 'FancyWM.Tests/FancyWM.Tests.csproj') --configuration Release --no-restore `
    --filter 'FullyQualifiedName~OverlayPadding' --logger 'trx;LogFileName=Release-overlay-padding.trx' `
    --results-directory $validationRoot *> (Join-Path $validationRoot 'Release-overlay-padding.log')
$exitCode = $LASTEXITCODE
$trxPath = Join-Path $validationRoot 'Release-overlay-padding.trx'
Require (Test-Path -LiteralPath $trxPath) 'TRX missing.'
[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$leaves = @($trx.SelectNodes("//*[local-name()='UnitTestResult']") | Where-Object { $_.SelectNodes("./*[local-name()='InnerResults']/*[local-name()='UnitTestResult']").Count -eq 0 })
$failures = @($leaves | Where-Object outcome -CNE 'Passed')
Require ($leaves.Count -eq 4 -and $failures.Count -eq $ExpectedFailed) 'Unexpected fixture result.'
Require (($ExpectedFailed -eq 0 -and $exitCode -eq 0) -or ($ExpectedFailed -eq 1 -and $exitCode -ne 0)) 'Unexpected process exit.'
if ($ExpectedFailed -eq 1) {
    Require ($failures[0].testName -eq 'OverlayPaddingPreservesMaterializedOwners') 'Wrong red test.'
    Require ($failures[0].InnerText.Contains('A completed padding-only pass must preserve model ownership.')) 'Wrong red assertion.'
}
$binarySource = Join-Path $repositoryRoot 'FancyWM.Tests/bin/Release/net10.0-windows10.0.18362.0'
$binaryRoot = Join-Path $snapshotRoot 'binaries'
New-Item -ItemType Directory -Path $binaryRoot | Out-Null
Copy-Item -Path (Join-Path $binarySource '*') -Destination $binaryRoot -Recurse
Get-ChildItem -LiteralPath $binaryRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($binaryRoot, $_.FullName).Replace('\','/'); Length = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
} | Export-Csv -NoTypeInformation (Join-Path $snapshotRoot 'binaries.csv')
[ordered]@{
    Verdict = if ($ExpectedFailed -eq 0) { 'CANDIDATE_PASS' } else { 'BASELINE_RED' }
    SnapshotId = $SnapshotId
    Total = $leaves.Count
    Passed = $leaves.Count - $failures.Count
    Failed = $failures.Count
    TrxSHA256 = (Get-FileHash -LiteralPath $trxPath).Hash
    SourceManifestSHA256 = (Get-FileHash -LiteralPath (Join-Path $snapshotRoot 'manifest.csv')).Hash
    BinaryManifestSHA256 = (Get-FileHash -LiteralPath (Join-Path $snapshotRoot 'binaries.csv')).Hash
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content (Join-Path $validationRoot 'fixture-summary.json')
Get-Content (Join-Path $validationRoot 'fixture-summary.json')
