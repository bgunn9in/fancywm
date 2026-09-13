param(
    [Parameter(Mandatory)][string]$CandidateSnapshotId,
    [Parameter(Mandatory)][string]$BaselineSnapshotId,
    [Parameter(Mandatory)][string]$ExperimentId
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function HashFile([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$candidateRoot = Join-Path $repositoryRoot "artifacts/performance/$CandidateSnapshotId"
$baselineRoot = Join-Path $repositoryRoot "artifacts/performance/$BaselineSnapshotId"
$experimentRoot = Join-Path $candidateRoot $ExperimentId
Require (!(Test-Path $experimentRoot)) "Experiment exists: $ExperimentId"
foreach ($root in $candidateRoot, $baselineRoot) { Require (Test-Path "$root/manifest.csv") "Snapshot missing: $root" }
$candidateManifest = @(Import-Csv (Join-Path $candidateRoot 'manifest.csv'))
$baselineManifest = @(Import-Csv (Join-Path $baselineRoot 'manifest.csv'))
foreach ($record in $candidateManifest) { Require ((HashFile (Join-Path $candidateRoot "source/$($record.Path)")) -ceq $record.SHA256) "Candidate source differs: $($record.Path)" }
foreach ($record in $baselineManifest) { Require ((HashFile (Join-Path $baselineRoot "source/$($record.Path)")) -ceq $record.SHA256) "Baseline source differs: $($record.Path)" }
$runner = @($candidateManifest | Where-Object Path -CEQ 'scripts/performance/Measure-KeybindingsPageTooltip.ps1')
Require ($runner.Count -eq 1 -and $runner[0].SHA256 -ceq (HashFile $PSCommandPath)) 'Executing runner differs.'
$fixturePath = 'FancyWM.Tests/Utilities/KeybindingsPageMaterializationTest.cs'
Require (@($candidateManifest | Where-Object Path -CEQ $fixturePath)[0].SHA256 -ceq @($baselineManifest | Where-Object Path -CEQ $fixturePath)[0].SHA256) 'A/B fixture differs.'

New-Item -ItemType Directory -Path $experimentRoot | Out-Null
$candidateRun = Join-Path $experimentRoot 'candidate-run'
$baselineRun = Join-Path $experimentRoot 'baseline-run'
Copy-Item -LiteralPath (Join-Path $candidateRoot 'binaries') -Destination $candidateRun -Recurse
Copy-Item -LiteralPath (Join-Path $candidateRoot 'binaries') -Destination $baselineRun -Recurse
Copy-Item -LiteralPath (Join-Path $baselineRoot 'binaries/FancyWM.dll') -Destination (Join-Path $baselineRun 'FancyWM.dll') -Force
$candidateArchive = @(Import-Csv (Join-Path $candidateRoot 'binaries.csv'))
$baselineArchive = @(Import-Csv (Join-Path $baselineRoot 'binaries.csv'))
foreach ($record in $candidateArchive) { Require ((HashFile (Join-Path $candidateRoot "binaries/$($record.Path)")) -ceq $record.SHA256) "Candidate binary differs: $($record.Path)" }
foreach ($record in $baselineArchive) { Require ((HashFile (Join-Path $baselineRoot "binaries/$($record.Path)")) -ceq $record.SHA256) "Baseline binary differs: $($record.Path)" }
$checks = @(Get-ChildItem -LiteralPath $candidateRun -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($candidateRun, $_.FullName).Replace('\','/')
    $candidateHash = HashFile $_.FullName
    $baselineHash = HashFile (Join-Path $baselineRun $relative)
    Require ((($relative -ceq 'FancyWM.dll') -eq ($candidateHash -cne $baselineHash))) "Unexpected binary delta: $relative"
    [pscustomobject]@{ Path = $relative; CandidateSHA256 = $candidateHash; BaselineSHA256 = $baselineHash }
})
$checks | Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'binary-checks.csv')

$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
& $dotnet --info *> (Join-Path $experimentRoot 'dotnet-info.log')
Require ($LASTEXITCODE -eq 0) 'dotnet --info failed.'
$filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.KeybindingsPageMaterializationTest.KeybindingsPageMaterializationCounterScenario'
$units = @{
    'constructor-bytes'='bytes/workload'; 'constructor-ticks'='ticks/workload';
    'layout-bytes'='bytes/workload'; 'layout-ticks'='ticks/workload';
    'timestamp-frequency'='ticks/second'; 'groups'='groups'; 'bindings'='bindings';
    'visuals'='objects'; 'logical'='objects'; 'dependency-objects'='objects';
    'items-controls'='controls'; 'key-press-boxes'='controls'; 'text-blocks'='controls';
    'description-hosts'='controls'; 'eager-description-tooltip-elements'='controls';
    'description-tooltip-strings'='strings'
}
$rows = [Collections.Generic.List[object]]::new()
$raw = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[object]]::new()
$previousFinish = $null
foreach ($pair in 1..5) {
    $variants = if ($pair % 2) { @('baseline','candidate') } else { @('candidate','baseline') }
    foreach ($variant in $variants) {
        $runId = "$variant-$pair"
        $runRoot = if ($variant -ceq 'baseline') { $baselineRun } else { $candidateRun }
        $trx = Join-Path $experimentRoot "$runId.trx"
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $dotnet
        $startInfo.WorkingDirectory = $runRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.Environment['DOTNET_TieredCompilation'] = '0'
        foreach ($argument in @('vstest',(Join-Path $runRoot 'FancyWM.Tests.dll'),'/Platform:x64',"/TestCaseFilter:$filter","/Logger:trx;LogFileName=$runId.trx","/ResultsDirectory:$experimentRoot")) { $startInfo.ArgumentList.Add($argument) }
        $started = [DateTimeOffset]::UtcNow
        $process = [Diagnostics.Process]::Start($startInfo)
        Require ($null -ne $process) 'Start failed.'
        $outTask = $process.StandardOutput.ReadToEndAsync(); $errTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit(); $stdout = $outTask.GetAwaiter().GetResult(); $stderr = $errTask.GetAwaiter().GetResult()
        $finished = [DateTimeOffset]::UtcNow
        [IO.File]::WriteAllText((Join-Path $experimentRoot "$runId.stdout.log"), $stdout)
        [IO.File]::WriteAllText((Join-Path $experimentRoot "$runId.stderr.log"), $stderr)
        Require ($process.ExitCode -eq 0 -and (Test-Path $trx)) "Run failed: $runId"
        if ($null -ne $previousFinish) { Require ($started -ge $previousFinish) 'Processes overlap.' }
        $previousFinish = $finished
        $processes.Add([pscustomobject]@{ Pair=$pair; Variant=$variant; RunId=$runId; StartedUtc=$started.ToString('o'); FinishedUtc=$finished.ToString('o'); ExitCode=$process.ExitCode; TrxSHA256=HashFile $trx })
        [xml]$document = Get-Content -Raw $trx
        $all = @($document.SelectNodes("//*[local-name()='UnitTestResult']"))
        $leaves = @($all | Where-Object { $_.SelectNodes("./*[local-name()='InnerResults']/*[local-name()='UnitTestResult']").Count -eq 0 })
        Require ($leaves.Count -eq 1 -and @($leaves | Where-Object outcome -CNE 'Passed').Count -eq 0) 'TRX differs.'
        $captured = [string]::Join("`n", @($document.SelectNodes("//*[local-name()='StdOut']") | ForEach-Object InnerText))
        $matches = [regex]::Matches($captured, '(?m)^PERFCOUNTER (?<scenario>keybindings-page-(?<count>1|10|50)) (?<metric>[a-z0-9-]+) (?<value>-?\d+)\s*$')
        Require ($matches.Count -eq 48) "Counters differ: $runId"
        $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($match in $matches) {
            $scenario = $match.Groups['scenario'].Value; $count = [int]$match.Groups['count'].Value
            $metric = $match.Groups['metric'].Value; $value = [long]$match.Groups['value'].Value
            Require ($units.ContainsKey($metric) -and $keys.Add("$scenario/$metric")) "Counter key differs: $runId"
            $raw.Add([pscustomobject]@{ Pair=$pair; Variant=$variant; BindingsPerGroup=$count; Metric=$metric; Value=$value })
            $snapshot = if ($variant -ceq 'baseline') { $BaselineSnapshotId } else { $CandidateSnapshotId }
            $binary = if ($variant -ceq 'baseline') { @($checks | Where-Object Path -CEQ 'FancyWM.dll')[0].BaselineSHA256 } else { @($checks | Where-Object Path -CEQ 'FancyWM.dll')[0].CandidateSHA256 }
            $rows.Add([pscustomobject][ordered]@{
                experiment_id=$ExperimentId; snapshot_id=$snapshot
                scenario="KeybindingsPage; $count bindings/group; 8 groups; no HWND"
                configuration='Release; net10.0-windows10.0.18362.0 x64 testhost; isolated sequential process; DOTNET_TieredCompilation=0'
                window_count=$count*8; metric=$metric; unit=$units[$metric]; run=$pair; value=$value; iterations=1
                instrument='Actual compiled KeybindingsPage XAML, SettingsViewModel binding groups and KeyPressBox controls; owned WPF Application/STA; no shown window'
                perf_id='PERF-032'; variant=$variant; pair=$pair; binary_sha256=$binary
                benchmark_sha256=@($candidateArchive | Where-Object Path -CEQ 'FancyWM.Tests.dll')[0].SHA256
            })
        }
    }
}
$rows | Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'measurements.csv')
$raw | Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'raw-counters.csv')
$processes | Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'process-times.csv')
$provenance = [ordered]@{
    Verdict='MEASUREMENT_CHECKS_PASS'; CandidateSnapshotId=$CandidateSnapshotId; BaselineSnapshotId=$BaselineSnapshotId; ExperimentId=$ExperimentId
    Pairs=5; Processes=10; MeasurementRows=$rows.Count
    CandidateFancyWMDllSHA256=@($checks | Where-Object Path -CEQ 'FancyWM.dll')[0].CandidateSHA256
    BaselineFancyWMDllSHA256=@($checks | Where-Object Path -CEQ 'FancyWM.dll')[0].BaselineSHA256
    FancyWMTestsDllSHA256=@($candidateArchive | Where-Object Path -CEQ 'FancyWM.Tests.dll')[0].SHA256
    MeasurementsSHA256=HashFile (Join-Path $experimentRoot 'measurements.csv'); RawCountersSHA256=HashFile (Join-Path $experimentRoot 'raw-counters.csv')
    ProcessTimesSHA256=HashFile (Join-Path $experimentRoot 'process-times.csv'); BinaryChecksSHA256=HashFile (Join-Path $experimentRoot 'binary-checks.csv')
    NativeCalls=$false; CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')
}
$provenance | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $experimentRoot 'provenance.json')
$provenance | ConvertTo-Json -Depth 5
