[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$ExperimentId,
    [string]$StrictReportPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert-Condition([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Get-BytesHash([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)) }
function Get-ByteRangeHash([byte[]]$Bytes, [int]$Offset, [int]$Count) {
    $range = [IO.MemoryStream]::new($Bytes, $Offset, $Count, $false)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { [Convert]::ToHexString($hasher.ComputeHash($range)) }
    finally { $hasher.Dispose(); $range.Dispose() }
}
$rootOutput = & git -C $PSScriptRoot rev-parse --show-toplevel
Assert-Condition ($LASTEXITCODE -eq 0) 'Cannot resolve repository root.'
$repositoryRoot = [IO.Path]::GetFullPath(($rootOutput -join '').Trim())
$candidateRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$experimentRoot = Join-Path $candidateRoot $ExperimentId
$measurementsPath = Join-Path $experimentRoot 'measurements.csv'
$receiptPath = Join-Path $experimentRoot 'ledger-append.json'
if ([string]::IsNullOrWhiteSpace($StrictReportPath)) {
    $StrictReportPath = Join-Path $candidateRoot 'validation/focus-worker-strict-verification.json'
}
elseif (![IO.Path]::IsPathRooted($StrictReportPath)) { $StrictReportPath = Join-Path $repositoryRoot $StrictReportPath }
$ledgerPath = Join-Path $repositoryRoot 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv'
$expectedPrefixLength = 39228823L
$expectedPrefixHash = '407C85168F01D049B885E58255E10CCF98BE91A5DFA0C1F798080CAA77D2AE94'
$expectedPrefixLines = 38161
$expectedPrefixRows = 38160
Assert-Condition (!(Test-Path -LiteralPath $receiptPath)) 'Ledger append receipt already exists; refusing a repeated append.'
$strictReportHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $StrictReportPath).Hash
$strictReport = Get-Content -LiteralPath $StrictReportPath -Raw | ConvertFrom-Json -DateKind String
Assert-Condition ($strictReport.Verdict -ceq 'ACCEPT' -and $strictReport.Accepted -is [bool] -and $strictReport.Accepted -and
    $strictReport.Scenario -ceq 'FocusWorkerAdmission' -and $strictReport.PerfId -ceq 'PERF-030' -and
    $strictReport.CandidateSnapshot -ceq $SnapshotId -and $strictReport.ExperimentId -ceq $ExperimentId -and
    $strictReport.MeasurementRows -eq 570 -and $strictReport.RawCounters -eq 570 -and
    $strictReport.ProcessCount -eq 10 -and $strictReport.AllocationPairs -eq 15 -and
    $strictReport.TimingAcceptanceGate -is [bool] -and !$strictReport.TimingAcceptanceGate) 'Strict FocusWorkerAdmission acceptance is missing or differs.'
$candidateManifestPath = Join-Path $candidateRoot 'manifest.csv'
$manifest = @(Import-Csv -LiteralPath $candidateManifestPath)
Assert-Condition ((Get-FileHash -Algorithm SHA256 -LiteralPath $candidateManifestPath).Hash -ceq $strictReport.CandidateManifestSHA256) 'Strict candidate manifest differs.'
foreach ($entry in @(
    [pscustomobject]@{ Path = 'scripts/performance/Append-FocusWorkerMeasurements.ps1'; Hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $PSCommandPath).Hash },
    [pscustomobject]@{ Path = 'scripts/performance/Verify-FocusWorkerAdmission.ps1'; Hash = $strictReport.VerifierSHA256 }
)) {
    $record = @($manifest | Where-Object Path -CEQ $entry.Path)
    Assert-Condition ($record.Count -eq 1 -and $record[0].SHA256 -ceq $entry.Hash -and
        (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path "$candidateRoot/source" $entry.Path)).Hash -ceq $entry.Hash) "Frozen accepted protocol differs: $($entry.Path)"
}
Assert-Condition ($strictReport.PairedSemanticComparisons -eq 240 -and $strictReport.SemanticMetricCount -eq 16 -and $strictReport.FrequencyInvariantComparisons -eq 29 -and $strictReport.Allocation.Count -eq 15 -and
    @($strictReport.Allocation | Where-Object { $_.BaselineBytes -ne 184000 -or $_.CandidateBytes -ne 152000 -or $_.SavedBytes -ne 32000 -or $_.BaselineBytesPerCall -ne 368 -or $_.CandidateBytesPerCall -ne 304 -or $_.SavedBytesPerCall -ne 64 }).Count -eq 0 -and
    $strictReport.BaselineRegressionLeaves -eq 8 -and $strictReport.BaselineSemanticTestsPassed -eq 5 -and
    $strictReport.BaselineWorkerAdmissionAllocationTestsFailed -eq 3 -and
    $strictReport.TestFqn -ceq 'FancyWM.Tests.Utilities.FocusHelperTest.FocusWorkerAdmissionCounterScenario' -and
    ($strictReport.SourceManifestDifferences -join '|') -ceq 'FancyWM/Utilities/FocusHelper.cs' -and
    ($strictReport.BinaryDifferences -join '|') -ceq 'FancyWM.dll') 'Strict allocation/semantic/regression evidence differs.'
$allocationKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($allocation in $strictReport.Allocation) {
    Assert-Condition ($allocation.Case -cin @('direct-success','alt-retry','attach-failure') -and
        [string]$allocation.Pair -cmatch '^[1-5]$' -and
        $allocationKeys.Add("$($allocation.Case)/$($allocation.Pair)") -and
        $allocation.BaselineBytes -eq 184000 -and $allocation.CandidateBytes -eq 152000 -and
        $allocation.SavedBytes -eq $allocation.BaselineBytes - $allocation.CandidateBytes) 'Strict allocation pair is invalid or duplicated.'
}
Assert-Condition ($allocationKeys.Count -eq 15) 'Strict allocation matrix differs.'
$regressionConfigurations = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
Assert-Condition ($strictReport.CandidateRegressions.Count -eq 2) 'Strict candidate targeted matrix differs.'
foreach ($regression in $strictReport.CandidateRegressions) {
    Assert-Condition ($regression.Configuration -cin @('Debug','Release') -and $regressionConfigurations.Add($regression.Configuration) -and
        $regression.Leaves -eq 8 -and $regression.Passed -eq 8 -and $regression.Failed -eq 0) 'Strict candidate targeted regression failed or duplicated.'
    $trxPath = Join-Path $candidateRoot "validation/$($regression.Configuration)-focus-worker-targeted.trx"
    Assert-Condition ((Get-FileHash -Algorithm SHA256 -LiteralPath $trxPath).Hash -ceq $regression.TrxSHA256 -and
        (Get-FileHash -Algorithm SHA256 -LiteralPath ([IO.Path]::ChangeExtension($trxPath,'.log'))).Hash -ceq $regression.LogSHA256) 'Strict candidate targeted artifact differs.'
}
foreach ($entry in @(
    [pscustomobject]@{ Path = (Join-Path $candidateRoot 'snapshot.json'); Hash = $strictReport.CandidateSnapshotSHA256 },
    [pscustomobject]@{ Path = (Join-Path $experimentRoot 'binary-checks.csv'); Hash = $strictReport.BinaryChecksSHA256 },
    [pscustomobject]@{ Path = (Join-Path $experimentRoot 'process-times.csv'); Hash = $strictReport.ProcessTimesSHA256 }
)) {
    Assert-Condition ((Get-FileHash -Algorithm SHA256 -LiteralPath $entry.Path).Hash -ceq $entry.Hash) 'Strict accepted provenance artifact differs.'
}
$expectedRuns = @('baseline-1','candidate-1','candidate-2','baseline-2','baseline-3','candidate-3','candidate-4','baseline-4','baseline-5','candidate-5')
Assert-Condition (($strictReport.ProcessOrder -join '|') -ceq ($expectedRuns -join '|') -and
    $strictReport.Processes.Count -eq 10 -and $strictReport.ProcessTimesRows -eq 10) 'Strict A/B process order differs.'
