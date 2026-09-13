param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$ExperimentId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Require ($LASTEXITCODE -eq 0 -and [StringComparer]::OrdinalIgnoreCase.Equals(
    [IO.Path]::GetFullPath((Get-Location).Path), [IO.Path]::GetFullPath($repositoryRoot))) 'Run from repository root.'
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$experimentRoot = Join-Path $snapshotRoot $ExperimentId
$manifestPath = Join-Path $snapshotRoot 'manifest.csv'
$measurementsPath = Join-Path $experimentRoot 'measurements.csv'
$rawPath = Join-Path $experimentRoot 'raw-counters.csv'
$processPath = Join-Path $experimentRoot 'process-times.csv'
$provenancePath = Join-Path $experimentRoot 'provenance.json'
$validationRoot = Join-Path $snapshotRoot 'validation'
$outputPath = Join-Path $validationRoot 'focus-worker-retention-strict-verification.json'
Require (!(Test-Path $outputPath)) 'Immutable strict report already exists.'
$manifest = @(Import-Csv $manifestPath)
foreach ($record in $manifest) { Require ((Hash (Join-Path $snapshotRoot "source/$($record.Path)")) -ceq $record.SHA256) "Frozen source differs: $($record.Path)" }
$verifier = @($manifest | Where-Object Path -CEQ 'scripts/performance/Verify-FocusWorkerRetention.ps1')
$runner = @($manifest | Where-Object Path -CEQ 'scripts/performance/Measure-FocusWorkerRetention.ps1')
Require ($verifier.Count -eq 1 -and $verifier[0].SHA256 -ceq (Hash $PSCommandPath) -and $runner.Count -eq 1) 'Frozen measurement protocol differs.'

