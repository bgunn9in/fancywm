[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$CandidateSnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$BaselineSnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$ExperimentId,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$DependencySnapshotId,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$NativeSnapshotId = 'FWM-ANIMATION-NATIVE-BASELINE-20260908-N1'
)
# Verifies completed evidence. No build, test, benchmark, replay, GUI or package
# action runs here. The two new outputs are a measurement reverification report
# and validation-summary.json; existing evidence is never overwritten.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }
function Safe-Child([string]$Root, [string]$Relative) {
    Require (![string]::IsNullOrWhiteSpace($Relative) -and ![IO.Path]::IsPathRooted($Relative) -and
        !$Relative.Contains('\') -and !$Relative.Contains(':') -and $Relative -cnotmatch '(^|/)(\.|\.\.|)(/|$)') "Noncanonical relative path: $Relative"
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    Require ($path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) "Path escapes root: $Relative"
    $path
}
function Rel([string]$Path) { [IO.Path]::GetRelativePath($repositoryRoot, $Path).Replace('\', '/') }
function Pin([string]$Path, [string]$ExpectedHash = '') {
    Require (Test-Path -LiteralPath $Path -PathType Leaf) "Evidence file missing: $Path"
    $relative = Rel $Path
    [void](Safe-Child $repositoryRoot $relative)
    $hash = Hash $Path
    $length = (Get-Item -LiteralPath $Path).Length
    if ($ExpectedHash) { Require ($hash -ceq $ExpectedHash) "Evidence SHA256 differs: $relative" }
    if ($pins.Contains($relative)) {
        Require ($pins[$relative].SHA256 -ceq $hash -and $pins[$relative].Bytes -eq $length) "Evidence changed during verification: $relative"
    }
    else { $pins[$relative] = [ordered]@{ SHA256 = $hash; Bytes = $length } }
    $hash
}
function Read-Json([string]$Path) { [void](Pin $Path); Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Read-Xml([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return ,$document
    }
    finally { $reader.Dispose() }
}
function Segment-Hash([byte[]]$Bytes, [int]$Offset, [int]$Count) {
    $stream = [IO.MemoryStream]::new($Bytes, $Offset, $Count, $false)
    try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
}
function Read-Snapshot([string]$Id) {
    $root = Safe-Child $performanceRoot $Id
    $metadata = Read-Json (Join-Path $root 'snapshot.json')
    $manifestHash = Pin (Join-Path $root 'manifest.csv')
    Require ($metadata.SnapshotId -ceq $Id -and $metadata.Commit -ceq $head -and $metadata.Tree -ceq $tree -and
        $metadata.ManifestSHA256 -ceq $manifestHash -and ($metadata.Submodules -join '|') -ceq ($submodules -join '|')) "Snapshot identity differs: $Id"
    $rows = @(Import-Csv -LiteralPath (Join-Path $root 'manifest.csv'))
    Require ($rows.Count -gt 2300) "Unexpectedly small frozen source: $Id"
    $map = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $physical = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $sourceRoot = Join-Path $root 'source'
    foreach ($row in $rows) {
        $path = Safe-Child $sourceRoot $row.Path
        Require (($row.PSObject.Properties.Name -join '|') -ceq 'Path|SHA256' -and
            $row.SHA256 -cmatch '^[0-9A-F]{64}$' -and $map.TryAdd($row.Path, $row.SHA256) -and $physical.Add($path)) "Invalid frozen manifest row: $Id/$($row.Path)"
        Require ((Hash $path) -ceq $row.SHA256) "Frozen source differs: $Id/$($row.Path)"
    }
    $files = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Force)
    Require ($files.Count -eq $rows.Count) "Unlisted frozen files: $Id"
    foreach ($file in $files) { Require ($physical.Contains($file.FullName)) "Unlisted frozen path: $($file.FullName)" }
    [pscustomobject]@{ Id = $Id; Root = $root; Map = $map; Files = $rows.Count; ManifestSHA256 = $manifestHash; SnapshotSHA256 = Hash (Join-Path $root 'snapshot.json') }
}
function Read-RunReceipt([string]$Root, [string]$Id, [string]$Script) {
    $receipt = Read-Json (Join-Path $Root 'run-receipt.json')
    Require ($receipt.SnapshotId -ceq $Id -and $receipt.Command -ceq "pwsh -NoProfile -File scripts/performance/$Script -SnapshotId $Id" -and
        $receipt.ExitCode -is [long] -and $receipt.ExitCode -eq 0) "Run completion receipt differs: $Id"
    $start = [DateTimeOffset]$receipt.StartedUtc
    $finish = [DateTimeOffset]$receipt.FinishedUtc
    Require ($start -lt $finish -and $finish -le [DateTimeOffset]::UtcNow) "Invalid run interval: $Id"
    [pscustomobject]@{ Receipt = $receipt; Start = $start; Finish = $finish }
}
function Read-PassedTrx([string]$Path, [int]$ExpectedLeaves, [object]$Interval = $null) {
    $trxHash = Pin $Path
    $logPath = [IO.Path]::ChangeExtension($Path, '.log')
    $logHash = Pin $logPath
    $trx = Read-Xml $Path
    Require ($trx.DocumentElement.LocalName -ceq 'TestRun' -and $trx.DocumentElement.NamespaceURI -ceq 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010') "Invalid TRX root: $Path"
    $all = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]'))
    $leaves = @($all | Where-Object { $_.SelectNodes('./*[local-name()="InnerResults"]/*[local-name()="UnitTestResult"]').Count -eq 0 })
    Require ($leaves.Count -gt 0 -and ($ExpectedLeaves -eq 0 -or $leaves.Count -eq $ExpectedLeaves) -and
        @($all | Where-Object outcome -CNE 'Passed').Count -eq 0 -and @($trx.SelectNodes('//*[local-name()="ErrorInfo"]')).Count -eq 0) "TRX dimensions or outcomes differ: $Path"
    $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($leaf in $leaves) { Require ($keys.Add($leaf.testId + '|' + $leaf.testName)) "Duplicate leaf identity: $Path/$($leaf.testName)" }
    $counters = @($trx.SelectNodes('//*[local-name()="Counters"]'))
    Require ($counters.Count -eq 1 -and $trx.TestRun.ResultSummary.outcome -ceq 'Completed') "TRX result summary differs: $Path"
    foreach ($name in @('total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted', 'notRunnable', 'notExecuted', 'disconnected', 'warning', 'completed', 'inProgress', 'pending')) {
        $expected = if ($name -cin @('total', 'executed', 'passed')) { $all.Count } else { 0 }
        Require ($counters[0].GetAttribute($name) -ceq [string]$expected) "TRX counter differs: $Path/$name"
    }
    $text = Get-Content -LiteralPath $logPath -Raw
    $matches = [regex]::Matches($text, '(?im)^\s*(Passed!|Пройден!)\s*:?\s*(?:failed|не пройдено)\s*:?\s*(?<failed>\d+),\s*(?:passed|пройдено)\s*:?\s*(?<passed>\d+),\s*(?:skipped|пропущено)\s*:?\s*(?<skipped>\d+),\s*(?:total|всего)\s*:?\s*(?<total>\d+),')
    Require ($matches.Count -eq 1 -and [int]$matches[0].Groups['failed'].Value -eq 0 -and
        [int]$matches[0].Groups['skipped'].Value -eq 0 -and [int]$matches[0].Groups['passed'].Value -eq $leaves.Count -and
        [int]$matches[0].Groups['total'].Value -eq $leaves.Count -and
        $text.Replace('\', '/').IndexOf([IO.Path]::GetFullPath($Path).Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -ge 0) "Terminal/TRX results differ: $logPath"
    $start = [DateTimeOffset]::Parse($trx.TestRun.Times.start, [Globalization.CultureInfo]::InvariantCulture)
    $finish = [DateTimeOffset]::Parse($trx.TestRun.Times.finish, [Globalization.CultureInfo]::InvariantCulture)
    Require ($start -lt $finish) "Invalid TRX interval: $Path"
    if ($null -ne $Interval) { Require ($Interval.Start -le $start -and $finish -le $Interval.Finish) "TRX outside recorded run: $Path" }
    [pscustomobject]@{ Path = Rel $Path; Raw = $all.Count; Leaves = $leaves.Count; Passed = $leaves.Count; Failed = 0; Skipped = 0; Keys = @($keys); Names = @($leaves.testName); TrxSHA256 = $trxHash; LogSHA256 = $logHash; Start = $start; Finish = $finish }
}
function Git-Blob([byte[]]$Bytes, [string]$Path, [string]$WorkingDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'git'; $start.WorkingDirectory = $WorkingDirectory; $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($arg in @('hash-object', "--path=$Path", '--stdin')) { [void]$start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    Require ($process.Start()) 'Cannot start read-only git hash-object.'
    try {
        $process.StandardInput.BaseStream.Write($Bytes, 0, $Bytes.Length); $process.StandardInput.Close()
        $output = $process.StandardOutput.ReadToEnd().Trim(); $errorText = $process.StandardError.ReadToEnd(); $process.WaitForExit()
        Require ($process.ExitCode -eq 0) "Git hash-object failed: $errorText"
        $output
    }
    finally { $process.Dispose() }
}

$rootOutput = & git -C $PSScriptRoot rev-parse --show-toplevel
Require ($LASTEXITCODE -eq 0) 'Cannot resolve repository root.'
$repositoryRoot = [IO.Path]::GetFullPath(($rootOutput -join '').Trim())
$performanceRoot = Join-Path $repositoryRoot 'artifacts/performance'
$snapshotRoot = Safe-Child $performanceRoot $SnapshotId
$validationRoot = Join-Path $snapshotRoot 'validation'
$candidateRoot = Safe-Child $performanceRoot $CandidateSnapshotId
$baselineRoot = Safe-Child $performanceRoot $BaselineSnapshotId
$experimentRoot = Safe-Child $candidateRoot $ExperimentId
if (!$DependencySnapshotId) { $DependencySnapshotId = "$SnapshotId-DEPENDENCY-VERIFY" }
$dependencyRoot = Join-Path (Safe-Child $performanceRoot $DependencySnapshotId) 'dependency-check'
$nativeRoot = Join-Path (Safe-Child $performanceRoot $NativeSnapshotId) 'preflight'
$summaryPath = Join-Path $validationRoot 'validation-summary.json'
$reverificationPath = Join-Path $validationRoot 'layout-callback-measurement-reverification.json'
Require (!(Test-Path -LiteralPath $summaryPath) -and !(Test-Path -LiteralPath $reverificationPath)) 'Immutable delivery outputs already exist.'
Require (@(@($SnapshotId, $CandidateSnapshotId, $BaselineSnapshotId) | Sort-Object -Unique).Count -eq 3) 'Baseline/candidate/delivery IDs must differ.'
$pins = [ordered]@{}
$writerHash = Pin $PSCommandPath
$head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
Require ($LASTEXITCODE -eq 0 -and $head -ceq '4fb943be3ee3768bb6de42b16b0a22bdbafa2e0b') 'HEAD differs from the authorized baseline.'
$tree = (& git -C $repositoryRoot rev-parse 'HEAD^{tree}').Trim()
Require ($LASTEXITCODE -eq 0 -and $tree -ceq '48c574b1abd3adb858942bd8c6a0f4d9fb3e39b7') 'HEAD tree differs.'
$submodules = @(& git -C $repositoryRoot submodule status --recursive)
Require ($LASTEXITCODE -eq 0 -and @($submodules | Where-Object { $_ -cmatch '^[+-U]' }).Count -eq 0) 'Submodule identity differs.'
$r1 = Read-Snapshot $BaselineSnapshotId
$c1 = Read-Snapshot $CandidateSnapshotId
$c2 = Read-Snapshot $SnapshotId
Require ($r1.Map.Count -eq $c1.Map.Count) 'R1/C1 source sets differ.'
$baselineDifferences = @($c1.Map.Keys | Where-Object { !$r1.Map.ContainsKey($_) -or $r1.Map[$_] -cne $c1.Map[$_] } | Sort-Object)
Require (($baselineDifferences -join '|') -ceq 'FancyWM/Utilities/LayoutInvalidationQueue.cs') 'R1/C1 difference exceeds the cached callback.'
$deliveryAllowed = @('PERFORMANCE_STATUS.md', 'PERFORMANCE_TODO.md', 'docs/performance/AUDIT.md',
    'docs/performance/IMPLEMENTATION_RESULTS.md', 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv',
    'scripts/performance/README.md', 'scripts/performance/Verify-LayoutCallbackDelivery.ps1')
$deliveryDifferences = @(@($c1.Map.Keys) + @($c2.Map.Keys) | Sort-Object -Unique | Where-Object {
    !$c1.Map.ContainsKey($_) -or !$c2.Map.ContainsKey($_) -or $c1.Map[$_] -cne $c2.Map[$_]
})
Require (@($deliveryDifferences | Where-Object { $_ -cnotin $deliveryAllowed }).Count -eq 0 -and
    @($c1.Map.Keys | Where-Object { !$c2.Map.ContainsKey($_) }).Count -eq 0) 'C1/C2 delta exceeds compact status, ledger and delivery verifier additions.'
$buildInputs = @($c2.Map.Keys | Where-Object { $_ -match '\.(cs|csproj|xaml|props|targets|sln|slnx|ps1)$|(^|/)(global\.json|NuGet\.config|version\.json)$' })
foreach ($relative in $buildInputs) {
    Require ((Hash (Safe-Child $repositoryRoot $relative)) -ceq $c2.Map[$relative]) "Live build input differs from C2: $relative"
}
Require ($c2.Map['scripts/performance/Verify-LayoutCallbackDelivery.ps1'] -ceq $writerHash) 'Delivery verifier is not the frozen C2 source.'
$functionalPins = @{
    'TODO.md' = 'C5AE4822AC1B62A92E66FE4C282035CD441F096CD93358DCE2E79119972C61D1'
    'IMPLEMENTATION_STATUS.md' = '580E05F35A0D6EEE150B175E7886FC87F2B2FAB76025B498BC656A7E9830335D'
}
foreach ($relative in $functionalPins.Keys) {
    [void](Pin (Safe-Child $repositoryRoot $relative) $functionalPins[$relative])
    foreach ($snapshot in @($r1, $c1, $c2)) { Require ($snapshot.Map[$relative] -ceq $functionalPins[$relative]) "Functional document changed: $relative" }
}

$strictPath = Join-Path $candidateRoot 'validation/layout-callback-strict-verification.json'
$strict = Read-Json $strictPath
Require ($strict.Verdict -ceq 'ACCEPT' -and $strict.Accepted -is [bool] -and $strict.Accepted -and
    $strict.Scenario -ceq 'LayoutCallbackCache' -and $strict.PerfId -ceq 'PERF-004' -and
    $strict.BaselineSnapshot -ceq $BaselineSnapshotId -and $strict.CandidateSnapshot -ceq $CandidateSnapshotId -and $strict.ExperimentId -ceq $ExperimentId -and
    $strict.MeasurementRows -eq 300 -and $strict.RawCounters -eq 300 -and $strict.ProcessCount -eq 10 -and
    $strict.AllocationPairs -eq 15 -and $strict.PairedSemanticComparisons -eq 90 -and
    $strict.TimingAcceptanceGate -is [bool] -and !$strict.TimingAcceptanceGate -and
    $strict.BaselineManifestSHA256 -ceq $r1.ManifestSHA256 -and $strict.CandidateManifestSHA256 -ceq $c1.ManifestSHA256 -and
    $strict.BaselineSnapshotSHA256 -ceq $r1.SnapshotSHA256 -and $strict.CandidateSnapshotSHA256 -ceq $c1.SnapshotSHA256) 'C1 strict acceptance differs.'
foreach ($entry in @(@('measurements.csv', 'MeasurementsSHA256'), @('layout-callback-binary-checks.csv', 'BinaryChecksSHA256'), @('layout-callback-process-times.csv', 'ProcessTimesSHA256'))) {
    [void](Pin (Join-Path $experimentRoot $entry[0]) $strict.($entry[1]))
}
[void](Pin (Join-Path $baselineRoot 'release-test-binaries.csv') $strict.BaselineArchiveManifestSHA256)
[void](Pin (Join-Path $baselineRoot 'release-test-binaries.provenance.json') $strict.BaselineProvenanceSHA256)
$measurementVerifier = Join-Path $c2.Root 'source/scripts/performance/Verify-LayoutCallbackCache.ps1'
[void](Pin $measurementVerifier $strict.VerifierSHA256)
foreach ($process in $strict.Processes) {
    Require ($process.Run -cmatch '^(baseline|candidate)-[1-5]$') 'Unexpected measurement process name.'
    [void](Pin (Join-Path $experimentRoot "$($process.Run).trx") $process.TrxSHA256)
    [void](Pin (Join-Path $experimentRoot "$($process.Run).log") $process.LogSHA256)
}
$appendPath = Join-Path $experimentRoot 'ledger-append.json'
$append = Read-Json $appendPath
$prefixLength = 37478593L
$prefixHash = '6B5AC23BB9A8AC86A3FBD6CA6FBC471E646684F69D49A6271921917C83745E62'
Require ($append.Verdict -ceq 'ACCEPT' -and $append.PerfId -ceq 'PERF-004' -and $append.SnapshotId -ceq $CandidateSnapshotId -and $append.ExperimentId -ceq $ExperimentId -and
    $append.LedgerPath -ceq 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv' -and $append.OldLength -eq $prefixLength -and $append.PrefixLength -eq $prefixLength -and
    $append.OldSHA256 -ceq $prefixHash -and $append.PrefixSHA256 -ceq $prefixHash -and $append.OldRows -eq 36750 -and $append.OldLines -eq 36751 -and
    $append.AppendedRows -eq 300 -and $append.NewRows -eq 37050 -and $append.NewLines -eq 37051 -and
    $append.MeasurementsSHA256 -ceq $strict.MeasurementsSHA256 -and $append.StrictReportSHA256 -ceq (Hash $strictPath)) 'Ledger append receipt differs.'
[void](Pin (Join-Path $repositoryRoot 'scripts/performance/Append-LayoutCallbackMeasurements.ps1') $append.AppenderSHA256)
$ledgerPath = Join-Path $repositoryRoot $append.LedgerPath
[void](Pin $ledgerPath $append.NewSHA256)
Require ($c2.Map[$append.LedgerPath] -ceq $append.NewSHA256 -and $r1.Map[$append.LedgerPath] -ceq $prefixHash -and $c1.Map[$append.LedgerPath] -ceq $prefixHash) 'Frozen ledger identities differ.'
$ledger = [IO.File]::ReadAllBytes($ledgerPath)
$csv = [IO.File]::ReadAllBytes((Join-Path $experimentRoot 'measurements.csv'))
$headerEnd = [Array]::IndexOf($csv, [byte]10)
Require ($headerEnd -ge 0 -and $ledger.Length -eq $append.NewLength -and $csv[-1] -eq 10 -and $ledger[-1] -eq 10) 'Ledger dimensions/newlines differ.'
$suffixLength = $csv.Length - $headerEnd - 1
Require ($suffixLength -eq $append.SuffixLength -and $ledger.Length -eq $prefixLength + $suffixLength -and
    (Segment-Hash $ledger 0 $prefixLength) -ceq $prefixHash -and
    (Segment-Hash $csv ($headerEnd + 1) $suffixLength) -ceq $append.SuffixSHA256 -and
    (Segment-Hash $ledger $prefixLength $suffixLength) -ceq $append.SuffixSHA256) 'Historical ledger prefix or exact accepted suffix differs.'
$ledgerText = [Text.UTF8Encoding]::new($false, $true).GetString($ledger)
Require ([regex]::Matches($ledgerText, '\n').Count -eq 37051 -and
    [regex]::Matches($ledgerText, '(?m)^"' + [regex]::Escape($ExperimentId) + '",').Count -eq 300) 'Ledger row counts differ.'

$validationRun = Read-RunReceipt $validationRoot $SnapshotId 'Validate-Implementation.ps1'
$validationScript = Get-Content -LiteralPath (Join-Path $c2.Root 'source/scripts/performance/Validate-Implementation.ps1') -Raw
Require ($validationScript.Contains('/p:Platform=x64 /p:RuntimeIdentifier=win-x64') -and $validationScript.Contains('/p:GenerateTemporaryStoreCertificate=False')) 'Frozen validation is not RID-aware x64/MSIX.'
$builds = @(foreach ($configuration in @('Debug', 'Release')) {
    foreach ($kind in @('restore', 'gui', 'full')) {
        $path = Join-Path $validationRoot "$configuration-$kind.log"
        $hash = Pin $path
        $text = Get-Content -LiteralPath $path -Raw
        Require ($text -notmatch '(?im)(:\s*(error|ошибка)\s+[A-Z]+\d+|Build FAILED|Сборка не удалась)') "Build failure marker: $path"
        if ($kind -ceq 'restore') { Require ($text -match 'Все проекты обновлены для восстановления|All projects are up-to-date for restore|Восстановлен|Restored') "Restore completion missing: $path" }
        elseif ($kind -ceq 'gui') { Require ($text -match '(?m)^\s*(Ошибок:\s*0|0 Error\(s\))\s*$' -and $text.Contains('FancyWM.GUI -> ')) "GUI completion missing: $path" }
        else {
            $packageName = if ($configuration -ceq 'Debug') { 'FancyWM.Package_0.0.0.0_x64_Debug.msix' } else { 'FancyWM.Package_0.0.0.0_x64.msix' }
            Require ($text.Contains($packageName) -and $text.Contains('FancyWM.GUI -> ') -and $text.Contains('FancyWM.ThemeEngine.Tests -> ')) "Full build outputs missing: $path"
        }
        [pscustomobject]@{ Configuration = $configuration; Kind = $kind; Result = 'PASS'; Log = Rel $path; SHA256 = $hash }
    }
})
[void](Pin (Join-Path $validationRoot 'dependency-patches.log'))
$tests = @(foreach ($configuration in @('Debug', 'Release')) {
    foreach ($project in @('FancyWM.Tests', 'FancyWM.Layouts.Tests', 'FancyWM.ThemeEngine.Tests')) {
        $expected = switch ($project) { 'FancyWM.Tests' { if ($configuration -ceq 'Debug') { 1973 } else { 2029 } } 'FancyWM.Layouts.Tests' { 160 } 'FancyWM.ThemeEngine.Tests' { 31 } }
        Read-PassedTrx (Join-Path $validationRoot "$configuration-$project.trx") $expected $validationRun
    }
})
$priorRoot = Join-Path $performanceRoot 'FWM-REFRESH-WINDOW-SNAPSHOT-20260908-C2'
$priorSummaryPath = Join-Path $priorRoot 'validation/validation-summary.json'
[void](Pin $priorSummaryPath '6195C331AE525D184076CD2F9765FD014562DF288805E78890751A98BEA9F248')
$prior = Read-Json $priorSummaryPath
Require ($prior.Verdict -ceq 'ACCEPT') 'Prior accepted gate changed.'
$newMethods = @('CallbackCacheReusesScheduledDelegateAcrossCompletedPasses', 'CallbackCacheDoesNotRootDisposedQueueAfterCompletedPass', 'LayoutCallbackCounterScenario')
$candidateTests = @(foreach ($configuration in @('Debug', 'Release')) {
    $targeted = Read-PassedTrx (Join-Path $candidateRoot "validation/$configuration-layout-callback-targeted.trx") 3
    Require ((@($targeted.Names | Sort-Object) -join '|') -ceq (@($newMethods | Sort-Object) -join '|')) "Targeted test composition differs: $configuration"
    $targeted
    $affected = Read-PassedTrx (Join-Path $candidateRoot "validation/$configuration-layout-callback-affected.trx") 0
    Require ($affected.Leaves -gt 3) "Affected suite is incomplete: $configuration"
    foreach ($name in $newMethods) { Require ($name -cin $affected.Names) "Affected regression is missing: $configuration/$name" }
    $affected
})
$composition = @(foreach ($test in $tests) {
    $leafName = [IO.Path]::GetFileName($test.Path)
    $previous = @($prior.Tests | Where-Object { [IO.Path]::GetFileName($_.Path) -ceq $leafName })
    Require ($previous.Count -eq 1) "Prior suite missing: $leafName"
    $priorTrxPath = Safe-Child $repositoryRoot $previous[0].Path
    [void](Pin $priorTrxPath $previous[0].TrxSHA256)
    $priorTrx = Read-Xml $priorTrxPath
    $priorLeaves = @($priorTrx.SelectNodes('//*[local-name()="UnitTestResult"]') | Where-Object { $_.SelectNodes('./*[local-name()="InnerResults"]/*[local-name()="UnitTestResult"]').Count -eq 0 })
    $previousKeys = @($priorLeaves | ForEach-Object { $_.testId + '|' + $_.testName })
    $differences = @(Compare-Object $previousKeys $test.Keys -CaseSensitive)
    Require (@($differences | Where-Object SideIndicator -CEQ '<=').Count -eq 0) "Previously accepted tests disappeared: $leafName"
    $added = @($differences | Where-Object SideIndicator -CEQ '=>' | ForEach-Object { ($_.InputObject -split '\|', 2)[1] } | Sort-Object)
    $expectedAdded = if ($leafName -like '*-FancyWM.Tests.trx') { @($newMethods | Sort-Object) } else { @() }
    Require (($added -join '|') -ceq ($expectedAdded -join '|')) "Full test additions differ: $leafName"
    [pscustomobject]@{ Suite = $leafName; PreviousLeaves = $priorLeaves.Count; Leaves = $test.Leaves; Added = $added; Removed = 0 }
})

$dependencyRun = Read-RunReceipt $dependencyRoot $DependencySnapshotId 'Test-DependencyPatches.ps1'
$dependencyTranscriptPath = Join-Path $dependencyRoot 'run.log'
[void](Pin $dependencyTranscriptPath)
$dependencyTranscript = Get-Content -LiteralPath $dependencyTranscriptPath -Raw
Require ($dependencyTranscript.Contains('Unknown local hunk rejected and preserved in isolated conflict fixture.') -and
    $dependencyTranscript.Contains('Clean pinned archive applies once, repeated apply/check succeeds, Release dependency builds:')) 'Dependency replay completion is missing.'
$patchManifestPath = Join-Path $repositoryRoot 'patches/winman-windows/manifest.json'
$patchManifest = Read-Json $patchManifestPath
$pinnedDependency = 'adb9f55b84b567db9f6e0ee8df4c33d11a12d91f'
Require ($patchManifest.pinned_commit -ceq $pinnedDependency -and $patchManifest.files.Count -eq 4) 'Dependency patch identity differs.'
[void](Pin (Safe-Child $repositoryRoot $patchManifest.patch) $patchManifest.patch_sha256)
$gitlinks = @(foreach ($name in @('ModernWpf', 'winman', 'winman-windows')) {
    $committed = (& git -C $repositoryRoot ls-tree HEAD -- $name).Trim(); Require ($LASTEXITCODE -eq 0) 'Committed gitlink lookup failed.'
    $indexed = (& git -C $repositoryRoot ls-files --stage -- $name).Trim(); Require ($LASTEXITCODE -eq 0) 'Index gitlink lookup failed.'
    $match = [regex]::Match($committed, '^160000 commit ([0-9a-f]{40})\s+' + [regex]::Escape($name) + '$')
    Require ($match.Success -and $indexed -cmatch ('^160000 ' + $match.Groups[1].Value + ' 0\s+' + [regex]::Escape($name) + '$')) 'Committed/index gitlink differs.'
    $working = (& git -C (Join-Path $repositoryRoot $name) rev-parse HEAD).Trim()
    Require ($LASTEXITCODE -eq 0 -and $working -ceq $match.Groups[1].Value -and ($name -cne 'winman-windows' -or $working -ceq $pinnedDependency)) "Dependency working HEAD differs: $name"
    [pscustomobject]@{ Path = $name; Committed = $committed; Index = $indexed; WorkingHEAD = $working }
})
foreach ($file in @('build.log', 'conflict.log', 'binary.csv', 'winman.zip', 'winman-windows.zip')) { [void](Pin (Join-Path $dependencyRoot $file)) }
$dependencyBuild = Get-Content -LiteralPath (Join-Path $dependencyRoot 'build.log') -Raw
Require ($dependencyBuild -match '(?m)^\s*(Ошибок:\s*0|0 Error\(s\))\s*$' -and $dependencyBuild -notmatch '(?im):\s*(error|ошибка)\s+[A-Z]+\d+') 'Dependency build failed.'
Require ((Get-Content -LiteralPath (Join-Path $dependencyRoot 'conflict.log') -Raw).Contains('Conflict: preserve local changes in src/WinMan.Windows/Windows/Win32Workspace.cs')) 'Dependency conflict rejection differs.'
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $dependencyRoot 'winman-windows.zip'))
try {
    $dependencyFiles = @(for ($index = 0; $index -lt $patchManifest.files.Count; $index++) {
        $record = $patchManifest.files[$index]
        $entries = @($zip.Entries | Where-Object FullName -CEQ $record.path)
        Require ($entries.Count -eq 1) "Pinned archive entry differs: $($record.path)"
        $stream = $entries[0].Open(); $memory = [IO.MemoryStream]::new()
        try { $stream.CopyTo($memory); $baseBytes = $memory.ToArray() } finally { $memory.Dispose(); $stream.Dispose() }
        $working = Join-Path $repositoryRoot 'winman-windows'
        Require ((Git-Blob $baseBytes $record.path $working) -ceq $record.base_blob) 'Pinned archive base blob differs.'
        $replay = Safe-Child (Join-Path $dependencyRoot 'source/winman-windows') $record.path
        $live = Safe-Child $working $record.path
        $replayBytes = [IO.File]::ReadAllBytes($replay); $liveBytes = [IO.File]::ReadAllBytes($live)
        Require ((Git-Blob $replayBytes $record.path $working) -ceq $record.applied_blob -and
            (Git-Blob $liveBytes $record.path $working) -ceq $record.applied_blob) 'Applied dependency replay/live blob differs.'
        [void](Pin $live $c2.Map["winman-windows/$($record.path)"])
        $replayHash = Pin $replay
        $conflict = Safe-Child (Join-Path $dependencyRoot 'conflict-fixture') $record.path
        $conflictBytes = [IO.File]::ReadAllBytes($conflict)
        if ($index -eq 0) {
            Require ($conflictBytes.Length -eq $replayBytes.Length + 23 -and
                (Segment-Hash $conflictBytes 0 $replayBytes.Length) -ceq $replayHash -and
                [Text.Encoding]::UTF8.GetString($conflictBytes, $replayBytes.Length, 23) -ceq "local-change-sentinel`r`n") 'Conflict sentinel or preserved prefix differs.'
        }
        else { Require ((Hash $conflict) -ceq $replayHash) 'Conflict fixture mutated another file.' }
        [pscustomobject]@{ Path = $record.path; BaseBlob = $record.base_blob; AppliedBlob = $record.applied_blob; ReplaySHA256 = $replayHash; LiveSHA256 = Hash $live; ConflictSHA256 = Pin $conflict; Sentinel = ($index -eq 0) }
    })
}
finally { $zip.Dispose() }
$dependencyBinary = @(Import-Csv -LiteralPath (Join-Path $dependencyRoot 'binary.csv'))
$dependencyBinaryPath = Join-Path $dependencyRoot 'source/winman-windows/src/WinMan.Windows/bin/Release/net10.0-windows7.0/WinMan.Windows.dll'
Require ($dependencyBinary.Count -eq 1 -and ($dependencyBinary[0].PSObject.Properties.Name -join '|') -ceq 'Path|Hash' -and
    [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($dependencyBinary[0].Path), $dependencyBinaryPath)) 'Dependency binary provenance differs.'
[void](Pin $dependencyBinaryPath $dependencyBinary[0].Hash)
$dependencyScript = Get-Content -LiteralPath (Join-Path $c2.Root 'source/scripts/performance/Test-DependencyPatches.ps1') -Raw
Require ($dependencyScript.Contains('foreach ($mode in @(''Check'', ''Apply'', ''Apply'', ''Check''))') -and
    $dependencyScript.Contains('throw ''Conflict was not rejected without mutation''')) 'Frozen dependency protocol differs.'

$provenancePath = Join-Path $snapshotRoot 'release/provenance.json'
$provenance = Read-Json $provenancePath
$expectedPackageSource = 'FancyWM.Package/AppPackages/FancyWM.Package_0.0.0.0_x64_Test/FancyWM.Package_0.0.0.0_x64.msix'
Require ($provenance.Snapshot -ceq $SnapshotId -and $provenance.SnapshotManifestSHA256 -ceq $c2.ManifestSHA256 -and
    $provenance.ValidationExitCode -eq 0 -and $provenance.ValidationCommand -ceq $validationRun.Receipt.Command -and
    $provenance.ReleaseBuildLog -ceq 'validation/Release-full.log' -and $provenance.ReleaseBuildLogSHA256 -ceq (Hash (Join-Path $validationRoot 'Release-full.log')) -and
    $provenance.Source -ceq $expectedPackageSource -and
    $provenance.Artifact -ceq 'release/FancyWM.Package_0.0.0.0_x64.msix' -and $provenance.EmbeddedFancyWMDllPath -ceq 'FancyWM.GUI/FancyWM.dll') 'Release package provenance differs.'
foreach ($flag in @('Installed', 'Launched', 'Published')) { Require ($provenance.$flag -is [bool] -and !$provenance.$flag) "Unexpected package action: $flag" }
$sourceArtifact = Safe-Child $repositoryRoot $provenance.Source
$sourceArtifactHash = Pin $sourceArtifact
$sourceArtifactItem = Get-Item -LiteralPath $sourceArtifact
$sourceArtifactTime = [DateTimeOffset]$provenance.SourceLastWriteUtc
$releaseFullText = Get-Content -LiteralPath (Join-Path $validationRoot 'Release-full.log') -Raw
$releasePackageOutputs = [regex]::Matches(
    $releaseFullText, '(?m)^[ \t]*FancyWM\.Package -> (?<path>[^\r\n]+\.msix)[ \t]*\r?$')
Require ($sourceArtifactHash -ceq $provenance.ArtifactSHA256 -and
    $sourceArtifactItem.Length -eq $provenance.ArtifactLength -and
    $sourceArtifactItem.LastWriteTimeUtc -eq $sourceArtifactTime.UtcDateTime -and
    $validationRun.Start -le $sourceArtifactTime -and $sourceArtifactTime -le $validationRun.Finish -and
    $releasePackageOutputs.Count -eq 1 -and
    [StringComparer]::OrdinalIgnoreCase.Equals(
        [IO.Path]::GetFullPath($releasePackageOutputs[0].Groups['path'].Value.Trim()),
        [IO.Path]::GetFullPath($sourceArtifact))) 'Release package source is not the exact output logged by this gate.'
$artifact = Safe-Child $snapshotRoot $provenance.Artifact
[void](Pin $artifact $provenance.ArtifactSHA256)
Require ((Get-Item -LiteralPath $artifact).Length -eq $provenance.ArtifactLength -and $provenance.ArtifactLength -gt 0) 'Archived package length differs.'
$package = [IO.Compression.ZipFile]::OpenRead($artifact)
try {
    $entries = @($package.Entries | Where-Object FullName -CEQ $provenance.EmbeddedFancyWMDllPath)
    Require ($entries.Count -eq 1 -and $entries[0].Length -eq $provenance.EmbeddedFancyWMDllLength -and $entries[0].Length -gt 0) 'Embedded FancyWM.dll dimensions differ.'
    $inputStream = $entries[0].Open(); $memory = [IO.MemoryStream]::new()
    try { $inputStream.CopyTo($memory); $embeddedBytes = $memory.ToArray() } finally { $memory.Dispose(); $inputStream.Dispose() }
    $embeddedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($embeddedBytes))
    Require ($embeddedHash -ceq $provenance.EmbeddedFancyWMDllSHA256 -and $embeddedBytes.Length -gt 64 -and
        [BitConverter]::ToUInt16($embeddedBytes, 0) -eq 0x5A4D) 'Embedded assembly hash or DOS header differs.'
    $peOffset = [BitConverter]::ToInt32($embeddedBytes, 0x3C)
    Require ($peOffset -gt 0 -and $peOffset + 26 -le $embeddedBytes.Length -and
        [BitConverter]::ToUInt32($embeddedBytes, $peOffset) -eq 0x4550 -and
        [BitConverter]::ToUInt16($embeddedBytes, $peOffset + 4) -eq 0x8664 -and
        [BitConverter]::ToUInt16($embeddedBytes, $peOffset + 24) -eq 0x20B) 'Embedded assembly is not AMD64 PE32+.'
    $manifestEntries = @($package.Entries | Where-Object FullName -CEQ 'AppxManifest.xml')
    Require ($manifestEntries.Count -eq 1) 'Package manifest entry differs.'
    $inputStream = $manifestEntries[0].Open()
    $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($inputStream, $settings)
    try { $manifest = [Xml.XmlDocument]::new(); $manifest.XmlResolver = $null; $manifest.Load($reader) } finally { $reader.Dispose(); $inputStream.Dispose() }
    Require ($manifest.Package.Identity.ProcessorArchitecture -ceq 'x64') 'MSIX processor architecture differs.'
}
finally { $package.Dispose() }
[void](Pin (Join-Path $repositoryRoot 'FancyWM/bin/x64/Release/net10.0-windows10.0.18362.0/win-x64/FancyWM.dll') $embeddedHash)

$nativeSummaryPath = Join-Path $nativeRoot 'native-preflight-summary.json'
[void](Pin $nativeSummaryPath 'B04F981CDC37B0B57EF8F46CC1A5C617FBEDBBE7800575F6EDEBE2621CAF79C7')
$native = Read-Json $nativeSummaryPath
Require ($native.SnapshotId -ceq $NativeSnapshotId -and $native.Commit -ceq $head -and $native.Verdict -ceq 'NATIVE_BLOCKED' -and
    !$native.Elevated -and $native.Interactive -and !$native.RecordingActiveBefore -and !$native.RecordingActiveAfter -and !$native.RawEtlCreated -and
    $native.WprAttempt.ExitCode -eq -984068079 -and $native.WprAttempt.ExitCodeHex -ceq '0xC5585011' -and
    ($native.WprAttempt.Arguments -join '|') -ceq '-start|CPU|-filemode' -and $native.Evidence.Count -eq 6) 'Native preflight blocker evidence differs.'
foreach ($entry in $native.Evidence) {
    $path = Safe-Child $nativeRoot $entry.Path
    [void](Pin $path $entry.SHA256)
    Require ((Get-Item -LiteralPath $path).Length -eq $entry.Length) "Native preflight evidence length differs: $($entry.Path)"
}
$wprAttempt = Read-Json (Join-Path $nativeRoot 'wpr-start-cpu.json')
Require ($wprAttempt.ExitCode -eq -984068079 -and $wprAttempt.ExitCodeHex -ceq '0xC5585011' -and
    ($wprAttempt.Arguments -join '|') -ceq '-start|CPU|-filemode' -and
    $wprAttempt.StdErr.Contains('Failed to enable the policy to profile system performance.') -and
    $wprAttempt.StdErr.Contains('Profile Id: CPU.Verbose.File') -and @(Get-ChildItem -LiteralPath $nativeRoot -Recurse -File -Filter '*.etl').Count -eq 0) 'Raw WPR failure differs or an unexpected ETL exists.'

# Re-run the exact frozen measurement verifier only after all delivery evidence
# checks have passed. It re-reads the complete source/archive/binary/raw-TRX
# matrix; no new performance measurement or test process is started.
$null = & $measurementVerifier -SnapshotId $CandidateSnapshotId -BaselineSnapshotId $BaselineSnapshotId -ExperimentId $ExperimentId -ReportPath $reverificationPath
$reverified = Read-Json $reverificationPath
$originalComparable = [ordered]@{}; $reverifiedComparable = [ordered]@{}
foreach ($property in $strict.PSObject.Properties) { if ($property.Name -cne 'VerifiedAtUtc') { $originalComparable[$property.Name] = $property.Value } }
foreach ($property in $reverified.PSObject.Properties) { if ($property.Name -cne 'VerifiedAtUtc') { $reverifiedComparable[$property.Name] = $property.Value } }
Require (($originalComparable | ConvertTo-Json -Depth 12 -Compress) -ceq ($reverifiedComparable | ConvertTo-Json -Depth 12 -Compress)) 'Fresh raw measurement reverification differs from accepted C1 evidence.'
foreach ($relative in @($pins.Keys)) { [void](Pin (Safe-Child $repositoryRoot $relative)) }
foreach ($relative in $buildInputs) { Require ((Hash (Safe-Child $repositoryRoot $relative)) -ceq $c2.Map[$relative]) "Build input changed during verification: $relative" }
$summary = [ordered]@{
    Verdict = 'ACCEPT'; ReportVersion = 1; SnapshotId = $SnapshotId; PerfId = 'PERF-004'
    BoundedStage = 'LayoutInvalidationQueue.ScheduleIfNeededCore cached instance callback'
    Commit = $head; Tree = $tree; SnapshotManifestSHA256 = $c2.ManifestSHA256; SnapshotJSONSHA256 = $c2.SnapshotSHA256
    SnapshotSourceFiles = $c2.Files; AllFrozenSourceFilesVerified = $r1.Files + $c1.Files + $c2.Files; BuildInputsVerified = $buildInputs.Count
    BaselineSnapshot = $BaselineSnapshotId; CandidateSnapshot = $CandidateSnapshotId
    BaselineManifestSHA256 = $r1.ManifestSHA256; CandidateManifestSHA256 = $c1.ManifestSHA256
    BaselineCandidateSourceDifferences = $baselineDifferences; CandidateC2Differences = $deliveryDifferences
    PinnedRawEvidence = $pins
    C1MeasurementAcceptance = [ordered]@{
        Verdict = 'ACCEPT'; StrictReportSHA256 = Hash $strictPath; ReverificationSHA256 = Hash $reverificationPath
        VerifierSHA256 = $strict.VerifierSHA256; MeasurementsSHA256 = $strict.MeasurementsSHA256; ProductionUnchangedInC2 = $true
        ProcessCount = 10; MeasurementRows = 300; RawCounters = 300; AllocationPairs = 15; SemanticComparisons = 90
        TimingClassification = $strict.TimingClassification; TimingWins = $strict.PairedTimingWins; TimingLosses = $strict.PairedTimingLosses
        TimingTies = $strict.PairedTimingTies; TimingAcceptanceGate = $false; Allocation = $strict.Allocation; Timing = $strict.Timing
    }
    Builds = $builds
    Tests = @($tests | Select-Object Path, Raw, Leaves, Passed, Failed, Skipped, TrxSHA256, LogSHA256, Start, Finish)
    CandidateTargetedAndAffectedTests = @($candidateTests | Select-Object Path, Raw, Leaves, Passed, Failed, Skipped, TrxSHA256, LogSHA256, Start, Finish)
    TestCompositionAgainstAcceptedPreviousGate = $composition
    OptimizationLedger = [ordered]@{
        Rows = 37050; Bytes = $ledger.Length; SHA256 = Hash $ledgerPath; PreviousRows = 36750; PreviousBytes = $prefixLength; PreviousSHA256 = $prefixHash
        AppendedRows = 300; AppendedBytes = $suffixLength; AppendedSHA256 = $append.SuffixSHA256; ExactHistoricalPrefix = $true; ExactAcceptedSuffix = $true; AppendProofSHA256 = Hash $appendPath
    }
    DependencyReplay = [ordered]@{
        SnapshotId = $DependencySnapshotId; Verdict = 'ACCEPT'; PinnedCommit = $pinnedDependency; Gitlinks = $gitlinks; Submodules = $submodules
        Files = $dependencyFiles; BinarySHA256 = $dependencyBinary[0].Hash; ConflictPreserved = $true; Protocol = @('Check', 'Apply', 'Apply', 'Check')
        ExitCode = 0; ReceiptSHA256 = Hash (Join-Path $dependencyRoot 'run-receipt.json'); NativeActionsPerformed = $false
    }
    Commands = [ordered]@{ FullValidation = $validationRun.Receipt.Command; FullValidationExitCode = 0; DependencyReplay = $dependencyRun.Receipt.Command; DependencyReplayExitCode = 0 }
    ReleaseArtifact = [ordered]@{
        Path = Rel $artifact; Bytes = $provenance.ArtifactLength; SHA256 = $provenance.ArtifactSHA256
        Source = $provenance.Source; SourceSHA256 = $sourceArtifactHash; SourceLastWriteUtc = $sourceArtifactTime.ToString('O')
        EmbeddedFancyWMDllPath = $provenance.EmbeddedFancyWMDllPath; EmbeddedFancyWMDllBytes = $embeddedBytes.Length; EmbeddedFancyWMDllSHA256 = $embeddedHash
        Architecture = 'x64'; EmbeddedArchitecture = 'AMD64 PE32+'; ProvenanceSHA256 = Hash $provenancePath; Installed = $false; Launched = $false; Published = $false
    }
    NativePreflight = [ordered]@{
        PerfId = 'PERF-010'; SnapshotId = $NativeSnapshotId; Verdict = 'NATIVE_BLOCKED'; SummarySHA256 = Hash $nativeSummaryPath
        Blocker = $native.Blocker; WprAttempt = $native.WprAttempt; Display = $native.Display; Video = $native.Video; RawEtlCreated = $false
        RequiredExternalDependency = 'Elevated interactive Windows performance recording token able to enable system profiling policy, with the fixed Release x64 transition fixture.'
        CPU = 'NOT_MEASURED'; GPU = 'NOT_MEASURED'; Scheduling = 'NOT_MEASURED'; NativeCalls = 'NOT_MEASURED'; PresentationLatency = 'NOT_MEASURED'
    }
    FunctionalDocuments = [ordered]@{ SHA256 = $functionalPins; PreservedAcrossR1_C1_C2_AndLive = $true }
    StatusCounts = [ordered]@{ IMPLEMENTED = 23; IN_PROGRESS = 14; WholeIdBlocked = 0 }
    Limitations = 'Managed queue scheduling only. Timing is descriptive; no general speedup claim. PERF-004 remains IMPLEMENTED and PERF-010 remains IN_PROGRESS. Native Dispatcher/WPF/DPI, transition scheduling/call fan-out, presentation latency, process CPU/GPU and energy remain E2E_PENDING / NOT_MEASURED.'
    SummaryWriterSHA256 = $writerHash; CreatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$json = $summary | ConvertTo-Json -Depth 14
$stream = [IO.File]::Open($summaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json + [Environment]::NewLine); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
finally { $stream.Dispose() }
[pscustomobject]@{ Verdict = 'ACCEPT'; SummaryPath = Rel $summaryPath; SummarySHA256 = Hash $summaryPath; WriterSHA256 = $writerHash; PinnedFiles = $pins.Count; TestRuns = $tests.Count; PackageSHA256 = Hash $artifact; NativeVerdict = 'NATIVE_BLOCKED' } | ConvertTo-Json
