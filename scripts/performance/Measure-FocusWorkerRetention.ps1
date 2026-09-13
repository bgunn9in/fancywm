param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$ExperimentId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }
function Load-Trx([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    }
    finally { $reader.Dispose() }
}

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Require ($LASTEXITCODE -eq 0 -and [StringComparer]::OrdinalIgnoreCase.Equals(
    [IO.Path]::GetFullPath((Get-Location).Path), [IO.Path]::GetFullPath($repositoryRoot))) 'Run from repository root.'
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$manifestPath = Join-Path $snapshotRoot 'manifest.csv'
$archiveManifestPath = Join-Path $snapshotRoot 'release-test-binaries.csv'
$archiveRoot = Join-Path $snapshotRoot 'release-test-binaries'
$preparedPath = Join-Path $snapshotRoot 'release-test-binaries.provenance.json'
Require ((Test-Path $manifestPath) -and (Test-Path $archiveManifestPath) -and (Test-Path $preparedPath)) 'Prepared snapshot is missing.'
$manifest = @(Import-Csv $manifestPath)
$runner = @($manifest | Where-Object Path -CEQ 'scripts/performance/Measure-FocusWorkerRetention.ps1')
Require ($runner.Count -eq 1 -and $runner[0].SHA256 -ceq (Hash $PSCommandPath)) 'Executing runner differs from frozen source.'
foreach ($record in $manifest) {
    Require ((Hash (Join-Path $snapshotRoot "source/$($record.Path)")) -ceq $record.SHA256) "Frozen source differs: $($record.Path)"
}
$archive = @(Import-Csv $archiveManifestPath)
$prepared = Get-Content -Raw $preparedPath | ConvertFrom-Json -DateKind String
Require ($prepared.SnapshotId -ceq $SnapshotId -and $prepared.ArchiveManifestSHA256 -ceq (Hash $archiveManifestPath)) 'Prepared binary provenance differs.'
foreach ($record in $archive) {
    $path = Join-Path $archiveRoot $record.Path
    Require ((Hash $path) -ceq $record.Hash -and (Get-Item $path).Length -eq [long]$record.Length) "Archived binary differs: $($record.Path)"
}
$production = @($archive | Where-Object Path -CEQ 'FancyWM.dll')
$fixture = @($archive | Where-Object Path -CEQ 'FancyWM.Tests.dll')
Require ($production.Count -eq 1 -and $fixture.Count -eq 1) 'Production or fixture binary is not unique.'

$experimentRoot = Join-Path $snapshotRoot $ExperimentId
Require (!(Test-Path $experimentRoot)) 'Immutable experiment already exists.'
New-Item -ItemType Directory -Path $experimentRoot | Out-Null
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
& $dotnet --info *> (Join-Path $experimentRoot 'dotnet-info.log')
Require ($LASTEXITCODE -eq 0) 'dotnet --info failed.'
$environment = [ordered]@{
    SnapshotId = $SnapshotId
    ExperimentId = $ExperimentId
    OSVersion = [Environment]::OSVersion.VersionString
    Framework = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
    ProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    ProcessorCount = [Environment]::ProcessorCount
    DOTNET_TieredCompilation = '0'
    NativeCalls = $false
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
}
$environment | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $experimentRoot 'environment.json')

