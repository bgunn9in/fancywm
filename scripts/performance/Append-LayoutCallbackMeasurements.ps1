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
    $StrictReportPath = Join-Path $candidateRoot 'validation/layout-callback-strict-verification.json'
}
elseif (![IO.Path]::IsPathRooted($StrictReportPath)) { $StrictReportPath = Join-Path $repositoryRoot $StrictReportPath }
$ledgerPath = Join-Path $repositoryRoot 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv'
$expectedPrefixLength = 37478593L
$expectedPrefixHash = '6B5AC23BB9A8AC86A3FBD6CA6FBC471E646684F69D49A6271921917C83745E62'
$expectedPrefixLines = 36751
$expectedPrefixRows = 36750
Assert-Condition (!(Test-Path -LiteralPath $receiptPath)) 'Ledger append receipt already exists; refusing a repeated append.'
$strictReportHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $StrictReportPath).Hash
$strictReport = Get-Content -LiteralPath $StrictReportPath -Raw | ConvertFrom-Json
Assert-Condition ($strictReport.Verdict -ceq 'ACCEPT' -and $strictReport.Accepted -is [bool] -and $strictReport.Accepted -and
    $strictReport.Scenario -ceq 'LayoutCallbackCache' -and $strictReport.PerfId -ceq 'PERF-004' -and
    $strictReport.CandidateSnapshot -ceq $SnapshotId -and $strictReport.ExperimentId -ceq $ExperimentId -and
    $strictReport.MeasurementRows -eq 300 -and $strictReport.RawCounters -eq 300 -and
    $strictReport.ProcessCount -eq 10 -and $strictReport.AllocationPairs -eq 15 -and
    $strictReport.TimingAcceptanceGate -is [bool] -and !$strictReport.TimingAcceptanceGate) 'Strict LayoutCallbackCache acceptance is missing or differs.'
$measurementsBytes = [IO.File]::ReadAllBytes($measurementsPath)
$measurementsHash = Get-BytesHash $measurementsBytes
Assert-Condition ($measurementsHash -ceq $strictReport.MeasurementsSHA256) 'Measurements differ from strict acceptance.'
$encoding = [Text.UTF8Encoding]::new($false, $true)
$measurementText = $encoding.GetString($measurementsBytes).TrimStart([char]0xFEFF)
$rows = @($measurementText | ConvertFrom-Csv)
$schema = 'experiment_id|snapshot_id|scenario|configuration|window_count|metric|unit|run|value|iterations|instrument|perf_id|variant|pair|binary_sha256|benchmark_sha256'
Assert-Condition ($rows.Count -eq 300) 'Expected exactly300 measurement rows.'
$keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($row in $rows) {
    Assert-Condition (($row.PSObject.Properties.Name -join '|') -ceq $schema -and
        $row.experiment_id -ceq $ExperimentId -and $row.perf_id -ceq 'PERF-004' -and
        $row.variant -cin @('baseline','candidate') -and $row.run -cmatch '^[1-5]$' -and
        $row.pair -ceq $row.run -and $row.window_count -ceq '0' -and $row.iterations -ceq '10000' -and
        $keys.Add("$($row.variant)/$($row.run)/$($row.scenario)/$($row.metric)")) 'Invalid or duplicate appended measurement row.'
}
$headerEnd = [Array]::IndexOf($measurementsBytes, [byte]10)
Assert-Condition ($headerEnd -ge 0 -and $measurementsBytes[-1] -eq 10) 'Measurements must contain a newline-terminated header and records.'
$suffixBytes = [byte[]]$measurementsBytes[($headerEnd + 1)..($measurementsBytes.Length - 1)]
Assert-Condition (@($suffixBytes.Where({ $_ -eq 10 })).Count -eq 300) 'Append requires exactly300 single-line CSV records.'
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
    Verdict = 'ACCEPT'; SnapshotId = $SnapshotId; ExperimentId = $ExperimentId; PerfId = 'PERF-004'
    LedgerPath = 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv'
    OldLength = $expectedPrefixLength; OldSHA256 = $prefixHash; OldLines = $expectedPrefixLines; OldRows = $expectedPrefixRows
    NewLength = $newLength; NewSHA256 = $newHash; NewLines = $expectedPrefixLines + 300; NewRows = $expectedPrefixRows + 300
    PrefixLength = $expectedPrefixLength; PrefixSHA256 = $actualPrefixHash
    SuffixLength = $suffixBytes.Length; SuffixSHA256 = $actualSuffixHash; AppendedRows = 300
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
