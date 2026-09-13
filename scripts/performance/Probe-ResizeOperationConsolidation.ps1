param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$validationRoot = Join-Path $snapshotRoot 'validation'
$hypothesisRoot = Join-Path $snapshotRoot 'rejected-position-snapshot-hypothesis'
Require ((Test-Path $snapshotRoot) -and !(Test-Path $hypothesisRoot)) 'Snapshot is missing or hypothesis already exists.'
$manifest = @(Import-Csv (Join-Path $snapshotRoot 'manifest.csv'))
foreach ($path in @('scripts/performance/Probe-ResizeOperationConsolidation.ps1','FancyWM/TilingService.cs')) {
    $record = @($manifest | Where-Object Path -CEQ $path)
    Require ($record.Count -eq 1 -and (Hash (Join-Path $snapshotRoot "source/$path")) -ceq $record[0].SHA256) "Frozen source differs: $path"
}
Require ((Hash $PSCommandPath) -ceq @($manifest | Where-Object Path -CEQ 'scripts/performance/Probe-ResizeOperationConsolidation.ps1')[0].SHA256) 'Executing probe differs from frozen protocol.'

New-Item -ItemType Directory -Path $hypothesisRoot | Out-Null
$sourcePath = Join-Path $repositoryRoot 'FancyWM/TilingService.cs'
$originalBytes = [IO.File]::ReadAllBytes($sourcePath)
$originalHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($originalBytes))
$expectedHash = @($manifest | Where-Object Path -CEQ 'FancyWM/TilingService.cs')[0].SHA256
Require ($originalHash -ceq $expectedHash) 'Live TilingService source differs from the frozen boundary.'
$encoding = [Text.UTF8Encoding]::new($false,$true)
$originalText = $encoding.GetString($originalBytes)
$needle = 'm_workspace.DisplayManager.Displays.FirstOrDefault(x => x.WorkArea.Contains(window.Position.Center))'
$replacement = 'm_workspace.DisplayManager.Displays.FirstOrDefault(x => x.WorkArea.Contains(oldSize.Center))'
Require (($originalText.Split($needle).Length - 1) -eq 2) 'Expected exactly two repeated Position reads.'
$candidateText = $originalText.Replace($needle,$replacement,[StringComparison]::Ordinal)
$candidateBytes = $encoding.GetBytes($candidateText)
[IO.File]::WriteAllBytes((Join-Path $hypothesisRoot 'TilingService.cs'),$candidateBytes)
$candidateHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($candidateBytes))

$targeted = 'FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.CanResizePreservesRepeatedProviderReadOrder|FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.CanResizePreservesMutationDuringRepeatedPositionRead|FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.ResizePreservesRepeatedProviderReadOrder|FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.ResizeOperationSnapshotBoundaryCounterScenario'
$candidateExit = -1
$restoredExit = -1
try {
    [IO.File]::WriteAllBytes($sourcePath,$candidateBytes)
    & dotnet test FancyWM.Tests/FancyWM.Tests.csproj --configuration Release --no-restore `
        --filter $targeted --logger 'trx;LogFileName=rejected-position-snapshot.trx' `
        --results-directory $hypothesisRoot *> (Join-Path $hypothesisRoot 'rejected-position-snapshot.log')
    $candidateExit = $LASTEXITCODE
    Require ($candidateExit -ne 0) 'The consolidation hypothesis unexpectedly passed the boundary fixture.'
    $outputRoot = Join-Path $repositoryRoot 'FancyWM.Tests/bin/Release/net10.0-windows10.0.18362.0'
    Copy-Item -LiteralPath (Join-Path $outputRoot 'FancyWM.dll') -Destination (Join-Path $hypothesisRoot 'FancyWM.dll')
    Copy-Item -LiteralPath (Join-Path $outputRoot 'FancyWM.Tests.dll') -Destination (Join-Path $hypothesisRoot 'FancyWM.Tests.dll')
}
finally {
    [IO.File]::WriteAllBytes($sourcePath,$originalBytes)
}
Require ((Hash $sourcePath) -ceq $originalHash) 'Live production source was not restored byte-for-byte.'
& dotnet test FancyWM.Tests/FancyWM.Tests.csproj --configuration Release --no-restore `
    --filter $targeted --logger 'trx;LogFileName=restored-current.trx' `
    --results-directory $validationRoot *> (Join-Path $validationRoot 'restored-current.log')
$restoredExit = $LASTEXITCODE
Require ($restoredExit -eq 0 -and (Hash $sourcePath) -ceq $originalHash) 'Restored current source did not pass.'

[ordered]@{
    Verdict='REJECT_POSITION_SNAPSHOT';SnapshotId=$SnapshotId;
    Hypothesis='Replace both per-display window.Position.Center reads with the earlier oldSize.Center value.';
    CandidateExitCode=$candidateExit;RestoredExitCode=$restoredExit;
    OriginalSourceSHA256=$originalHash;CandidateSourceSHA256=$candidateHash;
    CandidateFancyWMDllSHA256=Hash (Join-Path $hypothesisRoot 'FancyWM.dll');
    CandidateFancyWMTestsDllSHA256=Hash (Join-Path $hypothesisRoot 'FancyWM.Tests.dll');
    Regression='The final common fixture passes current production and rejects the two-site consolidation.';
    CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $hypothesisRoot 'rejection-summary.json')
Get-Content -Raw (Join-Path $hypothesisRoot 'rejection-summary.json')
