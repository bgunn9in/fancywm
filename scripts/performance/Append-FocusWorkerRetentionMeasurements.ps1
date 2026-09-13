param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$ExperimentId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Bytes-Hash([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)) }
function Range-Hash([byte[]]$Bytes, [int]$Offset, [int]$Count) {
    $stream = [IO.MemoryStream]::new($Bytes, $Offset, $Count, $false)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { [Convert]::ToHexString($algorithm.ComputeHash($stream)) }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Require ($LASTEXITCODE -eq 0) 'Cannot resolve repository root.'
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$experimentRoot = Join-Path $snapshotRoot $ExperimentId
$measurementsPath = Join-Path $experimentRoot 'measurements.csv'
$strictPath = Join-Path $snapshotRoot 'validation/focus-worker-retention-strict-verification.json'
$receiptPath = Join-Path $experimentRoot 'ledger-append.json'
$ledgerPath = Join-Path $repositoryRoot 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv'
Require (!(Test-Path $receiptPath)) 'Append receipt already exists.'
$strict = Get-Content -Raw $strictPath | ConvertFrom-Json -DateKind String
Require ($strict.Accepted -is [bool] -and $strict.Accepted -and $strict.Verdict -ceq 'ACCEPT' -and $strict.Scenario -ceq 'FocusWorkerRetention' -and $strict.SnapshotId -ceq $SnapshotId -and $strict.ExperimentId -ceq $ExperimentId -and $strict.MeasurementRows -eq 380 -and $strict.Processes -eq 5) 'Strict acceptance differs.'
$manifest = @(Import-Csv (Join-Path $snapshotRoot 'manifest.csv'))
foreach ($item in @(
    [pscustomobject]@{Path='scripts/performance/Append-FocusWorkerRetentionMeasurements.ps1';Hash=(Get-FileHash -Algorithm SHA256 $PSCommandPath).Hash},
    [pscustomobject]@{Path='scripts/performance/Verify-FocusWorkerRetention.ps1';Hash=$strict.VerifierSHA256})) {
    $record = @($manifest | Where-Object Path -CEQ $item.Path)
    Require ($record.Count -eq 1 -and $record[0].SHA256 -ceq $item.Hash) "Frozen protocol differs: $($item.Path)"
}
$measurementBytes = [IO.File]::ReadAllBytes($measurementsPath)
Require ((Bytes-Hash $measurementBytes) -ceq $strict.MeasurementsSHA256) 'Measurements changed after strict verification.'
$encoding = [Text.UTF8Encoding]::new($false, $true)
$measurementText = $encoding.GetString($measurementBytes).TrimStart([char]0xFEFF)
$rows = @($measurementText | ConvertFrom-Csv)
Require ($rows.Count -eq 380 -and @($rows | Where-Object { $_.experiment_id -cne $ExperimentId -or $_.snapshot_id -cne $SnapshotId -or $_.perf_id -cne 'PERF-030' }).Count -eq 0) 'Measurement rows differ.'
$headerEnd = [Array]::IndexOf($measurementBytes, [byte]10)
Require ($headerEnd -ge 0 -and $measurementBytes[-1] -eq 10) 'Measurement CSV is not newline terminated.'
$suffix = [byte[]]$measurementBytes[($headerEnd + 1)..($measurementBytes.Length - 1)]
Require (@($suffix | Where-Object { $_ -eq 10 }).Count -eq 380) 'Suffix row count differs.'
$expectedLength = 40597252L
$expectedHash = '4EB8974ADA760ADB195853E1C2CABBE4C9F687BF13681D88BD226E3F890F837A'
$expectedLines = 39821
$expectedRows = 39820
$stream = [IO.File]::Open($ledgerPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    Require ($stream.Length -eq $expectedLength) 'Ledger length differs from exact prefix.'
    $prefix = [byte[]]::new([int]$expectedLength)
    $stream.ReadExactly($prefix, 0, $prefix.Length)
    Require ((Bytes-Hash $prefix) -ceq $expectedHash -and $prefix[-1] -eq 10) 'Ledger prefix hash differs.'
    $prefixText = $encoding.GetString($prefix).TrimStart([char]0xFEFF)
    Require ($prefixText.Split([char]10).Length - 1 -eq $expectedLines -and !$prefixText.Contains('"' + $ExperimentId + '"')) 'Ledger prefix rows differ or experiment already exists.'
    $ledgerHeader = $prefixText.Substring(0, $prefixText.IndexOf([char]10)).TrimEnd([char]13)
    $measurementHeader = $measurementText.Substring(0, $measurementText.IndexOf([char]10)).TrimEnd([char]13)
    Require ($ledgerHeader -ceq $measurementHeader) 'Ledger schema differs.'
    $stream.Write($suffix, 0, $suffix.Length)
    $stream.Flush($true)
    $newLength = $stream.Length
    $stream.Position = 0
    $newBytes = [byte[]]::new([int]$newLength)
    $stream.ReadExactly($newBytes, 0, $newBytes.Length)
    $newHash = Bytes-Hash $newBytes
    Require ((Range-Hash $newBytes 0 $prefix.Length) -ceq $expectedHash -and (Range-Hash $newBytes $prefix.Length $suffix.Length) -ceq (Bytes-Hash $suffix)) 'Appended prefix/suffix readback differs.'
}
finally { $stream.Dispose() }
$receipt = [ordered]@{
    Verdict='ACCEPT';SnapshotId=$SnapshotId;ExperimentId=$ExperimentId;PerfId='PERF-030';OldLength=$expectedLength;OldSHA256=$expectedHash;
    OldLines=$expectedLines;OldRows=$expectedRows;NewLength=$newLength;NewSHA256=$newHash;NewLines=$expectedLines+380;NewRows=$expectedRows+380;
    PrefixLength=$expectedLength;PrefixSHA256=$expectedHash;SuffixLength=$suffix.Length;SuffixSHA256=Bytes-Hash $suffix;AppendedRows=380;
    MeasurementsSHA256=Bytes-Hash $measurementBytes;StrictReportSHA256=(Get-FileHash -Algorithm SHA256 $strictPath).Hash;AppenderSHA256=(Get-FileHash -Algorithm SHA256 $PSCommandPath).Hash
}
$json = $receipt | ConvertTo-Json -Depth 4
$file = [IO.File]::Open($receiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $bytes = $encoding.GetBytes($json + [Environment]::NewLine)
    $file.Write($bytes, 0, $bytes.Length)
}
finally { $file.Dispose() }
$json
