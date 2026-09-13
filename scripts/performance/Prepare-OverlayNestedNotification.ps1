param(
    [Parameter(Mandatory)][string]$SnapshotId,
    [ValidateSet(0,3)][int]$ExpectedFailed = 0
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
    --filter 'FullyQualifiedName~OverlayNested|FullyQualifiedName~OverlayPadding' --logger 'trx;LogFileName=Release-overlay-nested.trx' `
    --results-directory $validationRoot *> (Join-Path $validationRoot 'Release-overlay-nested.log')
$exitCode = $LASTEXITCODE
$trxPath = Join-Path $validationRoot 'Release-overlay-nested.trx'
Require (Test-Path -LiteralPath $trxPath) 'TRX missing.'
[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$leaves = @($trx.SelectNodes("//*[local-name()='UnitTestResult']") | Where-Object { $_.SelectNodes("./*[local-name()='InnerResults']/*[local-name()='UnitTestResult']").Count -eq 0 })
$failures = @($leaves | Where-Object outcome -CNE 'Passed')
Require ($leaves.Count -eq 7 -and $failures.Count -eq $ExpectedFailed) 'Unexpected fixture result.'
Require (($ExpectedFailed -eq 0 -and $exitCode -eq 0) -or ($ExpectedFailed -eq 3 -and $exitCode -ne 0)) 'Unexpected process exit.'
if ($ExpectedFailed -eq 3) {
    $expected = @('OverlayNestedPaddingKeepsCollectionsAndVisualsConsistent','OverlayNestedInvalidationAndDisposeFinishAfterListeners','OverlayNestedFailurePreservesPrimaryAndRetriesCleanup')
    Require (@(Compare-Object @($failures.testName) $expected).Count -eq 0) 'Wrong red tests.'
    Require (($failures | Where-Object testName -eq $expected[0]).InnerText.Contains('Recovery must not append fresh models beside disposed collection entries.')) 'Wrong visual regression.'
    Require (($failures | Where-Object testName -eq $expected[1]).InnerText.Contains('Cannot change ObservableCollection during a CollectionChanged event.')) 'Wrong collection regression.'
    Require (($failures | Where-Object testName -eq $expected[2]).InnerText.Contains('Deferred cleanup must preserve the original notification error.')) 'Wrong exception regression.'
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
    SnapshotId = $SnapshotId; Total = $leaves.Count; Passed = $leaves.Count - $failures.Count; Failed = $failures.Count
    TrxSHA256 = (Get-FileHash -LiteralPath $trxPath).Hash
    SourceManifestSHA256 = (Get-FileHash -LiteralPath (Join-Path $snapshotRoot 'manifest.csv')).Hash
    BinaryManifestSHA256 = (Get-FileHash -LiteralPath (Join-Path $snapshotRoot 'binaries.csv')).Hash
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content (Join-Path $validationRoot 'fixture-summary.json')
Get-Content (Join-Path $validationRoot 'fixture-summary.json')
