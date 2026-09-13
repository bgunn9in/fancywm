param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$CandidateSnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$BaselineSnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$ExperimentId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }
function Median([long[]]$Values) {
    $sorted = @($Values | Sort-Object); Require ($sorted.Count -gt 0) 'Cannot compute an empty median.'
    if ($sorted.Count % 2) { return [double]$sorted[[int]($sorted.Count / 2)] }
    return ([double]$sorted[$sorted.Count / 2 - 1] + [double]$sorted[$sorted.Count / 2]) / 2
}

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Require ($LASTEXITCODE -eq 0 -and [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath((Get-Location).Path),[IO.Path]::GetFullPath($repositoryRoot))) 'Run from repository root.'
$candidateRoot = Join-Path $repositoryRoot "artifacts/performance/$CandidateSnapshotId"
$baselineRoot = Join-Path $repositoryRoot "artifacts/performance/$BaselineSnapshotId"
$experimentRoot = Join-Path $candidateRoot $ExperimentId
$validationRoot = Join-Path $candidateRoot 'validation'
$outputPath = Join-Path $validationRoot 'display-removal-gc-pause-strict-verification.json'
Require (!(Test-Path $outputPath)) 'Immutable strict report already exists.'

$candidateManifest = @(Import-Csv (Join-Path $candidateRoot 'manifest.csv'))
$baselineManifest = @(Import-Csv (Join-Path $baselineRoot 'manifest.csv'))
$verifier = @($candidateManifest | Where-Object Path -CEQ 'scripts/performance/Verify-DisplayRemovalGcPause.ps1')
$runner = @($candidateManifest | Where-Object Path -CEQ 'scripts/performance/Measure-DisplayRemovalGcPause.ps1')
Require ($verifier.Count -eq 1 -and $verifier[0].SHA256 -ceq (Hash $PSCommandPath) -and $runner.Count -eq 1) 'Frozen verification protocol differs.'
$differences = [Collections.Generic.List[string]]::new()
foreach ($record in $candidateManifest) {
    $other = @($baselineManifest | Where-Object Path -CEQ $record.Path)
    Require ($other.Count -eq 1 -and (Hash (Join-Path $candidateRoot "source/$($record.Path)")) -ceq $record.SHA256 -and (Hash (Join-Path $baselineRoot "source/$($record.Path)")) -ceq $other[0].SHA256) "Frozen source differs: $($record.Path)"
    if ($record.SHA256 -cne $other[0].SHA256) { $differences.Add($record.Path) }
}
Require ($differences.Count -eq 1 -and $differences[0] -ceq 'FancyWM/MultiDisplayTilingService.cs') 'Strict source delta differs.'

$measurementsPath = Join-Path $experimentRoot 'measurements.csv'; $rawPath = Join-Path $experimentRoot 'raw-counters.csv'; $processPath = Join-Path $experimentRoot 'process-times.csv'; $provenancePath = Join-Path $experimentRoot 'provenance.json'; $binaryChecksPath = Join-Path $experimentRoot 'binary-checks.csv'
$rows = @(Import-Csv $measurementsPath); $raw = @(Import-Csv $rawPath); $processes = @(Import-Csv $processPath); $binaryChecks = @(Import-Csv $binaryChecksPath); $provenance = Get-Content -Raw $provenancePath | ConvertFrom-Json -DateKind String
Require ($rows.Count -eq 600 -and $raw.Count -eq 600 -and $processes.Count -eq 10 -and $provenance.Verdict -ceq 'MEASUREMENT_CHECKS_PASS' -and $provenance.Pairs -eq 5 -and $provenance.Processes -eq 10) 'Evidence dimensions/provenance differ.'
Require ($provenance.MeasurementsSHA256 -ceq (Hash $measurementsPath) -and $provenance.RawCountersSHA256 -ceq (Hash $rawPath) -and $provenance.ProcessTimesSHA256 -ceq (Hash $processPath) -and $provenance.BinaryChecksSHA256 -ceq (Hash $binaryChecksPath)) 'Evidence hash differs.'
Require (@($binaryChecks | Where-Object { ($_.Path -ceq 'FancyWM.dll') -ne ($_.CandidateSHA256 -cne $_.BaselineSHA256) }).Count -eq 0 -and @($binaryChecks | Where-Object Path -CEQ 'FancyWM.dll').Count -eq 1) 'Strict binary delta differs.'

