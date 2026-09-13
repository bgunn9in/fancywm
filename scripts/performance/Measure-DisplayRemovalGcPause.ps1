param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$CandidateSnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$BaselineSnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$ExperimentId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }
function Load-Trx([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try { $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null; $document.Load($reader); return $document }
    finally { $reader.Dispose() }
}

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Require ($LASTEXITCODE -eq 0 -and [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath((Get-Location).Path),[IO.Path]::GetFullPath($repositoryRoot))) 'Run from repository root.'
$candidateRoot = Join-Path $repositoryRoot "artifacts/performance/$CandidateSnapshotId"
$baselineRoot = Join-Path $repositoryRoot "artifacts/performance/$BaselineSnapshotId"
$experimentRoot = Join-Path $candidateRoot $ExperimentId
Require (!(Test-Path $experimentRoot) -and $CandidateSnapshotId -cne $BaselineSnapshotId) 'Experiment exists or snapshot IDs are equal.'

$candidateManifest = @(Import-Csv (Join-Path $candidateRoot 'manifest.csv'))
$baselineManifest = @(Import-Csv (Join-Path $baselineRoot 'manifest.csv'))
Require ($candidateManifest.Count -eq $baselineManifest.Count) 'Snapshot manifest dimensions differ.'
$differentSources = [Collections.Generic.List[string]]::new()
foreach ($record in $candidateManifest) {
    $other = @($baselineManifest | Where-Object Path -CEQ $record.Path)
    Require ($other.Count -eq 1 -and (Hash (Join-Path $candidateRoot "source/$($record.Path)")) -ceq $record.SHA256 -and
        (Hash (Join-Path $baselineRoot "source/$($record.Path)")) -ceq $other[0].SHA256) "Frozen source differs: $($record.Path)"
    if ($record.SHA256 -cne $other[0].SHA256) { $differentSources.Add($record.Path) }
}
Require ($differentSources.Count -eq 1 -and $differentSources[0] -ceq 'FancyWM/MultiDisplayTilingService.cs') 'A/B source delta is not exactly OnDisplayRemoved production source.'
$runner = @($candidateManifest | Where-Object Path -CEQ 'scripts/performance/Measure-DisplayRemovalGcPause.ps1')
Require ($runner.Count -eq 1 -and $runner[0].SHA256 -ceq (Hash $PSCommandPath)) 'Executing runner differs from frozen source.'

$candidateArchiveRoot = Join-Path $candidateRoot 'release-test-binaries'
$baselineArchiveRoot = Join-Path $baselineRoot 'release-test-binaries'
$candidateArchivePath = Join-Path $candidateRoot 'release-test-binaries.csv'
$baselineArchivePath = Join-Path $baselineRoot 'release-test-binaries.csv'
$candidateArchive = @(Import-Csv $candidateArchivePath)
$baselineArchive = @(Import-Csv $baselineArchivePath)
foreach ($record in $candidateArchive) {
    Require ((Hash (Join-Path $candidateArchiveRoot $record.Path)) -ceq $record.Hash -and
        (Get-Item (Join-Path $candidateArchiveRoot $record.Path)).Length -eq [long]$record.Length) "Candidate archived binary differs: $($record.Path)"
}
foreach ($record in $baselineArchive) {
    Require ((Hash (Join-Path $baselineArchiveRoot $record.Path)) -ceq $record.Hash -and
        (Get-Item (Join-Path $baselineArchiveRoot $record.Path)).Length -eq [long]$record.Length) "Baseline archived binary differs: $($record.Path)"
}
$candidateDll = @($candidateArchive | Where-Object Path -CEQ 'FancyWM.dll')
$candidateTests = @($candidateArchive | Where-Object Path -CEQ 'FancyWM.Tests.dll')
$baselineDll = @($baselineArchive | Where-Object Path -CEQ 'FancyWM.dll')
Require ($candidateDll.Count -eq 1 -and $candidateTests.Count -eq 1 -and $baselineDll.Count -eq 1 -and $candidateDll[0].Hash -cne $baselineDll[0].Hash) 'A/B binary identity differs.'

New-Item -ItemType Directory -Path $experimentRoot | Out-Null
$candidateRunRoot = Join-Path $experimentRoot 'candidate-binaries'
$baselineRunRoot = Join-Path $experimentRoot 'baseline-binaries'
New-Item -ItemType Directory -Path $candidateRunRoot,$baselineRunRoot | Out-Null
Copy-Item -Path (Join-Path $candidateArchiveRoot '*') -Destination $candidateRunRoot -Recurse
Copy-Item -Path (Join-Path $candidateArchiveRoot '*') -Destination $baselineRunRoot -Recurse
Copy-Item -LiteralPath (Join-Path $baselineArchiveRoot 'FancyWM.dll') -Destination (Join-Path $baselineRunRoot 'FancyWM.dll') -Force
$binaryChecks = @(Get-ChildItem -LiteralPath $candidateRunRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($candidateRunRoot,$_.FullName).Replace('\','/')
    $candidateHash = Hash $_.FullName; $baselineHash = Hash (Join-Path $baselineRunRoot $relative)
    Require (($relative -ceq 'FancyWM.dll') -eq ($candidateHash -cne $baselineHash)) "Unexpected A/B binary delta: $relative"
    [pscustomobject]@{Path=$relative;CandidateSHA256=$candidateHash;BaselineSHA256=$baselineHash}
})
$binaryChecks | Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'binary-checks.csv')

