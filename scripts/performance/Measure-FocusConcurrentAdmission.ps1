param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$BaselineSnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$CandidateSnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$ExperimentId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }
function Median([double[]]$Values) { $v=@($Values|Sort-Object); if($v.Count%2){return $v[[int]($v.Count/2)]}; return ($v[$v.Count/2-1]+$v[$v.Count/2])/2 }
function Load-Trx([string]$Path) {
    $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null
    $reader=[Xml.XmlReader]::Create($Path,$settings)
    try{$d=[Xml.XmlDocument]::new();$d.XmlResolver=$null;$d.Load($reader);return $d}finally{$reader.Dispose()}
}
function Load-Prepared([string]$Id,[string]$Role) {
    $root=Join-Path $repositoryRoot "artifacts/performance/$Id"
    $manifestPath=Join-Path $root 'manifest.csv';$archivePath=Join-Path $root 'release-test-binaries.csv';$provenancePath=Join-Path $root 'release-test-binaries.provenance.json'
    Require ((Test-Path $manifestPath) -and (Test-Path $archivePath) -and (Test-Path $provenancePath)) "Prepared snapshot missing: $Id"
    $manifest=@(Import-Csv $manifestPath);$archive=@(Import-Csv $archivePath);$provenance=Get-Content -Raw $provenancePath|ConvertFrom-Json
    Require ($provenance.SnapshotId -ceq $Id -and $provenance.Role -ceq $Role -and $provenance.ArchiveManifestSHA256 -ceq (Hash $archivePath)) "Prepared provenance differs: $Id"
    foreach($record in $manifest){Require((Hash (Join-Path $root "source/$($record.Path)"))-ceq $record.SHA256) "Frozen source differs: $Id/$($record.Path)"}
    foreach($record in $archive){$p=Join-Path $root "release-test-binaries/$($record.Path)";Require((Hash $p)-ceq $record.Hash -and (Get-Item $p).Length-eq [long]$record.Length) "Archived binary differs: $Id/$($record.Path)"}
    $production=@($archive|Where-Object Path -CEQ 'FancyWM.dll');$fixture=@($archive|Where-Object Path -CEQ 'FancyWM.Tests.dll')
    Require($production.Count-eq 1 -and $fixture.Count-eq 1) "Production/fixture binary missing: $Id"
    [pscustomobject]@{Id=$Id;Root=$root;Manifest=$manifest;Archive=$archive;Provenance=$provenance;Production=$production[0];Fixture=$fixture[0]}
}

