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
    $StrictReportPath = Join-Path $candidateRoot 'validation/focus-rejected-strict-verification.json'
}
elseif (![IO.Path]::IsPathRooted($StrictReportPath)) { $StrictReportPath = Join-Path $repositoryRoot $StrictReportPath }
$ledgerPath = Join-Path $repositoryRoot 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv'
$expectedPrefixLength = 38906083L
$expectedPrefixHash = 'E858BC389131CFB4852B829B847ED51B2B4C03B8426DB662CAFBD550550392C8'
$expectedPrefixLines = 37891
$expectedPrefixRows = 37890
Assert-Condition (!(Test-Path -LiteralPath $receiptPath)) 'Ledger append receipt already exists; refusing a repeated append.'
$strictReportHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $StrictReportPath).Hash
$strictReport = Get-Content -LiteralPath $StrictReportPath -Raw | ConvertFrom-Json
Assert-Condition ($strictReport.Verdict -ceq 'ACCEPT' -and $strictReport.Accepted -is [bool] -and $strictReport.Accepted -and
    $strictReport.Scenario -ceq 'FocusRejectedAdmission' -and $strictReport.PerfId -ceq 'PERF-030' -and
    $strictReport.CandidateSnapshot -ceq $SnapshotId -and $strictReport.ExperimentId -ceq $ExperimentId -and
    $strictReport.MeasurementRows -eq 270 -and $strictReport.RawCounters -eq 270 -and
    $strictReport.ProcessCount -eq 10 -and $strictReport.AllocationPairs -eq 15 -and
    $strictReport.TimingAcceptanceGate -is [bool] -and !$strictReport.TimingAcceptanceGate) 'Strict FocusRejectedAdmission acceptance is missing or differs.'
$candidateManifestPath = Join-Path $candidateRoot 'manifest.csv'
$manifest = @(Import-Csv -LiteralPath $candidateManifestPath)
Assert-Condition ((Get-FileHash -Algorithm SHA256 -LiteralPath $candidateManifestPath).Hash -ceq $strictReport.CandidateManifestSHA256) 'Strict candidate manifest differs.'
foreach ($entry in @(
    [pscustomobject]@{ Path = 'scripts/performance/Append-FocusRejectedMeasurements.ps1'; Hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $PSCommandPath).Hash },
    [pscustomobject]@{ Path = 'scripts/performance/Verify-FocusRejectedAdmission.ps1'; Hash = $strictReport.VerifierSHA256 }
)) {
    $record = @($manifest | Where-Object Path -CEQ $entry.Path)
    Assert-Condition ($record.Count -eq 1 -and $record[0].SHA256 -ceq $entry.Hash -and
        (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path "$candidateRoot/source" $entry.Path)).Hash -ceq $entry.Hash) "Frozen accepted protocol differs: $($entry.Path)"
}
Assert-Condition ($strictReport.PairedSemanticComparisons -eq 90 -and $strictReport.Allocation.Count -eq 15 -and
    @($strictReport.Allocation | Where-Object { $_.CandidateBytes -ne 0 -or $_.CandidateBytes -ge $_.BaselineBytes }).Count -eq 0 -and
    $strictReport.BaselineRegressionLeaves -eq 4 -and $strictReport.BaselineSemanticTestsPassed -eq 1 -and
    $strictReport.BaselineRejectedAdmissionAllocationTestsFailed -eq 3 -and
    $strictReport.TestFqn -ceq 'FancyWM.Tests.Utilities.FocusHelperTest.FocusRejectedAdmissionCounterScenario' -and
    ($strictReport.SourceManifestDifferences -join '|') -ceq 'FancyWM/Utilities/FocusHelper.cs' -and
    ($strictReport.BinaryDifferences -join '|') -ceq 'FancyWM.dll') 'Strict allocation/semantic/regression evidence differs.'
$allocationKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($allocation in $strictReport.Allocation) {
    Assert-Condition ($allocation.Case -cin @('superseded','expired','stopped') -and
        [string]$allocation.Pair -cmatch '^[1-5]$' -and
        $allocationKeys.Add("$($allocation.Case)/$($allocation.Pair)") -and
        $allocation.BaselineBytes -gt 0 -and $allocation.CandidateBytes -eq 0 -and
        $allocation.SavedBytes -eq $allocation.BaselineBytes - $allocation.CandidateBytes) 'Strict allocation pair is invalid or duplicated.'
}
Assert-Condition ($allocationKeys.Count -eq 15) 'Strict allocation matrix differs.'
$measurementsBytes = [IO.File]::ReadAllBytes($measurementsPath)
$measurementsHash = Get-BytesHash $measurementsBytes
Assert-Condition ($measurementsHash -ceq $strictReport.MeasurementsSHA256) 'Measurements differ from strict acceptance.'
$encoding = [Text.UTF8Encoding]::new($false, $true)
$measurementText = $encoding.GetString($measurementsBytes).TrimStart([char]0xFEFF)
$rows = @($measurementText | ConvertFrom-Csv)
$schema = 'experiment_id|snapshot_id|scenario|configuration|window_count|metric|unit|run|value|iterations|instrument|perf_id|variant|pair|binary_sha256|benchmark_sha256'
Assert-Condition ($rows.Count -eq 270) 'Expected exactly270 measurement rows.'
$units = [ordered]@{
    'allocated-bytes' = 'bytes/100000 calls'
    'elapsed-ticks' = 'ticks/100000 calls'
    'timestamp-frequency' = 'ticks/second'
    'calls' = 'calls/scenario'
    'rejected' = 'rejected-calls/scenario'
    'native-calls' = 'native-adapter-calls/scenario'
    'worker-starts' = 'worker-starts/scenario'
    'wait-calls' = 'wait-adapter-calls/scenario'
    'current-preserved' = 'unchanged-current-state-observations/scenario'
}
$keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$scenarios = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($row in $rows) {
    $expectedSnapshot = if ($row.variant -ceq 'baseline') { $strictReport.BaselineSnapshot } else { $SnapshotId }
    $expectedBinaryHash = if ($row.variant -ceq 'baseline') { $strictReport.BaselineProductionSHA256 } else { $strictReport.CandidateProductionSHA256 }
    Assert-Condition (($row.PSObject.Properties.Name -join '|') -ceq $schema -and
        $row.experiment_id -ceq $ExperimentId -and $row.perf_id -ceq 'PERF-030' -and
        $row.variant -cin @('baseline','candidate') -and $row.run -cmatch '^[1-5]$' -and
        $row.pair -ceq $row.run -and $row.window_count -ceq '0' -and $row.iterations -ceq '100000' -and
        $row.scenario -cmatch '^Actual FocusHelper\.RequestSequence\.Enqueue; focus-rejected-(superseded|expired|stopped);1000 discarded warmups then100000 calls$' -and
        ![string]::IsNullOrWhiteSpace($row.configuration) -and
        ![string]::IsNullOrWhiteSpace($row.instrument) -and $row.snapshot_id -ceq $expectedSnapshot -and
        $row.binary_sha256 -ceq $expectedBinaryHash -and $row.benchmark_sha256 -ceq $strictReport.CommonFixtureSHA256 -and
        $row.metric -cin $units.Keys -and $row.unit -ceq $units[$row.metric] -and
        $row.value -cmatch '^(0|[1-9][0-9]*)$' -and
        $keys.Add("$($row.variant)/$($row.run)/$($row.scenario)/$($row.metric)")) 'Invalid or duplicate appended measurement row.'
    [void]$scenarios.Add($row.scenario)
}
Assert-Condition ($scenarios.Count -eq 3) 'Expected exactly three rejected-admission scenarios.'
foreach ($scenario in $scenarios) {
    foreach ($variant in @('baseline','candidate')) {
        foreach ($pair in 1..5) {
            foreach ($metric in $units.Keys) {
                Assert-Condition ($keys.Contains("$variant/$pair/$scenario/$metric")) 'Incomplete appended measurement matrix.'
            }
        }
    }
}
$headerEnd = [Array]::IndexOf($measurementsBytes, [byte]10)
Assert-Condition ($headerEnd -ge 0 -and $measurementsBytes[-1] -eq 10) 'Measurements must contain a newline-terminated header and records.'
$suffixBytes = [byte[]]$measurementsBytes[($headerEnd + 1)..($measurementsBytes.Length - 1)]
Assert-Condition (@($suffixBytes.Where({ $_ -eq 10 })).Count -eq 270) 'Append requires exactly270 single-line CSV records.'
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
    NewLength = $newLength; NewSHA256 = $newHash; NewLines = $expectedPrefixLines + 270; NewRows = $expectedPrefixRows + 270
    PrefixLength = $expectedPrefixLength; PrefixSHA256 = $actualPrefixHash
    SuffixLength = $suffixBytes.Length; SuffixSHA256 = $actualSuffixHash; AppendedRows = 270
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