for ($index = 0; $index -lt $expectedRuns.Count; $index++) {
    $process = $strictReport.Processes[$index]
    Assert-Condition ($process.Run -ceq $expectedRuns[$index] -and
        (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $experimentRoot "$($process.Run).trx")).Hash -ceq $process.TrxSHA256 -and
        (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $experimentRoot "$($process.Run).stdout.log")).Hash -ceq $process.StdOutSHA256 -and
        (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $experimentRoot "$($process.Run).stderr.log")).Hash -ceq $process.StdErrSHA256 -and
        (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $experimentRoot "$($process.Run).command.json")).Hash -ceq $process.CommandSHA256 -and
        (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $experimentRoot "$($process.Run).process.json")).Hash -ceq $process.ProcessSHA256 -and
        (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $experimentRoot "runs/$($process.Run)/binaries.csv")).Hash -ceq $process.BinaryManifestSHA256) 'Strict original process evidence differs.'
}
$measurementsBytes = [IO.File]::ReadAllBytes($measurementsPath)
$measurementsHash = Get-BytesHash $measurementsBytes
Assert-Condition ($measurementsHash -ceq $strictReport.MeasurementsSHA256) 'Measurements differ from strict acceptance.'
$encoding = [Text.UTF8Encoding]::new($false, $true)
$measurementText = $encoding.GetString($measurementsBytes).TrimStart([char]0xFEFF)
$rows = @($measurementText | ConvertFrom-Csv)
$schema = 'experiment_id|snapshot_id|scenario|configuration|window_count|metric|unit|run|value|iterations|instrument|perf_id|variant|pair|binary_sha256|benchmark_sha256'
Assert-Condition ($rows.Count -eq 570) 'Expected exactly 570 measurement rows.'
$units = [ordered]@{
    'allocated-bytes' = 'bytes/500 admissions'; 'elapsed-ticks' = 'ticks/500 admissions'; 'timestamp-frequency' = 'ticks/second'
    'admissions' = 'admissions/scenario'; 'true-results' = 'true-completions/scenario'; 'current-results' = 'current-token-observations/scenario'
    'worker-starts' = 'worker-starts/scenario'; 'worker-exits' = 'worker-exits/scenario'; 'background-workers' = 'background-workers/scenario'
    'join-timeouts' = 'join-timeouts/scenario'; 'wait-calls' = 'wait-adapter-calls/scenario'; 'queue-calls' = 'queue-adapter-calls/scenario'
    'thread-id-reads' = 'thread-id-reads/scenario'; 'foreground-id-reads' = 'foreground-id-reads/scenario'
    'attach-calls' = 'attach-adapter-calls/scenario'; 'detach-calls' = 'detach-adapter-calls/scenario'
    'focus-calls' = 'focus-adapter-calls/scenario'; 'alt-presses' = 'alt-adapter-calls/scenario'; 'owned-attachments' = 'owned-attachments/scenario'
}
$expectedConfiguration = 'Release; net10.0-windows10.0.18362.0; x64; no debugger; DOTNET_TieredCompilation=0'
$expectedInstrument = 'Actual production FocusHelper.RequestSequence.Enqueue with one real completed background Thread per admission and a counted fake native adapter;32 discarded warmups then500 admissions;caller-thread GC.GetAllocatedBytesForCurrentThread;Stopwatch includes Thread.Start/Join and scheduling;common candidate fixture and dependencies, only archived FancyWM.dll differs;native focus,COM,WPF,DPI,whole-process CPU/GPU and native presentation latency NOT_MEASURED;timing descriptive without required timing win'
$frequencies = [Collections.Generic.HashSet[long]]::new()
$values = [Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
$keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$scenarios = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($row in $rows) {
    $expectedSnapshot = if ($row.variant -ceq 'baseline') { $strictReport.BaselineSnapshot } else { $SnapshotId }
    $expectedBinaryHash = if ($row.variant -ceq 'baseline') { $strictReport.BaselineProductionSHA256 } else { $strictReport.CandidateProductionSHA256 }
    Assert-Condition (($row.PSObject.Properties.Name -join '|') -ceq $schema -and
        $row.experiment_id -ceq $ExperimentId -and $row.perf_id -ceq 'PERF-030' -and
        $row.variant -cin @('baseline','candidate') -and $row.run -cmatch '^[1-5]$' -and
        $row.pair -ceq $row.run -and $row.window_count -ceq '0' -and $row.iterations -ceq '500' -and
        $row.scenario -cmatch '^Actual FocusHelper\.RequestSequence\.Enqueue; focus-worker-(direct-success|alt-retry|attach-failure);32 discarded warmups then500 completed worker admissions$' -and
        $row.configuration -ceq $expectedConfiguration -and
        $row.instrument -ceq $expectedInstrument -and $row.snapshot_id -ceq $expectedSnapshot -and
        $row.binary_sha256 -ceq $expectedBinaryHash -and $row.benchmark_sha256 -ceq $strictReport.CommonFixtureSHA256 -and
        $row.metric -cin $units.Keys -and $row.unit -ceq $units[$row.metric] -and
        $row.value -cmatch '^(0|[1-9][0-9]*)$' -and
        $keys.Add("$($row.variant)/$($row.run)/$($row.scenario)/$($row.metric)")) 'Invalid or duplicate appended measurement row.'
    $value = 0L
    Assert-Condition ([long]::TryParse($row.value, [Globalization.NumberStyles]::None,
        [Globalization.CultureInfo]::InvariantCulture, [ref]$value)) 'Measurement value exceeds Int64.'
    $case = [regex]::Match($row.scenario, 'focus-worker-(direct-success|alt-retry|attach-failure);').Groups[1].Value
    Assert-Condition ($values.TryAdd("$($row.variant)/$($row.run)/$case/$($row.metric)", $value)) 'Duplicate parsed measurement key.'
    $expected = switch -CaseSensitive ($row.metric) {
        'allocated-bytes' { if ($row.variant -ceq 'baseline') { 184000L } else { 152000L } }
        { $_ -cin @('admissions','current-results','worker-starts','worker-exits','background-workers',
            'queue-calls','thread-id-reads','foreground-id-reads','attach-calls') } { 500L }
        { $_ -cin @('join-timeouts','wait-calls','owned-attachments') } { 0L }
        { $_ -cin @('true-results','detach-calls') } { if ($case -ceq 'attach-failure') { 0L } else { 500L } }
        'focus-calls' { if ($case -ceq 'attach-failure') { 0L } elseif ($case -ceq 'alt-retry') { 1000L } else { 500L } }
        'alt-presses' { if ($case -ceq 'alt-retry') { 500L } else { 0L } }
        { $_ -cin @('elapsed-ticks','timestamp-frequency') } {
            Assert-Condition ($value -gt 0) 'Nonpositive elapsed ticks or frequency.'
            if ($_ -ceq 'timestamp-frequency') { [void]$frequencies.Add($value) }
            $null
        }
        default { throw "Unknown metric: $($row.metric)" }
    }
    if ($null -ne $expected) { Assert-Condition ($value -eq $expected) 'Appended semantic/allocation value differs from its scenario contract.' }
    [void]$scenarios.Add($row.scenario)
}
Assert-Condition ($scenarios.Count -eq 3) 'Expected exactly three worker-admission scenarios.'
foreach ($scenario in $scenarios) {
    foreach ($variant in @('baseline','candidate')) {
        foreach ($pair in 1..5) {
            foreach ($metric in $units.Keys) {
                Assert-Condition ($keys.Contains("$variant/$pair/$scenario/$metric")) 'Incomplete appended measurement matrix.'
            }
        }
    }
}
Assert-Condition ($frequencies.Count -eq 1 -and @($frequencies)[0] -eq $strictReport.TimestampFrequency) 'Appended timestamp frequency differs from strict acceptance.'
$semanticMetrics = @($units.Keys | Where-Object { $_ -cnotin @('allocated-bytes','elapsed-ticks','timestamp-frequency') })
$semanticComparisons = 0
foreach ($case in @('direct-success','alt-retry','attach-failure')) {
    foreach ($pair in 1..5) {
        foreach ($metric in $semanticMetrics) {
            Assert-Condition ($values["baseline/$pair/$case/$metric"] -eq $values["candidate/$pair/$case/$metric"]) 'Appended paired semantic value differs.'
            $semanticComparisons++
        }
    }
}
Assert-Condition ($semanticComparisons -eq 240) 'Appended semantic matrix differs.'
$headerEnd = [Array]::IndexOf($measurementsBytes, [byte]10)
Assert-Condition ($headerEnd -ge 0 -and $measurementsBytes[-1] -eq 10) 'Measurements must contain a newline-terminated header and records.'
$suffixBytes = [byte[]]$measurementsBytes[($headerEnd + 1)..($measurementsBytes.Length - 1)]
Assert-Condition (@($suffixBytes.Where({ $_ -eq 10 })).Count -eq 570) 'Append requires exactly 570 single-line CSV records.'
$suffixHash = Get-BytesHash $suffixBytes
# The exclusive handle makes prefix validation and the single append indivisible
# with respect to cooperating writers. An interrupted run cannot append again
# because the pinned length/hash and CreateNew receipt checks reject it.
$stream = [IO.File]::Open($ledgerPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    Assert-Condition ($stream.Length -eq $expectedPrefixLength) 'Ledger length differs from the pinned immutable prefix.'
    $prefixBytes = [byte[]]::new([int]$expectedPrefixLength)
    $stream.ReadExactly($prefixBytes, 0, $prefixBytes.Length)
    $prefixHash = Get-BytesHash $prefixBytes
    Assert-Condition ($prefixHash -ceq $expectedPrefixHash -and $prefixBytes[-1] -eq 10) 'Ledger prefix hash or final newline differs.'
    $prefixText = $encoding.GetString($prefixBytes).TrimStart([char]0xFEFF)
    $prefixLines = $prefixText.Split([char]10).Length - 1
    Assert-Condition ($prefixLines -eq $expectedPrefixLines -and $prefixLines - 1 -eq $expectedPrefixRows) 'Ledger prefix line/data-row count differs.'
    $existingHeader = $prefixText.Substring(0, $prefixText.IndexOf([char]10)).TrimEnd([char]13)
    $measurementHeader = $measurementText.Substring(0, $measurementText.IndexOf([char]10)).TrimEnd([char]13)
    Assert-Condition ($existingHeader -ceq $measurementHeader -and !$prefixText.Contains('"' + $ExperimentId + '"')) 'Ledger schema differs or experiment already exists.'
    Assert-Condition ((Get-FileHash -Algorithm SHA256 -LiteralPath $StrictReportPath).Hash -ceq $strictReportHash -and
        (Get-FileHash -Algorithm SHA256 -LiteralPath $measurementsPath).Hash -ceq $measurementsHash) 'Accepted inputs changed before append.'
    $stream.Write($suffixBytes, 0, $suffixBytes.Length)
    $stream.Flush($true)
    $newLength = $stream.Length
    Assert-Condition ($newLength -eq $expectedPrefixLength + $suffixBytes.Length) 'Appended ledger length differs.'
    $stream.Position = 0
    $newBytes = [byte[]]::new([int]$newLength)
    $stream.ReadExactly($newBytes, 0, $newBytes.Length)
    $newHash = Get-BytesHash $newBytes
    $actualPrefixHash = Get-ByteRangeHash $newBytes 0 $prefixBytes.Length
    $actualSuffixHash = Get-ByteRangeHash $newBytes $prefixBytes.Length $suffixBytes.Length
    Assert-Condition ($actualPrefixHash -ceq $prefixHash -and $actualSuffixHash -ceq $suffixHash) 'Readback prefix or suffix differs.'
}
finally { $stream.Dispose() }
$receipt = [ordered]@{
    Verdict = 'ACCEPT'; SnapshotId = $SnapshotId; ExperimentId = $ExperimentId; PerfId = 'PERF-030'
    LedgerPath = 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv'
    OldLength = $expectedPrefixLength; OldSHA256 = $prefixHash; OldLines = $expectedPrefixLines; OldRows = $expectedPrefixRows
    NewLength = $newLength; NewSHA256 = $newHash; NewLines = $expectedPrefixLines + 570; NewRows = $expectedPrefixRows + 570
    PrefixLength = $expectedPrefixLength; PrefixSHA256 = $actualPrefixHash
    SuffixLength = $suffixBytes.Length; SuffixSHA256 = $actualSuffixHash; AppendedRows = 570
    MeasurementsSHA256 = $measurementsHash; StrictReportSHA256 = $strictReportHash
    AppenderSHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $PSCommandPath).Hash
}
$json = $receipt | ConvertTo-Json -Depth 4
$receiptStream = [IO.File]::Open($receiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $bytes = $encoding.GetBytes($json + [Environment]::NewLine)
    $receiptStream.Write($bytes, 0, $bytes.Length)
}
finally { $receiptStream.Dispose() }
$json