$metrics = @{
    'focus-worker-active' = @(
        'baseline-process-handles','active-process-handles','settled-process-handles',
        'active-handle-delta','settled-handle-delta','baseline-process-threads',
        'active-process-threads','settled-process-threads','active-thread-delta',
        'settled-thread-delta','worker-starts','worker-exits','worker-field-cleared')
    'focus-worker-retention-completed' = @(
        'lifetimes','admissions','true-results','false-results','worker-starts',
        'worker-exits','background-workers','managed-worker-thread-ids','queue-calls',
        'attach-calls','detach-calls','focus-calls','owned-attachments',
        'worker-field-cleared','live-worker-references','baseline-process-handles',
        'settled-process-handles','settled-handle-delta','baseline-process-threads',
        'settled-process-threads','settled-thread-delta')
    'focus-worker-retention-overlap-first' = @(
        'lifetimes','admissions','true-results','false-results','worker-starts',
        'worker-exits','background-workers','managed-worker-thread-ids','queue-calls',
        'attach-calls','detach-calls','focus-calls','owned-attachments',
        'worker-field-cleared','live-worker-references','baseline-process-handles',
        'settled-process-handles','settled-handle-delta','baseline-process-threads',
        'settled-process-threads','settled-thread-delta')
    'focus-worker-retention-overlap-repeat' = @(
        'lifetimes','admissions','true-results','false-results','worker-starts',
        'worker-exits','background-workers','managed-worker-thread-ids','queue-calls',
        'attach-calls','detach-calls','focus-calls','owned-attachments',
        'worker-field-cleared','live-worker-references','baseline-process-handles',
        'settled-process-handles','settled-handle-delta','baseline-process-threads',
        'settled-process-threads','settled-thread-delta')
}
$expected = @{
    'focus-worker-active' = @{'active-thread-delta'=1;'settled-thread-delta'=0;'worker-starts'=1;'worker-exits'=1;'worker-field-cleared'=1}
    'focus-worker-retention-completed' = @{lifetimes=128;admissions=128;'true-results'=128;'false-results'=0;'worker-starts'=128;'worker-exits'=128;'background-workers'=128;'managed-worker-thread-ids'=128;'queue-calls'=128;'attach-calls'=128;'detach-calls'=128;'focus-calls'=128;'owned-attachments'=0;'worker-field-cleared'=1;'live-worker-references'=0;'settled-thread-delta'=0}
    'focus-worker-retention-overlap-first' = @{lifetimes=64;admissions=1024;'true-results'=64;'false-results'=960;'worker-starts'=64;'worker-exits'=64;'background-workers'=64;'managed-worker-thread-ids'=64;'queue-calls'=128;'attach-calls'=64;'detach-calls'=64;'focus-calls'=64;'owned-attachments'=0;'worker-field-cleared'=1;'live-worker-references'=0;'settled-thread-delta'=0}
    'focus-worker-retention-overlap-repeat' = @{lifetimes=64;admissions=1024;'true-results'=64;'false-results'=960;'worker-starts'=64;'worker-exits'=64;'background-workers'=64;'managed-worker-thread-ids'=64;'queue-calls'=128;'attach-calls'=64;'detach-calls'=64;'focus-calls'=64;'owned-attachments'=0;'worker-field-cleared'=1;'live-worker-references'=0;'settled-thread-delta'=0}
}
$units = @{
    lifetimes='worker lifetimes/process';admissions='admissions/process';'true-results'='results/process';'false-results'='results/process';
    'worker-starts'='threads/process';'worker-exits'='threads/process';'background-workers'='threads/process';'managed-worker-thread-ids'='managed thread IDs/process';
    'queue-calls'='calls/process';'attach-calls'='calls/process';'detach-calls'='calls/process';'focus-calls'='calls/process';'owned-attachments'='attachments/end';
    'worker-field-cleared'='boolean';'live-worker-references'='managed objects/end';'baseline-process-handles'='process handles';'active-process-handles'='process handles';
    'settled-process-handles'='process handles';'active-handle-delta'='process handles';'settled-handle-delta'='process handles';
    'baseline-process-threads'='process threads';'active-process-threads'='process threads';'settled-process-threads'='process threads';
    'active-thread-delta'='process threads';'settled-thread-delta'='process threads'
}
$rows = [Collections.Generic.List[object]]::new()
$raw = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[object]]::new()
$values = [Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
$filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.FocusHelperTest.FocusWorkerRetentionCounterScenario'
$previousFinish = $null
foreach ($run in 1..5) {
    $runId = "candidate-$run"
    $runRoot = Join-Path $experimentRoot "runs/$runId"
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    foreach ($record in $archive) {
        $source = Join-Path $archiveRoot $record.Path
        $destination = Join-Path $runRoot $record.Path
        New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }
    $assembly = [IO.Path]::GetFullPath((Join-Path $runRoot 'FancyWM.Tests.dll'))
    $trx = Join-Path $experimentRoot "$runId.trx"
    $stdoutPath = Join-Path $experimentRoot "$runId.stdout.log"
    $stderrPath = Join-Path $experimentRoot "$runId.stderr.log"
    $arguments = @('vstest',$assembly,'/Platform:x64',"/TestCaseFilter:$filter", "/Logger:trx;LogFileName=$runId.trx", "/ResultsDirectory:$experimentRoot")
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dotnet
    $startInfo.WorkingDirectory = $runRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['DOTNET_TieredCompilation'] = '0'
    foreach ($argument in $arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = [DateTimeOffset]::UtcNow
    try {
        Require ($process.Start()) "Cannot start $runId"
        $pidValue = $process.Id
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $timedOut = !$process.WaitForExit(300000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
        $finished = [DateTimeOffset]::UtcNow
    }
    finally { $process.Dispose() }
    [IO.File]::WriteAllText($stdoutPath, $stdout, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($stderrPath, $stderr, [Text.UTF8Encoding]::new($false))
    Require (!$timedOut -and $exitCode -eq 0) "Measurement process failed: $runId"
    if ($previousFinish) { Require ($started -ge $previousFinish) 'Measurement processes overlap.' }
    $previousFinish = $finished
    $document = Load-Trx $trx
    $leaves = @($document.SelectNodes('.//*[local-name()="UnitTestResult"]') | Where-Object {
        $_.SelectNodes('./*[local-name()="InnerResults"]/*[local-name()="UnitTestResult"]').Count -eq 0
    })
    Require ($leaves.Count -eq 1 -and $leaves[0].testName -ceq 'FocusWorkerRetentionCounterScenario' -and $leaves[0].outcome -ceq 'Passed') "Counter TRX differs: $runId"
    $counterLines = @($leaves[0].SelectSingleNode('./*[local-name()="Output"]/*[local-name()="StdOut"]').InnerText -split '\r?\n' | Where-Object { $_ -cmatch '^PERFCOUNTER ' })
    Require ($counterLines.Count -eq 76) "Expected 76 counters: $runId/$($counterLines.Count)"
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in $counterLines) {
        $match = [regex]::Match($line, '^PERFCOUNTER (focus-worker-(?:active|retention-(?:completed|overlap-(?:first|repeat)))) ([a-z0-9-]+) (-?[0-9]+)$')
        Require ($match.Success) "Malformed counter: $runId/$line"
        $scenario = $match.Groups[1].Value
        $metric = $match.Groups[2].Value
        $value = [long]::Parse($match.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
        Require ($metrics.ContainsKey($scenario) -and $metric -cin $metrics[$scenario] -and $seen.Add("$scenario/$metric") -and $values.TryAdd("$run/$scenario/$metric", $value)) "Unexpected or duplicate counter: $runId/$scenario/$metric"
        if ($expected[$scenario].ContainsKey($metric)) { Require ($value -eq $expected[$scenario][$metric]) "Semantic counter differs: $runId/$scenario/$metric" }
        $raw.Add([pscustomobject]@{RunId=$runId;Run=$run;Scenario=$scenario;Metric=$metric;Value=$value;Line=$line})
        $rows.Add([pscustomobject][ordered]@{
            experiment_id=$ExperimentId;snapshot_id=$SnapshotId;scenario="Actual FocusHelper.RequestSequence.Enqueue/RunWorker; $scenario; controlled managed retention";
            configuration='Release; net10.0-windows10.0.18362.0; x64; isolated process; DOTNET_TieredCompilation=0';window_count=0;metric=$metric;unit=$units[$metric];
            run=$run;value=[string]$value;iterations=if($scenario-ceq 'focus-worker-retention-completed'){128}elseif($scenario-clike 'focus-worker-retention-overlap-*'){64}else{1};
            instrument='Process.HandleCount and Process.Threads.Count stable-min samples; WeakReference<Thread>; RequestSequence.m_worker reflection; fake INative counters; no native focus calls';
            perf_id='PERF-030';variant='candidate-observation';pair=$run;binary_sha256=$production[0].Hash;benchmark_sha256=$fixture[0].Hash
        })
    }
    foreach ($scenario in $metrics.Keys) { foreach ($metric in $metrics[$scenario]) { Require ($seen.Contains("$scenario/$metric")) "Missing counter: $runId/$scenario/$metric" } }
    foreach ($scenario in @('focus-worker-active','focus-worker-retention-completed','focus-worker-retention-overlap-repeat')) {
        Require ($values["$run/$scenario/settled-thread-delta"] -eq 0) "Settled process thread count grew: $runId/$scenario"
        $handleDelta = $values["$run/$scenario/settled-handle-delta"]
        Require ($handleDelta -ge 0 -and $handleDelta -le 1) "Settled process handle count is outside the predeclared 0..1 bound: $runId/$scenario/$handleDelta"
    }
    $firstOverlapHandleDelta = $values["$run/focus-worker-retention-overlap-first/settled-handle-delta"]
    Require ($values["$run/focus-worker-retention-overlap-first/settled-thread-delta"] -eq 0 -and $firstOverlapHandleDelta -ge 0 -and $firstOverlapHandleDelta -le 3) "First overlap epoch exceeds its diagnostic initialization bound: $runId"
    Require ($values["$run/focus-worker-active/active-thread-delta"] -eq 1) "Active worker process-thread footprint differs: $runId"
    $processes.Add([pscustomobject]@{RunId=$runId;Run=$run;ProcessId=$pidValue;StartedUtc=$started.ToString('o');FinishedUtc=$finished.ToString('o');ExitCode=$exitCode;TrxSHA256=Hash $trx;StdOutSHA256=Hash $stdoutPath;StdErrSHA256=Hash $stderrPath;FancyWMDllSHA256=$production[0].Hash;FancyWMTestsDllSHA256=$fixture[0].Hash})
}
Require ($rows.Count -eq 380 -and $raw.Count -eq 380 -and $processes.Count -eq 5) 'Measurement totals differ.'
$rows | Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'measurements.csv')
$raw | Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'raw-counters.csv')
$processes | Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'process-times.csv')
$evidence = @('measurements.csv','raw-counters.csv','process-times.csv','environment.json','dotnet-info.log' | ForEach-Object {
    [pscustomobject]@{Path=$_;SHA256=Hash (Join-Path $experimentRoot $_)}
})
$provenance = [ordered]@{
    Verdict='MEASUREMENT_CHECKS_PASS';SnapshotId=$SnapshotId;ExperimentId=$ExperimentId;PerfId='PERF-030';ProcessCount=5;ProcessesSequential=$true;
    MeasurementRows=$rows.Count;RawCounters=$raw.Count;WarmupCompletedWorkerLifetimes=640;WarmupOverlappedWorkerLifetimes=320;
    CompletedWorkerLifetimes=640;OverlappedWorkerLifetimes=640;OverlappedAdmissions=10240;
    Acceptance='Exact managed ownership/callback/native-adapter semantics; zero settled process-thread delta; first overlap epoch bounded at 0..3 handles and equal repeat epoch at 0..1.';
    Scope='Candidate-only observation after an equal-size warmup workload; no production delta and no A/B performance claim.';
    NotMeasured=@('native thread handles by type','native UIA timeout','late focus','whole-process CPU','GPU','COM','WPF','DPI','presentation latency');
    FancyWMDllSHA256=$production[0].Hash;FancyWMTestsDllSHA256=$fixture[0].Hash;Evidence=$evidence;CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')
}
$provenance | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $experimentRoot 'provenance.json')
$provenance | ConvertTo-Json -Depth 7
