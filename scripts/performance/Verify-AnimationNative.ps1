param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Z0-9-]+$')][string]$SnapshotId,
    [ValidatePattern('^[A-Z0-9-]+$')][string]$VerifierSnapshotId
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function HashFile([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Percentile([double[]]$Values, [double]$Percent) {
    Require ($Values.Count -gt 0) 'Cannot summarize an empty sample.'
    $sorted = @($Values | Sort-Object)
    $sorted[[Math]::Max(0, [Math]::Ceiling($Percent * $sorted.Count) - 1)]
}
$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$snapshotRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"))
$measurementRoot = Join-Path $snapshotRoot 'native'
$verifierRoot = if ($VerifierSnapshotId) {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts/performance/$VerifierSnapshotId"))
} else { $snapshotRoot }
$reportPath = Join-Path $verifierRoot 'native-verification.json'
Require (!(Test-Path -LiteralPath $reportPath)) 'The immutable verification report already exists.'
$sourceManifest = @(Import-Csv -LiteralPath (Join-Path $snapshotRoot 'manifest.csv'))
foreach ($file in $sourceManifest) {
    Require ((HashFile (Join-Path $snapshotRoot "source/$($file.Path)")) -ceq $file.SHA256) "Frozen source differs: $($file.Path)"
}
$verifierManifest = if ($VerifierSnapshotId) {
    @(Import-Csv -LiteralPath (Join-Path $verifierRoot 'manifest.csv'))
} else { $sourceManifest }
if ($VerifierSnapshotId) {
    foreach ($file in $verifierManifest) {
        Require ((HashFile (Join-Path $verifierRoot "source/$($file.Path)")) -ceq $file.SHA256) "Frozen verifier source differs: $($file.Path)"
    }
}
$frozenVerifier = @($verifierManifest | Where-Object Path -CEQ 'scripts/performance/Verify-AnimationNative.ps1')
Require ($frozenVerifier.Count -eq 1 -and $frozenVerifier[0].SHA256 -ceq (HashFile $PSCommandPath)) 'The verifier was changed after freezing.'
foreach ($file in (Import-Csv -LiteralPath (Join-Path $snapshotRoot 'native-evidence.csv'))) {
    $path = Join-Path $measurementRoot $file.Path
    Require ((Get-Item -LiteralPath $path).Length -eq [long]$file.Length -and (HashFile $path) -ceq $file.SHA256) "Native evidence differs: $($file.Path)"
}
$binaryManifest = @(Import-Csv -LiteralPath (Join-Path $measurementRoot 'binary-manifest.csv'))
foreach ($file in $binaryManifest) {
    Require ((HashFile (Join-Path $snapshotRoot "binaries/$($file.Path)")) -ceq $file.SHA256) "Archived binary differs: $($file.Path)"
}
$environment = Get-Content -LiteralPath (Join-Path $measurementRoot 'environment.json') -Raw | ConvertFrom-Json
$commands = @(Get-Content -LiteralPath (Join-Path $measurementRoot 'commands.json') -Raw | ConvertFrom-Json)
Require ($environment.Runs -ge 5 -and $environment.Iterations -ge 8) 'At least five baseline processes with eight transitions per count are required.'
Require (@($commands | Where-Object { $_.ExitCode -ne 0 -or $_.TimedOut }).Count -eq 0) 'A captured command failed.'
$processes = @($commands | Where-Object Name -Match '^run-\d+$')
Require ($processes.Count -eq $environment.Runs) 'Process count differs.'
Require ((Get-Content -LiteralPath (Join-Path $measurementRoot 'wpr-after.log') -Raw) -match 'not recording') 'Owned WPR cleanup did not pass.'
Require ((Get-Item -LiteralPath (Join-Path $measurementRoot 'animation.etl')).Length -gt 0) 'ETL is missing.'
$traceStats = Get-Content -LiteralPath (Join-Path $measurementRoot 'trace-stats.txt') -Raw
$traceHeader = Get-Content -LiteralPath (Join-Path $measurementRoot 'trace-header.txt') -Raw
Require ($traceHeader -match '(?m)^Total # Lost Buffers\s*:\s*0\s*$' -and $traceHeader -match '(?m)^Total # Lost Events\s*:\s*0\s*$') 'The trace lost events or buffers.'
$markers = Get-Content -LiteralPath (Join-Path $measurementRoot 'markers.csv') -Raw
$markerMatches = [regex]::Matches($markers, '(?m)^FancyWM-AnimationNativeHarness/Boundary/,\s*\d+,\s*dotnet\.exe\s*\((\d+)\).*?"transition : (\d+)",\s*"phase : (\d+)",\s*"qpc : (\d+)"\s*$')
Require ($markerMatches.Count -eq $environment.Runs * ($environment.Iterations + 2) * 3 * 4) 'ETL marker dimensions differ.'
$markerBoundaries = @{}
foreach ($marker in $markerMatches) {
    $key = "$($marker.Groups[1].Value)/$($marker.Groups[2].Value)/$($marker.Groups[3].Value)"
    Require (!$markerBoundaries.ContainsKey($key)) 'A duplicate ETL boundary was recorded.'
    $markerBoundaries[$key] = [long]$marker.Groups[4].Value
}
foreach ($provider in @('c72441c8-a777-4d33-8d80-794f778a01ca', 'Microsoft-Windows-Dwm-Core', 'Microsoft-Windows-DxgKrnl', 'CSwitch')) {
    Require ($traceStats -match [regex]::Escape($provider)) "Trace provider/event is absent: $provider"
}
$nativeRows = [Collections.Generic.List[object]]::new()
$fixedDisplays = @{}
for ($run = 1; $run -le $environment.Runs; $run++) {
    $command = $processes[$run - 1]
    Require ($command.Name -ceq "run-$run") 'Process order differs.'
    if ($run -gt 1) { Require ([DateTimeOffset]::Parse($command.StartedUtc) -ge [DateTimeOffset]::Parse($processes[$run - 2].FinishedUtc)) 'Measurement processes overlap.' }
    $summary = Get-Content -LiteralPath (Join-Path $measurementRoot "run-$run/summary.json") -Raw | ConvertFrom-Json
    Require ($summary.ProcessId -eq $command.ProcessId -and $summary.Architecture -ceq 'X64' -and $summary.CleanupPassed -and $summary.EtwProviderEnabled) 'Native process identity, architecture, ETW or cleanup failed.'
    Require ($summary.DurationMs -eq 250 -and $summary.Warmups -eq 2 -and $summary.Iterations -eq $environment.Iterations) 'Transition inputs differ.'
    Require ($summary.Transitions.Count -eq 3 * ($summary.Warmups + $summary.Iterations)) 'Transition count differs.'
    Require ($summary.NativeTargetOwnerThreads -eq 1) 'The HWND owner-thread topology differs.'
    foreach ($transition in $summary.Transitions) {
        $phase = 0
        foreach ($boundary in @('StartQpc', 'CompletedQpc', 'NativeVerifiedQpc', 'DwmFlushedQpc')) {
            $phase++
            $key = "$($summary.ProcessId)/$($transition.Transition)/$phase"
            Require ($markerBoundaries.ContainsKey($key) -and $markerBoundaries[$key] -eq [long]$transition.$boundary) "A QPC boundary differs from the ETL marker export: run $run transition $($transition.Transition) $boundary"
        }
    }
    foreach ($assembly in $summary.Assemblies) {
        Require ((HashFile $assembly.Location) -ceq $assembly.SHA256 -and $assembly.Location.StartsWith((Join-Path $snapshotRoot 'binaries'), [StringComparison]::OrdinalIgnoreCase)) 'The process loaded a different production assembly.'
    }
    foreach ($display in $summary.Displays) {
        $fixed = $display | Select-Object Count, Dpi, WorkArea, Originals, Destinations | ConvertTo-Json -Depth 12 -Compress
        if ($run -eq 1) { $fixedDisplays[[int]$display.Count] = $fixed }
        else { Require ($fixedDisplays[[int]$display.Count] -ceq $fixed) 'Display/DPI/geometry changed across runs.' }
    }
    $calls = @(Import-Csv -LiteralPath (Join-Path $measurementRoot "run-$run/calls.csv"))
    Require ($calls.Count -lt 500000) 'The sidecar exceeded its bounded capacity.'
    foreach ($transition in @($summary.Transitions | Where-Object Measured)) {
        Require ($transition.NativeRectanglesPassed -and $transition.StartQpc -lt $transition.CompletedQpc -and $transition.CompletedQpc -le $transition.NativeVerifiedQpc -and $transition.NativeVerifiedQpc -le $transition.DwmFlushedQpc) 'Completion boundaries differ.'
        $samples = @($calls | Where-Object { [int]$_.transition -eq $transition.Transition })
        $reads = @($samples | Where-Object operation -CEQ 'PositionRead')
        $writes = @($samples | Where-Object operation -CEQ 'SetPosition')
        $applied = @($samples | Where-Object operation -CEQ 'NativeApplied')
        $frames = @($samples | Where-Object operation -CEQ 'Frame' | Sort-Object { [long]$_.qpc })
        Require ($frames.Count -gt 1 -and $reads.Count -ge $writes.Count -and $writes.Count -ge $transition.Count) 'Read/write/frame dimensions differ.'
        Require ($applied.Count -eq $writes.Count) 'Native delivery count differs from successful SetPosition calls.'
        $display = @($summary.Displays | Where-Object Count -EQ $transition.Count)[0]
        $expected = if ($transition.Iteration % 2 -eq 1) { $display.Destinations } else { $display.Originals }
        $lastApplied = 0L
        for ($target = 0; $target -lt $transition.Count; $target++) {
            $targetApplied = @($applied | Where-Object { [int]$_.target -eq $target } | Sort-Object { [long]$_.qpc })
            Require ($targetApplied.Count -gt 0) 'A target never received a native position update.'
            $last = $targetApplied[-1]
            Require ([int]$last.x -eq $expected[$target].Left -and [int]$last.y -eq $expected[$target].Top -and [int]$last.width -eq $expected[$target].Width -and [int]$last.height -eq $expected[$target].Height) 'The final native event does not match the target rectangle.'
            $lastApplied = [Math]::Max($lastApplied, [long]$last.qpc)
        }
        $intervals = for ($index = 1; $index -lt $frames.Count; $index++) {
            (([long]$frames[$index].qpc + [long]$frames[$index].duration_ticks) - ([long]$frames[$index - 1].qpc + [long]$frames[$index - 1].duration_ticks)) * 1000.0 / $summary.StopwatchFrequency
        }
        $fanout = @($writes | Group-Object frame | ForEach-Object Count)
        $nativeRows.Add([pscustomobject]@{
            Run = $run; Count = $transition.Count; Iteration = $transition.Iteration
            Frames = $frames.Count; PositionReads = $reads.Count; SetPositionCalls = $writes.Count; NativeApplied = $applied.Count
            WriteThreads = @($writes.thread_id | Sort-Object -Unique).Count
            MaxWritesPerFrame = ($fanout | Measure-Object -Maximum).Maximum
            FrameIntervalMedianMs = Percentile $intervals 0.5; FrameIntervalP95Ms = Percentile $intervals 0.95
            CompletionMs = $transition.CompletionMs; NativeVerifiedMs = $transition.NativeVerifiedMs
            LastNativeAppliedMs = ($lastApplied - $transition.StartQpc) * 1000.0 / $summary.StopwatchFrequency
            NativeTailAfterCompletionMs = [Math]::Max(0.0, ($lastApplied - $transition.CompletedQpc) * 1000.0 / $summary.StopwatchFrequency)
            DwmFlushBoundaryMs = $transition.DwmFlushBoundaryMs; ProcessCpuMs = $transition.ProcessCpuMs
        })
    }
}
Require ($nativeRows.Count -eq $environment.Runs * $environment.Iterations * 3) 'Measured row count differs.'
$aggregates = foreach ($count in @(1, 10, 50)) {
    $rows = @($nativeRows | Where-Object Count -EQ $count)
    [ordered]@{ Count = $count; Transitions = $rows.Count
        MedianCompletionMs = Percentile $rows.CompletionMs 0.5
        MedianLastNativeAppliedMs = Percentile $rows.LastNativeAppliedMs 0.5
        MedianNativeTailMs = Percentile $rows.NativeTailAfterCompletionMs 0.5
        P95NativeTailMs = Percentile $rows.NativeTailAfterCompletionMs 0.95
        MedianFrameIntervalMs = Percentile $rows.FrameIntervalMedianMs 0.5
        MedianProcessCpuMs = Percentile $rows.ProcessCpuMs 0.5
        TotalPositionReads = ($rows.PositionReads | Measure-Object -Sum).Sum
        TotalSetPositionCalls = ($rows.SetPositionCalls | Measure-Object -Sum).Sum
        MaxWritesPerFrame = ($rows.MaxWritesPerFrame | Measure-Object -Maximum).Maximum }
}
$ledgerHash = HashFile (Join-Path $repositoryRoot 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv')
Require ($ledgerHash -ceq $environment.LedgerBeforeSHA256) 'The historical ledger changed during the baseline.'
$nativeRows | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $verifierRoot 'native-transition-measurements.csv')
$report = [ordered]@{
    Verdict = 'NATIVE_BASELINE_CAPTURED'; PerfId = 'PERF-010'; SnapshotId = $SnapshotId
    VerifierSnapshotId = if ($VerifierSnapshotId) { $VerifierSnapshotId } else { $SnapshotId }
    Processes = $environment.Runs; MeasuredTransitions = $nativeRows.Count
    LostEvents = 0; LostBuffers = 0; EtwBoundaryMarkersVerified = $environment.Runs * ($environment.Iterations + 2) * 3 * 4
    ProductionChange = $false; ABComparison = $false; PhysicalPresentationMeasured = $false
    Aggregates = @($aggregates); LedgerSHA256 = $ledgerHash
    SourceManifestSHA256 = HashFile (Join-Path $snapshotRoot 'manifest.csv')
    EvidenceManifestSHA256 = HashFile (Join-Path $snapshotRoot 'native-evidence.csv')
    MeasurementsSHA256 = HashFile (Join-Path $verifierRoot 'native-transition-measurements.csv')
    VerifierSourceManifestSHA256 = HashFile (Join-Path $verifierRoot 'manifest.csv')
    VerifierSHA256 = HashFile $PSCommandPath; CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Limitations = 'Instrumented baseline, one HWND owner thread and real WinMan workspace per process; CPU includes fixture/workspace and is quantized. Native WM_WINDOWPOSCHANGED timestamps measure application of geometry, not physical display presentation. PositionRead counts provider-cache/liveness calls. No full-app CPU/GPU/energy or speedup claim. Native read/write fan-out does not establish that batching preserves behavior.'
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath
$report | ConvertTo-Json -Depth 6
