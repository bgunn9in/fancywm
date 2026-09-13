param(
    [Parameter(Mandatory)][string]$SnapshotId,
    [ValidateSet(0,1)][int]$ExpectedFailed = 0
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function HashFile([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
Require (!(Test-Path -LiteralPath $snapshotRoot)) "Snapshot exists: $SnapshotId"
& "$PSScriptRoot/Save-ImplementationSnapshot.ps1" -SnapshotId $SnapshotId | Out-Null
$validationRoot = Join-Path $snapshotRoot 'validation'
New-Item -ItemType Directory -Force -Path $validationRoot | Out-Null
$log = Join-Path $validationRoot 'Release-keybindings-page.log'
& dotnet test (Join-Path $repositoryRoot 'FancyWM.Tests/FancyWM.Tests.csproj') `
    --configuration Release --no-restore `
    --filter 'FullyQualifiedName~KeybindingsPageMaterializationTest' `
    --logger 'trx;LogFileName=Release-keybindings-page.trx' `
    --results-directory $validationRoot *> $log
$exitCode = $LASTEXITCODE
$trxPath = Join-Path $validationRoot 'Release-keybindings-page.trx'
Require (Test-Path $trxPath) 'TRX was not produced.'
[xml]$trx = Get-Content -Raw $trxPath
$all = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
$leaves = @($all | Where-Object { $_.SelectNodes("./*[local-name()='InnerResults']/*[local-name()='UnitTestResult']").Count -eq 0 })
$failed = @($leaves | Where-Object outcome -CNE 'Passed')
Require ($leaves.Count -eq 2 -and $failed.Count -eq $ExpectedFailed) 'Fixture outcome differs.'
Require ((($ExpectedFailed -eq 0) -and ($exitCode -eq 0)) -or (($ExpectedFailed -eq 1) -and ($exitCode -ne 0))) 'dotnet outcome differs.'

$binarySource = Join-Path $repositoryRoot 'FancyWM.Tests/bin/Release/net10.0-windows10.0.18362.0'
$binaryRoot = Join-Path $snapshotRoot 'binaries'
New-Item -ItemType Directory -Path $binaryRoot | Out-Null
Copy-Item -Path (Join-Path $binarySource '*') -Destination $binaryRoot -Recurse
$archive = @(Get-ChildItem -LiteralPath $binaryRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($binaryRoot, $_.FullName).Replace('\','/'); Length = $_.Length; SHA256 = HashFile $_.FullName }
})
$archive | Export-Csv -NoTypeInformation (Join-Path $snapshotRoot 'binaries.csv')
$summary = [ordered]@{
    Verdict = if ($ExpectedFailed -eq 0) { 'CANDIDATE_PASS' } else { 'BASELINE_RED' }
    SnapshotId = $SnapshotId
    Total = $leaves.Count
    Passed = $leaves.Count - $failed.Count
    Failed = $failed.Count
    RawResults = $all.Count
    ExpectedFailed = $ExpectedFailed
    FancyWMDllSHA256 = @($archive | Where-Object Path -CEQ 'FancyWM.dll')[0].SHA256
    FancyWMTestsDllSHA256 = @($archive | Where-Object Path -CEQ 'FancyWM.Tests.dll')[0].SHA256
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
}
$summary | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $validationRoot 'fixture-summary.json')
$summary | ConvertTo-Json -Depth 4
