param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$ExperimentId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Require ($LASTEXITCODE -eq 0 -and [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath((Get-Location).Path),[IO.Path]::GetFullPath($repositoryRoot))) 'Run from repository root.'
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$experimentRoot = Join-Path $snapshotRoot $ExperimentId
Require ((Test-Path $snapshotRoot) -and !(Test-Path $experimentRoot)) 'Snapshot is missing or experiment already exists.'
$manifest = @(Import-Csv (Join-Path $snapshotRoot 'manifest.csv'))
foreach ($record in $manifest) { Require ((Hash (Join-Path $snapshotRoot "source/$($record.Path)")) -ceq $record.SHA256) "Frozen source differs: $($record.Path)" }
$runner = @($manifest | Where-Object Path -CEQ 'scripts/performance/Measure-ResizeOperationSnapshotBoundary.ps1')
Require ($runner.Count -eq 1 -and $runner[0].SHA256 -ceq (Hash $PSCommandPath)) 'Executing runner differs from frozen source.'
$binaryRoot = Join-Path $snapshotRoot 'release-test-binaries'
$binaryManifest = @(Import-Csv (Join-Path $snapshotRoot 'release-test-binaries.csv'))
foreach ($record in $binaryManifest) {
    $path = Join-Path $binaryRoot $record.Path
    Require ((Hash $path) -ceq $record.SHA256 -and (Get-Item $path).Length -eq [long]$record.Length) "Archived binary differs: $($record.Path)"
}
$fancyDll = @($binaryManifest | Where-Object Path -CEQ 'FancyWM.dll')[0]
$testDll = @($binaryManifest | Where-Object Path -CEQ 'FancyWM.Tests.dll')[0]

New-Item -ItemType Directory -Path $experimentRoot | Out-Null
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
& $dotnet --info *> (Join-Path $experimentRoot 'dotnet-info.log')
Require ($LASTEXITCODE -eq 0) 'dotnet --info failed.'
$filter = 'FullyQualifiedName=FancyWM.Tests.AlgorithmicLayouts.TilingServiceAlgorithmicIntegrationTest.ResizeOperationSnapshotBoundaryCounterScenario'
$raw = [Collections.Generic.List[object]]::new()
$rows = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[object]]::new()
$previousFinish = $null
$units = @{
    'cycles'='operations';'display-count'='snapshot displays';'position-reads'='property reads';
    'snapshot-reads'='provider reads';'workarea-reads'='property reads';'true-results'='results';'false-results'='results'
}
foreach ($run in 1..5) {
    $runId = "current-$run"
    $trx = Join-Path $experimentRoot "$runId.trx"
    $stdoutPath = Join-Path $experimentRoot "$runId.stdout.log"
    $stderrPath = Join-Path $experimentRoot "$runId.stderr.log"
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName=$dotnet;$startInfo.WorkingDirectory=$binaryRoot;$startInfo.UseShellExecute=$false;$startInfo.CreateNoWindow=$true;$startInfo.RedirectStandardOutput=$true;$startInfo.RedirectStandardError=$true
    $startInfo.Environment['DOTNET_TieredCompilation']='0'
    foreach ($argument in @('vstest',(Join-Path $binaryRoot 'FancyWM.Tests.dll'),'/Platform:x64',"/TestCaseFilter:$filter","/Logger:trx;LogFileName=$runId.trx","/ResultsDirectory:$experimentRoot")) { $startInfo.ArgumentList.Add($argument) }
    $started=[DateTimeOffset]::UtcNow;$process=[Diagnostics.Process]::Start($startInfo);Require($null-ne $process)'Could not start isolated test process.'
    $stdoutTask=$process.StandardOutput.ReadToEndAsync();$stderrTask=$process.StandardError.ReadToEndAsync();$process.WaitForExit();$stdout=$stdoutTask.GetAwaiter().GetResult();$stderr=$stderrTask.GetAwaiter().GetResult();$finished=[DateTimeOffset]::UtcNow
    [IO.File]::WriteAllText($stdoutPath,$stdout);[IO.File]::WriteAllText($stderrPath,$stderr)
    Require ($process.ExitCode -eq 0 -and (Test-Path $trx)) "Isolated process failed: $runId"
    if ($null-ne $previousFinish) { Require ($started -ge $previousFinish) 'Measurement processes overlap.' }
    $previousFinish=$finished
    $processes.Add([pscustomobject]@{RunId=$runId;Run=$run;StartedUtc=$started.ToString('o');FinishedUtc=$finished.ToString('o');ExitCode=$process.ExitCode;TrxSHA256=Hash $trx})
    [xml]$document=Get-Content -Raw $trx
    $counter=$document.TestRun.ResultSummary.Counters
    Require ([int]$counter.total -eq 1 -and [int]$counter.passed -eq 1) "TRX result differs: $runId"
    $captured = [string]::Join("`n",@($document.SelectNodes("//*[local-name()='StdOut']") | ForEach-Object InnerText))
    $matches=[regex]::Matches($captured,'(?m)^PERFCOUNTER (?<scenario>resize-operation-snapshot-(?<count>1|10|50)) (?<metric>[a-z0-9-]+) (?<value>-?\d+)\s*$')
    Require ($matches.Count -eq 21) "Counter count differs: $runId"
    $keys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($match in $matches) {
        $scenario=$match.Groups['scenario'].Value;$count=[int]$match.Groups['count'].Value;$metric=$match.Groups['metric'].Value;$value=[long]$match.Groups['value'].Value
        Require ($units.ContainsKey($metric) -and $keys.Add("$scenario/$metric")) "Counter key differs: $runId/$scenario/$metric"
        $raw.Add([pscustomobject]@{Run=$run;Scenario=$scenario;DisplayCount=$count;Metric=$metric;Value=$value})
        $rows.Add([pscustomobject][ordered]@{
            experiment_id=$ExperimentId;snapshot_id=$SnapshotId;
            scenario="TilingService.CanResize; mutable IWindow.Position and IDisplay.WorkArea provider boundary; $count displays; 100 operations";
            configuration='Release; net10.0-windows10.0.18362.0; x64 testhost; isolated sequential process; DOTNET_TieredCompilation=0';
            window_count=$count;metric=$metric;unit=$units[$metric];run=$run;value=$value;iterations=100;
            instrument='Actual public CanResize generic-layout path with callback-backed window/display/provider properties; exact order and mutation regression; managed counts only; no native window/display/COM calls';
            perf_id='PERF-022';variant='current-safety-observation';pair=$run;binary_sha256=$fancyDll.SHA256;benchmark_sha256=$testDll.SHA256
        })
    }
}
$rows | Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'measurements.csv')
$raw | Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'raw-counters.csv')
$processes | Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'process-times.csv')
$provenance=[ordered]@{
    Verdict='MEASUREMENT_CHECKS_PASS';SnapshotId=$SnapshotId;ExperimentId=$ExperimentId;PerfId='PERF-022';
    ProcessCount=$processes.Count;MeasurementRows=$rows.Count;RawCounters=$raw.Count;Sequential=$true;NativeCalls=$false;ABPerformanceClaim=$false;
    FancyWMDllSHA256=$fancyDll.SHA256;FancyWMTestsDllSHA256=$testDll.SHA256;
    MeasurementsSHA256=Hash (Join-Path $experimentRoot 'measurements.csv');RawCountersSHA256=Hash (Join-Path $experimentRoot 'raw-counters.csv');ProcessTimesSHA256=Hash (Join-Path $experimentRoot 'process-times.csv');
    CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')
}
$provenance | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $experimentRoot 'provenance.json')
$provenance | ConvertTo-Json -Depth 4