$metricNames = @('collection-requests','full-gc-passes','gen0-collections','gen1-collections','gen2-collections','allocated-bytes','elapsed-ticks','gc-pause-ticks','non-gc-ticks','timestamp-frequency','managed-bytes-before','managed-bytes-before-final-collection','settled-managed-bytes','removed-owners','duplicate-events','event-calls','cycles','churn-bytes','routing-checks','checksum')
$values = [Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
foreach ($row in $rows) {
    $scenarioMatch = [regex]::Match($row.scenario,'; (1|10|50) duplicate events per removed owner;')
    Require ($scenarioMatch.Success -and $row.experiment_id -ceq $ExperimentId -and $row.perf_id -ceq 'PERF-018' -and $row.variant -cin @('baseline','candidate') -and $row.metric -cin $metricNames -and [int]$row.run -eq [int]$row.pair -and [int]$row.iterations -eq 3 -and $row.benchmark_sha256 -ceq $provenance.FancyWMTestsDllSHA256) 'Measurement row identity differs.'
    $pair=[int]$row.pair;$variant=$row.variant;$duplicates=[int]$scenarioMatch.Groups[1].Value;$value=[long]$row.value
    $expectedSnapshot=if($variant-ceq 'baseline'){$BaselineSnapshotId}else{$CandidateSnapshotId};$expectedBinary=if($variant-ceq 'baseline'){$provenance.BaselineFancyWMDllSHA256}else{$provenance.CandidateFancyWMDllSHA256}
    Require ($row.snapshot_id -ceq $expectedSnapshot -and $row.binary_sha256 -ceq $expectedBinary -and $values.TryAdd("$pair/$variant/$duplicates/$($row.metric)",$value)) 'Measurement source/binary or key differs.'
}
foreach ($pair in 1..5) { foreach ($variant in @('baseline','candidate')) { foreach ($duplicates in @(1,10,50)) {
    foreach ($metric in $metricNames) { Require ($values.ContainsKey("$pair/$variant/$duplicates/$metric")) "Missing measurement: $pair/$variant/$duplicates/$metric" }
    $requestCount=if($variant-ceq 'candidate'){3}else{3*($duplicates+1)}
    Require ($values["$pair/$variant/$duplicates/collection-requests"]-eq $requestCount -and $values["$pair/$variant/$duplicates/full-gc-passes"]-eq 2*$requestCount) "GC admission differs: $pair/$variant/$duplicates"
    foreach($generation in @('gen0-collections','gen1-collections','gen2-collections')){Require($values["$pair/$variant/$duplicates/$generation"]-eq 2*$requestCount)"Generation collection count differs: $pair/$variant/$duplicates/$generation"}
    Require ($values["$pair/$variant/$duplicates/removed-owners"]-eq 3 -and $values["$pair/$variant/$duplicates/routing-checks"]-eq 3 -and $values["$pair/$variant/$duplicates/cycles"]-eq 3 -and $values["$pair/$variant/$duplicates/duplicate-events"]-eq 3*$duplicates -and $values["$pair/$variant/$duplicates/event-calls"]-eq 3*($duplicates+1) -and $values["$pair/$variant/$duplicates/churn-bytes"]-eq 3L*($duplicates+1)*65536) "Equal workload differs: $pair/$variant/$duplicates"
    $elapsed=$values["$pair/$variant/$duplicates/elapsed-ticks"];$pause=$values["$pair/$variant/$duplicates/gc-pause-ticks"];$nonGc=$values["$pair/$variant/$duplicates/non-gc-ticks"]
    Require ($elapsed-gt 0 -and $pause-gt 0 -and $nonGc-ge 0 -and $elapsed-eq $pause+$nonGc -and $values["$pair/$variant/$duplicates/allocated-bytes"]-gt $values["$pair/$variant/$duplicates/churn-bytes"] -and $values["$pair/$variant/$duplicates/managed-bytes-before"]-gt 0 -and $values["$pair/$variant/$duplicates/managed-bytes-before-final-collection"]-gt 0 -and $values["$pair/$variant/$duplicates/settled-managed-bytes"]-gt 0 -and $values["$pair/$variant/$duplicates/checksum"]-gt 0) "Managed measurement differs: $pair/$variant/$duplicates"
}}}
foreach ($pair in 1..5) { foreach ($duplicates in @(1,10,50)) { foreach ($metric in @('removed-owners','routing-checks','cycles','duplicate-events','event-calls','churn-bytes','checksum','timestamp-frequency')) {
    Require ($values["$pair/baseline/$duplicates/$metric"] -eq $values["$pair/candidate/$duplicates/$metric"]) "A/B workload mismatch: $pair/$duplicates/$metric"
}}}

for($index=0;$index-lt $processes.Count;$index++){
    $expectedPair=[int][Math]::Floor($index/2)+1;$first=if($expectedPair%2){'baseline'}else{'candidate'};$expectedVariant=if($index%2-eq 0){$first}else{if($first-ceq 'baseline'){'candidate'}else{'baseline'}}
    Require([int]$processes[$index].Pair-eq $expectedPair -and $processes[$index].Variant-ceq $expectedVariant -and $processes[$index].RunId-ceq "$expectedVariant-$expectedPair" -and [int]$processes[$index].ExitCode-eq 0)"Process order differs: $index"
    if($index-gt 0){Require([DateTimeOffset]::Parse($processes[$index].StartedUtc)-ge [DateTimeOffset]::Parse($processes[$index-1].FinishedUtc))'Processes overlap.'}
}
$rawKeys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);foreach($record in $raw){$key="$($record.Pair)/$($record.Variant)/$($record.Duplicates)/$($record.Metric)";Require($rawKeys.Add($key)-and $values.ContainsKey($key)-and [long]$record.Value-eq $values[$key])"Raw counter differs: $key"}