$rows = @(Import-Csv $measurementsPath)
$raw = @(Import-Csv $rawPath)
$processes = @(Import-Csv $processPath)
$provenance = Get-Content -Raw $provenancePath | ConvertFrom-Json -DateKind String
Require ($rows.Count -eq 380 -and $raw.Count -eq 380 -and $processes.Count -eq 5) 'Evidence totals differ.'
Require ($provenance.Verdict -ceq 'MEASUREMENT_CHECKS_PASS' -and $provenance.SnapshotId -ceq $SnapshotId -and $provenance.ExperimentId -ceq $ExperimentId -and $provenance.MeasurementRows -eq 380 -and $provenance.ProcessCount -eq 5) 'Measurement provenance differs.'
$expected = @{
    'focus-worker-active' = @{'active-thread-delta'=1;'settled-thread-delta'=0;'worker-starts'=1;'worker-exits'=1;'worker-field-cleared'=1}
    'focus-worker-retention-completed' = @{lifetimes=128;admissions=128;'true-results'=128;'false-results'=0;'worker-starts'=128;'worker-exits'=128;'background-workers'=128;'managed-worker-thread-ids'=128;'queue-calls'=128;'attach-calls'=128;'detach-calls'=128;'focus-calls'=128;'owned-attachments'=0;'worker-field-cleared'=1;'live-worker-references'=0;'settled-thread-delta'=0}
    'focus-worker-retention-overlap-first' = @{lifetimes=64;admissions=1024;'true-results'=64;'false-results'=960;'worker-starts'=64;'worker-exits'=64;'background-workers'=64;'managed-worker-thread-ids'=64;'queue-calls'=128;'attach-calls'=64;'detach-calls'=64;'focus-calls'=64;'owned-attachments'=0;'worker-field-cleared'=1;'live-worker-references'=0;'settled-thread-delta'=0}
    'focus-worker-retention-overlap-repeat' = @{lifetimes=64;admissions=1024;'true-results'=64;'false-results'=960;'worker-starts'=64;'worker-exits'=64;'background-workers'=64;'managed-worker-thread-ids'=64;'queue-calls'=128;'attach-calls'=64;'detach-calls'=64;'focus-calls'=64;'owned-attachments'=0;'worker-field-cleared'=1;'live-worker-references'=0;'settled-thread-delta'=0}
}
$values = [Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
foreach ($row in $rows) {
    Require ($row.experiment_id -ceq $ExperimentId -and $row.snapshot_id -ceq $SnapshotId -and $row.perf_id -ceq 'PERF-030' -and $row.variant -ceq 'candidate-observation' -and $row.binary_sha256 -ceq $provenance.FancyWMDllSHA256 -and $row.benchmark_sha256 -ceq $provenance.FancyWMTestsDllSHA256) 'Measurement identity differs.'
    $match = [regex]::Match($row.scenario, '; (?<scenario>focus-worker-(?:active|retention-(?:completed|overlap-(?:first|repeat))));')
    Require ($match.Success) "Malformed measurement scenario: $($row.scenario)"
    $run = [int]::Parse($row.run, [Globalization.CultureInfo]::InvariantCulture)
    $value = [long]::Parse($row.value, [Globalization.CultureInfo]::InvariantCulture)
    Require ($run -ge 1 -and $run -le 5 -and $values.TryAdd("$run/$($match.Groups['scenario'].Value)/$($row.metric)", $value)) 'Duplicate measurement key.'
}
foreach ($run in 1..5) {
    foreach ($scenario in $expected.Keys) {
        foreach ($metric in $expected[$scenario].Keys) {
            $key = "$run/$scenario/$metric"
            Require ($values.ContainsKey($key) -and $values[$key] -eq $expected[$scenario][$metric]) "Semantic result differs: $key"
        }
        $threadDelta = $values["$run/$scenario/settled-thread-delta"]
        Require ($threadDelta -eq 0) "Settled process thread count grew: $run/$scenario"
        $handleDelta = $values["$run/$scenario/settled-handle-delta"]
        $maximumHandleDelta = if ($scenario -ceq 'focus-worker-retention-overlap-first') { 3 } else { 1 }
        Require ($handleDelta -ge 0 -and $handleDelta -le $maximumHandleDelta) "Settled process handle count is outside its declared bound: $run/$scenario"
    }
    Require ($values["$run/focus-worker-active/active-thread-delta"] -eq 1) "Active worker process-thread footprint differs: $run"
}
for ($index = 0; $index -lt $processes.Count; $index++) {
    $expectedRun = $index + 1
    Require ($processes[$index].RunId -ceq "candidate-$expectedRun" -and [int]$processes[$index].Run -eq $expectedRun -and [int]$processes[$index].ExitCode -eq 0) "Process identity differs: $expectedRun"
    if ($index -gt 0) { Require ([DateTimeOffset]::Parse($processes[$index].StartedUtc) -ge [DateTimeOffset]::Parse($processes[$index - 1].FinishedUtc)) 'Measurement processes overlap.' }
}
$rawKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($record in $raw) {
    $key = "$($record.Run)/$($record.Scenario)/$($record.Metric)"
    Require ($rawKeys.Add($key) -and $values.ContainsKey($key) -and [long]$record.Value -eq $values[$key]) "Raw counter differs: $key"
}
$completedDeltas = @(1..5 | ForEach-Object { $values["$_/focus-worker-retention-completed/settled-handle-delta"] })
$firstOverlapDeltas = @(1..5 | ForEach-Object { $values["$_/focus-worker-retention-overlap-first/settled-handle-delta"] })
$repeatOverlapDeltas = @(1..5 | ForEach-Object { $values["$_/focus-worker-retention-overlap-repeat/settled-handle-delta"] })
$report = [ordered]@{
    Verdict='ACCEPT';Accepted=$true;ReportVersion=1;Scenario='FocusWorkerRetention';PerfId='PERF-030';SnapshotId=$SnapshotId;ExperimentId=$ExperimentId;
    MeasurementRows=$rows.Count;RawCounters=$raw.Count;Processes=$processes.Count;ProcessesSequential=$true;
    EqualSizeWarmup=$true;WarmupCompletedWorkerLifetimes=640;WarmupOverlappedWorkerLifetimes=320;
    CompletedWorkerLifetimes=640;OverlappedWorkerLifetimes=640;OverlappedAdmissions=10240;
    ProcessThreadResult='All active deltas are +1 and all settled deltas are 0.';
    CompletedHandleDeltaMin=($completedDeltas | Measure-Object -Minimum).Minimum;CompletedHandleDeltaMax=($completedDeltas | Measure-Object -Maximum).Maximum;
    FirstOverlapHandleDeltaMin=($firstOverlapDeltas | Measure-Object -Minimum).Minimum;FirstOverlapHandleDeltaMax=($firstOverlapDeltas | Measure-Object -Maximum).Maximum;
    RepeatOverlapHandleDeltaMin=($repeatOverlapDeltas | Measure-Object -Minimum).Minimum;RepeatOverlapHandleDeltaMax=($repeatOverlapDeltas | Measure-Object -Maximum).Maximum;
    RetentionBound='First 64-worker overlap epoch may initialize 0..3 aggregate handles; the equal repeat epoch is 0..1, with no worker-proportional growth.';
    WorkerReferencesAlive=0;WorkerFieldCleared=$true;NativeCallsPerformed=$false;ABPerformanceClaim=$false;
    Limitations='Process.HandleCount is an aggregate testhost observation, not native handle-type attribution. UIA timeout, late native focus, production scheduling, CPU/GPU, COM/WPF/DPI and presentation latency remain NOT_MEASURED.';
    MeasurementsSHA256=Hash $measurementsPath;RawCountersSHA256=Hash $rawPath;ProcessTimesSHA256=Hash $processPath;ProvenanceSHA256=Hash $provenancePath;
    RunnerSHA256=$runner[0].SHA256;VerifierSHA256=Hash $PSCommandPath;CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')
}
$report | ConvertTo-Json -Depth 6 | Set-Content $outputPath
$report | ConvertTo-Json -Depth 6