$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
& $dotnet --info *> (Join-Path $experimentRoot 'dotnet-info.log')
Require ($LASTEXITCODE -eq 0) 'dotnet --info failed.'
[ordered]@{
    CandidateSnapshotId=$CandidateSnapshotId;BaselineSnapshotId=$BaselineSnapshotId;ExperimentId=$ExperimentId;
    OSVersion=[Environment]::OSVersion.VersionString;Framework=[Runtime.InteropServices.RuntimeInformation]::FrameworkDescription;
    ProcessArchitecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString();ProcessorCount=[Environment]::ProcessorCount;
    DOTNET_TieredCompilation='0';NativeCalls=$false;CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $experimentRoot 'environment.json')

$metricNames = @('collection-requests','full-gc-passes','gen0-collections','gen1-collections','gen2-collections','allocated-bytes','elapsed-ticks','gc-pause-ticks','non-gc-ticks','timestamp-frequency','managed-bytes-before','managed-bytes-before-final-collection','settled-managed-bytes','removed-owners','duplicate-events','event-calls','cycles','churn-bytes','routing-checks','checksum')
$units = @{
    'collection-requests'='requests/process';'full-gc-passes'='forced full collections/process';'gen0-collections'='collections/process';'gen1-collections'='collections/process';'gen2-collections'='collections/process';
    'allocated-bytes'='bytes/workload';'elapsed-ticks'='ticks/workload';'gc-pause-ticks'='ticks inside forced collector';'non-gc-ticks'='ticks outside forced collector';'timestamp-frequency'='ticks/second';
    'managed-bytes-before'='managed bytes';'managed-bytes-before-final-collection'='managed bytes';'settled-managed-bytes'='managed bytes';'removed-owners'='owners/process';
    'duplicate-events'='events/process';'event-calls'='events/process';'cycles'='lifetimes/process';'churn-bytes'='requested bytes/process';'routing-checks'='checks/process';'checksum'='semantic checksum'
}
$rows = [Collections.Generic.List[object]]::new(); $raw = [Collections.Generic.List[object]]::new(); $processes = [Collections.Generic.List[object]]::new()
$filter = 'FullyQualifiedName=FancyWM.Tests.AlgorithmicLayouts.MultiDisplayTilingServiceIntegrationTest.DisplayRemovalForcedGcCounterScenario'
$previousFinish = $null
foreach ($pair in 1..5) {
    $variants = if ($pair % 2) { @('baseline','candidate') } else { @('candidate','baseline') }
    foreach ($variant in $variants) {
        $runId = "$variant-$pair"; $runRoot = if ($variant -ceq 'baseline') { $baselineRunRoot } else { $candidateRunRoot }
        $trx = Join-Path $experimentRoot "$runId.trx"; $stdoutPath = Join-Path $experimentRoot "$runId.stdout.log"; $stderrPath = Join-Path $experimentRoot "$runId.stderr.log"
        $arguments = @('vstest',(Join-Path $runRoot 'FancyWM.Tests.dll'),'/Platform:x64',"/TestCaseFilter:$filter","/Logger:trx;LogFileName=$runId.trx","/ResultsDirectory:$experimentRoot")
        $startInfo = [Diagnostics.ProcessStartInfo]::new(); $startInfo.FileName=$dotnet; $startInfo.WorkingDirectory=$runRoot; $startInfo.UseShellExecute=$false; $startInfo.CreateNoWindow=$true; $startInfo.RedirectStandardOutput=$true; $startInfo.RedirectStandardError=$true; $startInfo.Environment['DOTNET_TieredCompilation']='0'
        foreach ($argument in $arguments) { $startInfo.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new(); $process.StartInfo=$startInfo; $started=[DateTimeOffset]::UtcNow
        try {
            Require ($process.Start()) "Cannot start $runId"; $pidValue=$process.Id; $stdoutTask=$process.StandardOutput.ReadToEndAsync(); $stderrTask=$process.StandardError.ReadToEndAsync();
            $timedOut=!$process.WaitForExit(300000); if($timedOut){$process.Kill($true);$process.WaitForExit()}; $stdout=$stdoutTask.GetAwaiter().GetResult();$stderr=$stderrTask.GetAwaiter().GetResult();$exitCode=$process.ExitCode;$finished=[DateTimeOffset]::UtcNow
        } finally { $process.Dispose() }
        [IO.File]::WriteAllText($stdoutPath,$stdout,[Text.UTF8Encoding]::new($false));[IO.File]::WriteAllText($stderrPath,$stderr,[Text.UTF8Encoding]::new($false))
        Require (!$timedOut -and $exitCode -eq 0) "Measurement process failed: $runId"
        if($previousFinish){Require($started -ge $previousFinish)'Measurement processes overlap.'};$previousFinish=$finished
        $document=Load-Trx $trx;$leaves=@($document.SelectNodes('.//*[local-name()="UnitTestResult"]')|Where-Object{$_.SelectNodes('./*[local-name()="InnerResults"]/*[local-name()="UnitTestResult"]').Count-eq 0})
        Require($leaves.Count-eq 1 -and $leaves[0].testName-ceq 'DisplayRemovalForcedGcCounterScenario' -and $leaves[0].outcome-ceq 'Passed')"Counter TRX differs: $runId"
        $counterLines=@($leaves[0].SelectSingleNode('./*[local-name()="Output"]/*[local-name()="StdOut"]').InnerText -split '\r?\n'|Where-Object{$_ -cmatch '^PERFCOUNTER '})
        Require($counterLines.Count-eq 60)"Expected 60 counters: $runId/$($counterLines.Count)";$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($line in $counterLines){
            $match=[regex]::Match($line,'^PERFCOUNTER display-removal-gc-pause-(1|10|50) ([a-z0-9-]+) (0|[1-9][0-9]*)$');Require($match.Success)"Malformed counter: $runId/$line"
            $duplicates=[int]$match.Groups[1].Value;$metric=$match.Groups[2].Value;$value=[long]$match.Groups[3].Value;$key="$duplicates/$metric"
            Require($metric -cin $metricNames -and $seen.Add($key))"Unexpected/duplicate counter: $runId/$key"
            $expectedRequests=if($variant-ceq 'candidate'){3}else{3*($duplicates+1)}
            switch($metric){
                'collection-requests'{Require($value-eq $expectedRequests)"Collection requests differ: $runId/$duplicates"}
                'full-gc-passes'{Require($value-eq 2*$expectedRequests)"Full GC passes differ: $runId/$duplicates"}
                {$_ -cin @('gen0-collections','gen1-collections','gen2-collections')}{Require($value-eq 2*$expectedRequests)"GC generation count differs: $runId/$duplicates/$metric"}
                'removed-owners'{Require($value-eq 3)"Removed owners differ: $runId/$duplicates"}
                'duplicate-events'{Require($value-eq 3*$duplicates)"Duplicate events differ: $runId/$duplicates"}
                'event-calls'{Require($value-eq 3*($duplicates+1))"Event calls differ: $runId/$duplicates"}
                'cycles'{Require($value-eq 3)"Cycles differ: $runId/$duplicates"}
                'churn-bytes'{Require($value-eq 3L*($duplicates+1)*65536)"Churn differs: $runId/$duplicates"}
                'routing-checks'{Require($value-eq 3)"Routing checks differ: $runId/$duplicates"}
                'timestamp-frequency'{Require($value-eq [Diagnostics.Stopwatch]::Frequency)"Timestamp frequency differs: $runId"}
            }
            $raw.Add([pscustomobject]@{RunId=$runId;Pair=$pair;Variant=$variant;Duplicates=$duplicates;Metric=$metric;Value=$value;Line=$line})
            $rows.Add([pscustomobject][ordered]@{experiment_id=$ExperimentId;snapshot_id=if($variant-ceq 'baseline'){$BaselineSnapshotId}else{$CandidateSnapshotId};scenario="Actual MultiDisplayTilingService.OnDisplayRemoved; $duplicates duplicate events per removed owner;3 equal lifetimes;two compacting full collections per admitted request";configuration='Release; net10.0-windows10.0.18362.0; x64 testhost; isolated sequential process; DOTNET_TieredCompilation=0';window_count=0;metric=$metric;unit=$units[$metric];run=$pair;value=[string]$value;iterations=3;instrument='Injected callback executes the exact GCHelper two-pass forced compacting full collection and WaitForPendingFinalizers; process-wide allocated bytes, managed heap samples, generation counts and Stopwatch pause interval; fake display/service adapters; no native display, user window, compositor or process CPU/GPU';perf_id='PERF-018';variant=$variant;pair=$pair;binary_sha256=if($variant-ceq 'baseline'){$baselineDll[0].Hash}else{$candidateDll[0].Hash};benchmark_sha256=$candidateTests[0].Hash})
        }
        foreach($duplicates in @(1,10,50)){foreach($metric in $metricNames){Require($seen.Contains("$duplicates/$metric"))"Missing counter: $runId/$duplicates/$metric"}}
        $processes.Add([pscustomobject]@{RunId=$runId;Pair=$pair;Variant=$variant;ProcessId=$pidValue;StartedUtc=$started.ToString('o');FinishedUtc=$finished.ToString('o');ExitCode=$exitCode;TrxSHA256=Hash $trx;StdOutSHA256=Hash $stdoutPath;StdErrSHA256=Hash $stderrPath;FancyWMDllSHA256=if($variant-ceq 'baseline'){$baselineDll[0].Hash}else{$candidateDll[0].Hash};FancyWMTestsDllSHA256=$candidateTests[0].Hash})
    }
}
Require($rows.Count-eq 600 -and $raw.Count-eq 600 -and $processes.Count-eq 10)'Measurement dimensions differ.'
$rows|Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'measurements.csv');$raw|Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'raw-counters.csv');$processes|Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'process-times.csv')
$values=@{};foreach($record in $raw){$values["$($record.Pair)/$($record.Variant)/$($record.Duplicates)/$($record.Metric)"]=[long]$record.Value}
$timingWins=0;$allocationWins=0;$memoryBeforeFinalWins=0
foreach($pair in 1..5){foreach($duplicates in @(1,10,50)){
    if($values["$pair/candidate/$duplicates/gc-pause-ticks"]-lt $values["$pair/baseline/$duplicates/gc-pause-ticks"]){$timingWins++}
    if($values["$pair/candidate/$duplicates/allocated-bytes"]-lt $values["$pair/baseline/$duplicates/allocated-bytes"]){$allocationWins++}
    if($values["$pair/candidate/$duplicates/managed-bytes-before-final-collection"]-lt $values["$pair/baseline/$duplicates/managed-bytes-before-final-collection"]){$memoryBeforeFinalWins++}
}}
$provenance=[ordered]@{Verdict='MEASUREMENT_CHECKS_PASS';CandidateSnapshotId=$CandidateSnapshotId;BaselineSnapshotId=$BaselineSnapshotId;ExperimentId=$ExperimentId;PerfId='PERF-018';Pairs=5;Processes=10;ProcessesSequential=$true;MeasurementRows=600;RawCounters=600;TimingComparisons=15;GcPauseCandidateWins=$timingWins;AllocationCandidateWins=$allocationWins;PreFinalMemoryCandidateWins=$memoryBeforeFinalWins;Acceptance='Exact equal display lifecycle/event/churn workload; candidate admits one request per removed owner while mechanical baseline admits duplicates; actual synchronous two-pass forced compacting full collections measured.';Scope='Managed testhost A/B with exact production admission delta; GC pause, allocation and managed-memory observations are separate; timing is descriptive.';NotMeasured=@('native heap','real display removal','settings-window combined collector','process CPU/GPU','DWM/compositor','presentation latency');CandidateFancyWMDllSHA256=$candidateDll[0].Hash;BaselineFancyWMDllSHA256=$baselineDll[0].Hash;FancyWMTestsDllSHA256=$candidateTests[0].Hash;MeasurementsSHA256=Hash (Join-Path $experimentRoot 'measurements.csv');RawCountersSHA256=Hash (Join-Path $experimentRoot 'raw-counters.csv');ProcessTimesSHA256=Hash (Join-Path $experimentRoot 'process-times.csv');BinaryChecksSHA256=Hash (Join-Path $experimentRoot 'binary-checks.csv');CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')}
$provenance|ConvertTo-Json -Depth 6|Set-Content (Join-Path $experimentRoot 'provenance.json');$provenance|ConvertTo-Json -Depth 6