$comparisons=[Collections.Generic.List[object]]::new();$timingWins=0;$allocationWins=0;$preFinalWins=0
foreach($duplicates in @(1,10,50)){
    foreach($pair in 1..5){
        $basePause=$values["$pair/baseline/$duplicates/gc-pause-ticks"];$candidatePause=$values["$pair/candidate/$duplicates/gc-pause-ticks"]
        $baseAllocation=$values["$pair/baseline/$duplicates/allocated-bytes"];$candidateAllocation=$values["$pair/candidate/$duplicates/allocated-bytes"]
        $baseMemory=$values["$pair/baseline/$duplicates/managed-bytes-before-final-collection"];$candidateMemory=$values["$pair/candidate/$duplicates/managed-bytes-before-final-collection"]
        if($candidatePause-lt $basePause){$timingWins++};if($candidateAllocation-lt $baseAllocation){$allocationWins++};if($candidateMemory-lt $baseMemory){$preFinalWins++}
        $comparisons.Add([pscustomobject]@{Duplicates=$duplicates;Pair=$pair;BaselineRequests=$values["$pair/baseline/$duplicates/collection-requests"];CandidateRequests=$values["$pair/candidate/$duplicates/collection-requests"];BaselineGcPauseTicks=$basePause;CandidateGcPauseTicks=$candidatePause;BaselineAllocatedBytes=$baseAllocation;CandidateAllocatedBytes=$candidateAllocation;BaselinePreFinalManagedBytes=$baseMemory;CandidatePreFinalManagedBytes=$candidateMemory})
    }
}
$summary=foreach($duplicates in @(1,10,50)){
    [pscustomobject]@{Duplicates=$duplicates;BaselineRequestsPerProcess=3*($duplicates+1);CandidateRequestsPerProcess=3;AvoidedRequestsPerProcess=3*$duplicates;BaselineFullGcPassesPerProcess=6*($duplicates+1);CandidateFullGcPassesPerProcess=6;MedianBaselineGcPauseTicks=Median([long[]](1..5|ForEach-Object{$values["$_/baseline/$duplicates/gc-pause-ticks"]}));MedianCandidateGcPauseTicks=Median([long[]](1..5|ForEach-Object{$values["$_/candidate/$duplicates/gc-pause-ticks"]}));MedianBaselineAllocatedBytes=Median([long[]](1..5|ForEach-Object{$values["$_/baseline/$duplicates/allocated-bytes"]}));MedianCandidateAllocatedBytes=Median([long[]](1..5|ForEach-Object{$values["$_/candidate/$duplicates/allocated-bytes"]}));MedianBaselinePreFinalManagedBytes=Median([long[]](1..5|ForEach-Object{$values["$_/baseline/$duplicates/managed-bytes-before-final-collection"]}));MedianCandidatePreFinalManagedBytes=Median([long[]](1..5|ForEach-Object{$values["$_/candidate/$duplicates/managed-bytes-before-final-collection"]}))}
}
New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null
$report=[ordered]@{Verdict='ACCEPT';Accepted=$true;ReportVersion=1;Scenario='DisplayRemovalGcPause';PerfId='PERF-018';CandidateSnapshotId=$CandidateSnapshotId;BaselineSnapshotId=$BaselineSnapshotId;ExperimentId=$ExperimentId;Pairs=5;Processes=10;ProcessesSequential=$true;MeasurementRows=600;RawCounters=600;EqualLifecycleEventAndChurnWorkload=$true;ProductionSourceDifferences=@($differences);BinaryDifferences=@('FancyWM.dll');TotalBaselineRequests=960;TotalCandidateRequests=45;TotalAvoidedRequests=915;TotalBaselineForcedFullGcPasses=1920;TotalCandidateForcedFullGcPasses=90;GcPauseCandidateWins=$timingWins;GcPauseCandidateLosses=15-$timingWins;AllocationCandidateWins=$allocationWins;PreFinalManagedMemoryCandidateWins=$preFinalWins;TimingClaim='Descriptive Stopwatch intervals around exact synchronous collector callback; deterministic acceptance rests on admitted request/full-GC counts.';Summary=@($summary);PairComparisons=@($comparisons);Limitations='Managed x64 testhost with fake displays/services. No native heap, real display removal, SettingsWindow combined collection, whole-process CPU/GPU, DWM/compositor or presentation latency.';MeasurementsSHA256=Hash $measurementsPath;RawCountersSHA256=Hash $rawPath;ProcessTimesSHA256=Hash $processPath;ProvenanceSHA256=Hash $provenancePath;BinaryChecksSHA256=Hash $binaryChecksPath;RunnerSHA256=$runner[0].SHA256;VerifierSHA256=Hash $PSCommandPath;CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')}
$report|ConvertTo-Json -Depth 8|Set-Content $outputPath;$report|ConvertTo-Json -Depth 8
