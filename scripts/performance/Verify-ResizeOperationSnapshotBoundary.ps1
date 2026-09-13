param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$ExperimentId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }
function Trx-Counters([string]$Path) { [xml]$d=Get-Content -Raw $Path; return $d.TestRun.ResultSummary.Counters }

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$experimentRoot = Join-Path $snapshotRoot $ExperimentId
$validationRoot = Join-Path $snapshotRoot 'validation'
$outputPath = Join-Path $validationRoot 'resize-operation-boundary-strict-verification.json'
Require (!(Test-Path $outputPath)) 'Immutable strict report already exists.'
$manifest=@(Import-Csv (Join-Path $snapshotRoot 'manifest.csv'))
foreach($record in $manifest){Require((Hash (Join-Path $snapshotRoot "source/$($record.Path)"))-ceq $record.SHA256)"Frozen source differs: $($record.Path)"}
foreach($name in @('Measure-ResizeOperationSnapshotBoundary.ps1','Verify-ResizeOperationSnapshotBoundary.ps1')){
    $record=@($manifest|Where-Object Path -CEQ "scripts/performance/$name");Require($record.Count-eq 1)"Missing frozen protocol: $name"
    if($name-ceq 'Verify-ResizeOperationSnapshotBoundary.ps1'){Require($record[0].SHA256-ceq(Hash $PSCommandPath))'Executing verifier differs.'}
}
$rows=@(Import-Csv (Join-Path $experimentRoot 'measurements.csv'));$raw=@(Import-Csv (Join-Path $experimentRoot 'raw-counters.csv'));$processes=@(Import-Csv (Join-Path $experimentRoot 'process-times.csv'));$provenance=Get-Content -Raw (Join-Path $experimentRoot 'provenance.json')|ConvertFrom-Json -DateKind String
Require($rows.Count-eq 105 -and $raw.Count-eq 105 -and $processes.Count-eq 5 -and $provenance.Verdict-ceq 'MEASUREMENT_CHECKS_PASS' -and $provenance.ABPerformanceClaim-eq $false)'Evidence dimensions/provenance differ.'
Require($provenance.MeasurementsSHA256-ceq(Hash (Join-Path $experimentRoot 'measurements.csv')) -and $provenance.RawCountersSHA256-ceq(Hash (Join-Path $experimentRoot 'raw-counters.csv')) -and $provenance.ProcessTimesSHA256-ceq(Hash (Join-Path $experimentRoot 'process-times.csv')))'Evidence hash differs.'
$values=[Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
foreach($row in $rows){
    $match=[regex]::Match($row.scenario,'; (?<count>1|10|50) displays;');Require($match.Success -and $row.experiment_id-ceq $ExperimentId -and $row.snapshot_id-ceq $SnapshotId -and $row.perf_id-ceq 'PERF-022' -and $row.variant-ceq 'current-safety-observation' -and $row.binary_sha256-ceq $provenance.FancyWMDllSHA256 -and $row.benchmark_sha256-ceq $provenance.FancyWMTestsDllSHA256)'Measurement identity differs.'
    $run=[int]$row.run;$count=[int]$match.Groups['count'].Value;$value=[long]$row.value;Require($values.TryAdd("$run/$count/$($row.metric)",$value))'Duplicate measurement key.'
}
foreach($run in 1..5){foreach($count in @(1,10,50)){
    Require($values["$run/$count/cycles"]-eq 100 -and $values["$run/$count/display-count"]-eq $count -and $values["$run/$count/position-reads"]-eq 100*($count+1) -and $values["$run/$count/snapshot-reads"]-eq 100 -and $values["$run/$count/workarea-reads"]-eq 100*($count+2) -and $values["$run/$count/true-results"]+$values["$run/$count/false-results"]-eq 100)"Semantic count differs: $run/$count"
}}
for($index=0;$index-lt $processes.Count;$index++){Require([int]$processes[$index].Run-eq $index+1 -and $processes[$index].RunId-ceq "current-$($index+1)" -and [int]$processes[$index].ExitCode-eq 0)"Process identity differs: $index";if($index-gt 0){Require([DateTimeOffset]::Parse($processes[$index].StartedUtc)-ge [DateTimeOffset]::Parse($processes[$index-1].FinishedUtc))'Processes overlap.'}}
$rawKeys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);foreach($record in $raw){$key="$($record.Run)/$($record.DisplayCount)/$($record.Metric)";Require($rawKeys.Add($key)-and $values.ContainsKey($key)-and [long]$record.Value-eq $values[$key])"Raw counter differs: $key"}
foreach($configuration in @('Debug','Release')){foreach($suite in @('targeted','affected')){$counter=Trx-Counters (Join-Path $validationRoot "$configuration-$suite.trx");Require([int]$counter.failed-eq 0 -and [int]$counter.passed-gt 0)"$configuration $suite validation differs."}}
$hypothesisRoot=Join-Path $snapshotRoot 'rejected-position-snapshot-hypothesis';$rejection=Get-Content -Raw (Join-Path $hypothesisRoot 'rejection-summary.json')|ConvertFrom-Json -DateKind String;$candidateCounter=Trx-Counters (Join-Path $hypothesisRoot 'rejected-position-snapshot.trx');$restoredCounter=Trx-Counters (Join-Path $validationRoot 'restored-current.trx')
Require($rejection.Verdict-ceq 'REJECT_POSITION_SNAPSHOT' -and [int]$candidateCounter.total-eq 4 -and [int]$candidateCounter.failed-eq 4 -and [int]$restoredCounter.passed-eq 4 -and (Hash (Join-Path $repositoryRoot 'FancyWM/TilingService.cs'))-ceq $rejection.OriginalSourceSHA256)'Hypothesis rejection or byte-exact restore differs.'
$targetedDebug=Trx-Counters (Join-Path $validationRoot 'Debug-targeted.trx');$targetedRelease=Trx-Counters (Join-Path $validationRoot 'Release-targeted.trx');$affectedDebug=Trx-Counters (Join-Path $validationRoot 'Debug-affected.trx');$affectedRelease=Trx-Counters (Join-Path $validationRoot 'Release-affected.trx')
$report=[ordered]@{
    Verdict='ACCEPT_SAFETY_BOUNDARY';Accepted=$true;PerfId='PERF-022';SnapshotId=$SnapshotId;ExperimentId=$ExperimentId;ProductionDelta=$false;
    ConsolidationDecision='REJECT';Reason='Position and WorkArea callbacks are observable between provider-list elements; the second Position read can mutate the published snapshot during the same operation.';
    TargetedDebug=[int]$targetedDebug.passed;TargetedRelease=[int]$targetedRelease.passed;
    AffectedDebug=[int]$affectedDebug.passed;AffectedRelease=[int]$affectedRelease.passed;
    HypothesisFailedLeaves=4;RestoredPassedLeaves=4;Processes=5;MeasurementRows=105;ABPerformanceClaim=$false;NativeCalls=$false;
    MeasurementsSHA256=Hash (Join-Path $experimentRoot 'measurements.csv');RawCountersSHA256=Hash (Join-Path $experimentRoot 'raw-counters.csv');ProcessTimesSHA256=Hash (Join-Path $experimentRoot 'process-times.csv');RejectionSummarySHA256=Hash (Join-Path $hypothesisRoot 'rejection-summary.json');VerifierSHA256=Hash $PSCommandPath;CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')
}
$report|ConvertTo-Json -Depth 5|Set-Content $outputPath;$report|ConvertTo-Json -Depth 5