$repositoryRoot=(& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Require($LASTEXITCODE-eq 0 -and [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath((Get-Location).Path),[IO.Path]::GetFullPath($repositoryRoot))) 'Run from repository root.'
Require($BaselineSnapshotId-cne $CandidateSnapshotId) 'Snapshots must differ.'
$baseline=Load-Prepared $BaselineSnapshotId 'Baseline';$candidate=Load-Prepared $CandidateSnapshotId 'Candidate'
Require($baseline.Manifest.Count-eq $candidate.Manifest.Count) 'Source manifest counts differ.'
$differences=[Collections.Generic.List[string]]::new()
foreach($record in $candidate.Manifest){$old=@($baseline.Manifest|Where-Object Path -CEQ $record.Path);Require($old.Count-eq 1) "Baseline source missing: $($record.Path)";if($old[0].SHA256-cne $record.SHA256){$differences.Add($record.Path)}}
Require(($differences-join '|')-ceq 'FancyWM/Utilities/FocusHelper.cs') "Only FocusHelper.cs may differ: $($differences-join '|')"
Require($baseline.Provenance.FixtureSourceSHA256-ceq $candidate.Provenance.FixtureSourceSHA256 -and $baseline.Production.Hash-cne $candidate.Production.Hash) 'Common fixture source or distinct production binary requirement failed.'
Require((Hash $PSCommandPath)-ceq @($candidate.Manifest|Where-Object Path -CEQ 'scripts/performance/Measure-FocusConcurrentAdmission.ps1')[0].SHA256) 'Runner differs from candidate snapshot.'

$candidateRoot=Join-Path $repositoryRoot "artifacts/performance/$CandidateSnapshotId"
$experimentRoot=Join-Path $candidateRoot $ExperimentId
Require(!(Test-Path $experimentRoot)) 'Immutable experiment already exists.'
New-Item -ItemType Directory -Path $experimentRoot|Out-Null
$dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
& $dotnet --info *> (Join-Path $experimentRoot 'dotnet-info.log');Require($LASTEXITCODE-eq 0) 'dotnet --info failed.'
$units=[ordered]@{
 'first-caller-allocated-bytes'='bytes/first admission';'warm-caller-allocated-bytes'='bytes/warm admission';'lazy-delegate-bytes'='bytes/sequence';
 'admissions'='admissions/scenario';'worker-starts'='threads/scenario';'worker-exits'='threads/scenario';'calls'='calls/scenario';'accepted'='requests/scenario';'replaced'='requests/scenario';
 'true-results'='results/scenario';'false-results'='results/scenario';'background-workers'='threads/scenario';'overlapped-callers'='callers/scenario';
 'caller-allocated-bytes'='bytes/scenario';'total-allocated-bytes'='bytes/scenario';'lock-wait-lower-bound-ticks'='ticks/scenario';'enqueue-elapsed-ticks'='ticks/scenario';
 'timestamp-frequency'='ticks/second';'queue-calls'='calls/scenario';'attach-calls'='calls/scenario';'detach-calls'='calls/scenario';'focus-calls'='calls/scenario';'owned-attachments'='attachments/end';
 'superseded'='requests/scenario';'completion-callbacks'='callbacks/scenario';'completion-order-checksum'='index-sum/scenario';'latest-current'='requests/end';'worker-thread-count'='threads/scenario';
 'worker-transition-allocated-bytes'='bytes/255 transitions';'worker-transition-min-bytes'='bytes/transition';'worker-transition-max-bytes'='bytes/transition';'thread-id-reads'='reads/scenario';
 'foreground-id-reads'='reads/scenario';'alt-presses'='calls/scenario';'native-timeouts'='timeouts/scenario'
}
$scenarios=@('focus-first-worker-delegate','focus-concurrent-enqueue','focus-worker-overlap-direct-success','focus-worker-overlap-alt-retry','focus-worker-overlap-attach-failure')
$metricsByScenario=@{
 'focus-first-worker-delegate'=@('first-caller-allocated-bytes','warm-caller-allocated-bytes','lazy-delegate-bytes','admissions','worker-starts','worker-exits')
 'focus-concurrent-enqueue'=@('calls','accepted','replaced','true-results','false-results','worker-starts','worker-exits','background-workers','overlapped-callers','caller-allocated-bytes','total-allocated-bytes','lock-wait-lower-bound-ticks','enqueue-elapsed-ticks','timestamp-frequency','queue-calls','attach-calls','detach-calls','focus-calls','owned-attachments')
}
$overlapMetrics=@('admissions','accepted','superseded','completion-callbacks','completion-order-checksum','true-results','false-results','latest-current','worker-starts','worker-exits','background-workers','worker-thread-count','worker-transition-allocated-bytes','worker-transition-min-bytes','worker-transition-max-bytes','caller-allocated-bytes','total-allocated-bytes','enqueue-elapsed-ticks','timestamp-frequency','queue-calls','thread-id-reads','foreground-id-reads','attach-calls','detach-calls','focus-calls','alt-presses','owned-attachments','native-timeouts')
foreach($scenario in $scenarios|Where-Object {$_ -clike 'focus-worker-overlap-*'}){$metricsByScenario[$scenario]=$overlapMetrics}
$expected=@{
 'focus-first-worker-delegate'=@{admissions=2;'worker-starts'=2;'worker-exits'=2}
 'focus-concurrent-enqueue'=@{calls=32;accepted=32;replaced=31;'true-results'=1;'false-results'=31;'worker-starts'=1;'worker-exits'=1;'background-workers'=1;'overlapped-callers'=32;'queue-calls'=1;'attach-calls'=1;'detach-calls'=1;'focus-calls'=1;'owned-attachments'=0}
 'focus-worker-overlap-direct-success'=@{admissions=256;accepted=256;superseded=255;'completion-callbacks'=256;'completion-order-checksum'=32640;'true-results'=1;'false-results'=255;'latest-current'=1;'worker-starts'=1;'worker-exits'=1;'background-workers'=1;'worker-thread-count'=1;'queue-calls'=256;'thread-id-reads'=1;'foreground-id-reads'=1;'attach-calls'=1;'detach-calls'=1;'focus-calls'=1;'alt-presses'=0;'owned-attachments'=0;'native-timeouts'=0}
 'focus-worker-overlap-alt-retry'=@{admissions=256;accepted=256;superseded=255;'completion-callbacks'=256;'completion-order-checksum'=32640;'true-results'=1;'false-results'=255;'latest-current'=1;'worker-starts'=1;'worker-exits'=1;'background-workers'=1;'worker-thread-count'=1;'queue-calls'=256;'thread-id-reads'=1;'foreground-id-reads'=1;'attach-calls'=1;'detach-calls'=1;'focus-calls'=2;'alt-presses'=1;'owned-attachments'=0;'native-timeouts'=0}
 'focus-worker-overlap-attach-failure'=@{admissions=256;accepted=256;superseded=255;'completion-callbacks'=256;'completion-order-checksum'=32640;'true-results'=0;'false-results'=256;'latest-current'=1;'worker-starts'=1;'worker-exits'=1;'background-workers'=1;'worker-thread-count'=1;'queue-calls'=256;'thread-id-reads'=1;'foreground-id-reads'=1;'attach-calls'=1;'detach-calls'=0;'focus-calls'=0;'alt-presses'=0;'owned-attachments'=0;'native-timeouts'=0}
}
$rows=[Collections.Generic.List[object]]::new();$raw=[Collections.Generic.List[object]]::new();$processes=[Collections.Generic.List[object]]::new();$values=[Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
$filter='FullyQualifiedName=FancyWM.Tests.Utilities.FocusHelperTest.FocusConcurrentAdmissionCounterScenario'
$order=[Collections.Generic.List[string]]::new()
foreach($pair in 1..5){$variants=if($pair%2){@('baseline','candidate')}else{@('candidate','baseline')};foreach($variant in $variants){
 $run="$variant-$pair";$order.Add($run);$selected=if($variant-ceq 'baseline'){$baseline}else{$candidate}
 $runRoot=Join-Path $experimentRoot "runs/$run";New-Item -ItemType Directory -Path $runRoot|Out-Null
 foreach($record in $candidate.Archive){$sourceRoot=if($record.Path-ceq 'FancyWM.dll'){$selected.Root}else{$candidate.Root};$source=Join-Path $sourceRoot "release-test-binaries/$($record.Path)";$dest=Join-Path $runRoot $record.Path;New-Item -ItemType Directory -Force -Path (Split-Path $dest)|Out-Null;Copy-Item -LiteralPath $source -Destination $dest}
 $assembly=[IO.Path]::GetFullPath((Join-Path $runRoot 'FancyWM.Tests.dll'));$trx=Join-Path $experimentRoot "$run.trx";$stdoutPath=Join-Path $experimentRoot "$run.stdout.log";$stderrPath=Join-Path $experimentRoot "$run.stderr.log"
 $args=@('vstest',$assembly,'/Platform:x64',"/TestCaseFilter:$filter","/Logger:trx;LogFileName=$run.trx","/ResultsDirectory:$experimentRoot")
 $si=[Diagnostics.ProcessStartInfo]::new();$si.FileName=$dotnet;$si.WorkingDirectory=$runRoot;$si.UseShellExecute=$false;$si.CreateNoWindow=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true;$si.Environment['DOTNET_TieredCompilation']='0';foreach($a in $args){$si.ArgumentList.Add($a)}
 $p=[Diagnostics.Process]::new();$p.StartInfo=$si;$start=[DateTimeOffset]::UtcNow
 try{Require($p.Start()) "Cannot start $run";$pidValue=$p.Id;$outTask=$p.StandardOutput.ReadToEndAsync();$errTask=$p.StandardError.ReadToEndAsync();$timedOut=!$p.WaitForExit(300000);if($timedOut){$p.Kill($true);$p.WaitForExit()};$stdout=$outTask.GetAwaiter().GetResult();$stderr=$errTask.GetAwaiter().GetResult();$exit=$p.ExitCode;$finish=[DateTimeOffset]::UtcNow}finally{$p.Dispose()}
 [IO.File]::WriteAllText($stdoutPath,$stdout,[Text.UTF8Encoding]::new($false));[IO.File]::WriteAllText($stderrPath,$stderr,[Text.UTF8Encoding]::new($false));Require(!$timedOut -and $exit-eq 0) "Measurement process failed: $run"
 $doc=Load-Trx $trx;$leaf=@($doc.SelectNodes('.//*[local-name()="UnitTestResult"]')|Where-Object{$_.SelectNodes('./*[local-name()="InnerResults"]/*[local-name()="UnitTestResult"]').Count-eq 0});Require($leaf.Count-eq 1 -and $leaf[0].testName-ceq 'FocusConcurrentAdmissionCounterScenario' -and $leaf[0].outcome-ceq 'Passed') "Counter TRX differs: $run"
 $counterLines=@($leaf[0].SelectSingleNode('./*[local-name()="Output"]/*[local-name()="StdOut"]').InnerText -split '\r?\n'|Where-Object{$_ -cmatch '^PERFCOUNTER '});Require($counterLines.Count-eq 109) "Expected 109 counters: $run/$($counterLines.Count)"
 $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 foreach($line in $counterLines){$m=[regex]::Match($line,'^PERFCOUNTER (focus-(?:first-worker-delegate|concurrent-enqueue|worker-overlap-(?:direct-success|alt-retry|attach-failure))) ([a-z0-9-]+) (0|[1-9][0-9]*)$');Require($m.Success) "Malformed counter: $run/$line";$scenario=$m.Groups[1].Value;$metric=$m.Groups[2].Value;$value=[long]::Parse($m.Groups[3].Value,[Globalization.CultureInfo]::InvariantCulture);Require($scenario-cin $scenarios -and $metric-cin $metricsByScenario[$scenario] -and $seen.Add("$scenario/$metric") -and $values.TryAdd("$variant/$pair/$scenario/$metric",$value)) "Unexpected/duplicate counter: $run/$scenario/$metric";if($expected[$scenario].ContainsKey($metric)){Require($value-eq $expected[$scenario][$metric]) "Semantic counter differs: $run/$scenario/$metric"};$raw.Add([pscustomobject]@{RunId=$run;Variant=$variant;Pair=$pair;Scenario=$scenario;Metric=$metric;Value=$value;Line=$line});$iterations=if($scenario-clike 'focus-worker-overlap-*'){256}elseif($scenario-ceq 'focus-concurrent-enqueue'){32}else{2};$rows.Add([pscustomobject][ordered]@{experiment_id=$ExperimentId;snapshot_id=$selected.Id;scenario="Actual FocusHelper.RequestSequence.Enqueue/RunWorker; $scenario; controlled overlap";configuration='Release; net10.0-windows10.0.18362.0; x64; isolated process; DOTNET_TieredCompilation=0';window_count=0;metric=$metric;unit=$units[$metric];run=$pair;value=[string]$value;iterations=$iterations;instrument='GC.GetAllocatedBytesForCurrentThread worker samples; caller-thread and GC.GetTotalAllocatedBytes separately; Stopwatch lock/enqueue ticks; fake INative ownership/call counters; native CPU/GPU/latency NOT_MEASURED';perf_id='PERF-030';variant=$variant;pair=$pair;binary_sha256=$selected.Production.Hash;benchmark_sha256=$candidate.Fixture.Hash})}
 foreach($scenario in $scenarios){foreach($metric in $metricsByScenario[$scenario]){Require($seen.Contains("$scenario/$metric")) "Missing counter: $run/$scenario/$metric"}}
 $processes.Add([pscustomobject]@{RunId=$run;Variant=$variant;Pair=$pair;ProcessId=$pidValue;Start=$start.ToString('o');Finish=$finish.ToString('o');TrxSHA256=Hash $trx;StdOutSHA256=Hash $stdoutPath;StdErrSHA256=Hash $stderrPath;FancyWMDllSHA256=$selected.Production.Hash;FancyWMTestsDllSHA256=$candidate.Fixture.Hash})
}}
$expectedOrder='baseline-1|candidate-1|candidate-2|baseline-2|baseline-3|candidate-3|candidate-4|baseline-4|baseline-5|candidate-5';Require(($order-join '|')-ceq $expectedOrder -and $rows.Count-eq 1090) 'Process order or row count differs.'
for($i=1;$i-lt $processes.Count;$i++){Require([DateTimeOffset]::Parse($processes[$i].Start)-ge [DateTimeOffset]::Parse($processes[$i-1].Finish)) 'Measurement processes overlap.'}
$semanticMetrics=@('admissions','worker-starts','worker-exits','calls','accepted','replaced','true-results','false-results','background-workers','overlapped-callers','queue-calls','attach-calls','detach-calls','focus-calls','owned-attachments','superseded','completion-callbacks','completion-order-checksum','latest-current','worker-thread-count','thread-id-reads','foreground-id-reads','alt-presses','native-timeouts')
$allocationMetrics=@('first-caller-allocated-bytes','warm-caller-allocated-bytes','lazy-delegate-bytes','caller-allocated-bytes','total-allocated-bytes','worker-transition-allocated-bytes','worker-transition-min-bytes','worker-transition-max-bytes')
$timingMetrics=@('lock-wait-lower-bound-ticks','enqueue-elapsed-ticks','timestamp-frequency')
$paired=[Collections.Generic.List[object]]::new();$semanticComparisons=0;$allocationWins=0;$timingWins=0;$timingLosses=0;$timingTies=0
foreach($scenario in $scenarios){foreach($pair in 1..5){foreach($metric in $metricsByScenario[$scenario]){$b=$values["baseline/$pair/$scenario/$metric"];$c=$values["candidate/$pair/$scenario/$metric"];if($metric-cin $semanticMetrics){Require($b-eq $c) "Semantic A/B mismatch: $pair/$scenario/$metric";$semanticComparisons++};if($metric-ceq 'worker-transition-allocated-bytes'){Require($c-lt $b) "Worker transition allocation did not improve: $pair/$scenario";$allocationWins++};if($metric-ceq 'enqueue-elapsed-ticks'){$timing=if($c-lt $b){$timingWins++;'win'}elseif($c-gt $b){$timingLosses++;'loss'}else{$timingTies++;'tie'};$paired.Add([pscustomobject]@{Scenario=$scenario;Pair=$pair;BaselineEnqueueTicks=$b;CandidateEnqueueTicks=$c;Timing=$timing;BaselineWorkerTransitionBytes=if($scenario-clike 'focus-worker-overlap-*'){$values["baseline/$pair/$scenario/worker-transition-allocated-bytes"]}else{$null};CandidateWorkerTransitionBytes=if($scenario-clike 'focus-worker-overlap-*'){$values["candidate/$pair/$scenario/worker-transition-allocated-bytes"]}else{$null}})}}}}
Require($allocationWins-eq 15 -and $semanticComparisons-eq 400) 'Paired acceptance counts differ.'
$rows|Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'measurements.csv');$raw|Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'raw-counters.csv');$processes|Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'process-times.csv');$paired|Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'paired-results.csv')
$summary=@(foreach($scenario in $scenarios|Where-Object{$_ -clike 'focus-worker-overlap-*'}){$bp=@(1..5|ForEach-Object{$values["baseline/$_/$scenario/worker-transition-allocated-bytes"]});$cp=@(1..5|ForEach-Object{$values["candidate/$_/$scenario/worker-transition-allocated-bytes"]});[pscustomobject]@{Scenario=$scenario;BaselineMedianWorkerTransitionBytes=Median([double[]]$bp);CandidateMedianWorkerTransitionBytes=Median([double[]]$cp);Transitions=255}})
$evidence=@('measurements.csv','raw-counters.csv','process-times.csv','paired-results.csv','dotnet-info.log'|ForEach-Object{[pscustomobject]@{Path=$_;SHA256=Hash(Join-Path $experimentRoot $_)}})
$provenance=[ordered]@{ExperimentId=$ExperimentId;PerfId='PERF-030';Decision='MEASUREMENT_CHECKS_PASS';BaselineSnapshotId=$BaselineSnapshotId;CandidateSnapshotId=$CandidateSnapshotId;SourceDifferences=@($differences);OnlyFancyWMDllDiffersInRunBinaries=$true;BaselineFancyWMDllSHA256=$baseline.Production.Hash;CandidateFancyWMDllSHA256=$candidate.Production.Hash;CommonFancyWMTestsDllSHA256=$candidate.Fixture.Hash;ProcessCount=10;ProcessOrder=$expectedOrder;MeasurementRows=$rows.Count;RawCounters=$raw.Count;AllocationPairsImproved=$allocationWins;SemanticComparisons=$semanticComparisons;TimingWins=$timingWins;TimingLosses=$timingLosses;TimingTies=$timingTies;AllocationScope='Worker-thread transition samples are direct per-thread readings; caller and all-thread totals are separate. First lazy ThreadStart delegate is separately reported.';TimingScope='Managed enqueue and forced lock-wait ticks are descriptive and include OS scheduling; no timing win is required.';NotMeasured=@('native focus timing','thread handles','whole-process CPU','GPU','COM','WPF','DPI','presentation latency');Summaries=$summary;Evidence=$evidence}
$provenance|ConvertTo-Json -Depth 8|Set-Content (Join-Path $experimentRoot 'measurement-provenance.json');$provenance|ConvertTo-Json -Depth 8
