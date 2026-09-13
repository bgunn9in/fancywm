param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$ExperimentId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }
function Load-Json([string]$Path) { Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -DateKind String }
function Load-TrxStats([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try { $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null; $document.Load($reader) }
    finally { $reader.Dispose() }
    $all = @($document.SelectNodes('//*[local-name()="UnitTestResult"]'))
    $leaves = @($all | Where-Object { $_.SelectNodes('./*[local-name()="InnerResults"]/*[local-name()="UnitTestResult"]').Count -eq 0 })
    [pscustomobject]@{Raw=$all.Count;Leaves=$leaves.Count;Passed=@($leaves|Where-Object outcome -CEQ 'Passed').Count;Failed=@($leaves|Where-Object outcome -CNE 'Passed').Count;Names=@($leaves.testName|Sort-Object);SHA256=Hash $Path}
}

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Require ($LASTEXITCODE -eq 0) 'Cannot resolve repository root.'
$head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$tree = (& git -C $repositoryRoot rev-parse 'HEAD^{tree}').Trim()
Require ($head -ceq '4fb943be3ee3768bb6de42b16b0a22bdbafa2e0b' -and $tree -ceq '48c574b1abd3adb858942bd8c6a0f4d9fb3e39b7') 'HEAD/tree differs.'
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$validationRoot = Join-Path $snapshotRoot 'validation'
$experimentRoot = Join-Path $snapshotRoot $ExperimentId
$manifestPath = Join-Path $snapshotRoot 'manifest.csv'
$snapshotPath = Join-Path $snapshotRoot 'snapshot.json'
$manifest = @(Import-Csv $manifestPath)
$snapshot = Load-Json $snapshotPath
Require ($snapshot.SnapshotId -ceq $SnapshotId -and $snapshot.Commit -ceq $head -and $snapshot.Tree -ceq $tree -and $snapshot.ManifestSHA256 -ceq (Hash $manifestPath)) 'Snapshot identity differs.'
foreach ($record in $manifest) { Require ((Hash (Join-Path $snapshotRoot "source/$($record.Path)")) -ceq $record.SHA256) "Frozen source differs: $($record.Path)" }
$delivery = @($manifest | Where-Object Path -CEQ 'scripts/performance/Verify-FocusWorkerRetentionDelivery.ps1')
Require ($delivery.Count -eq 1 -and $delivery[0].SHA256 -ceq (Hash $PSCommandPath)) 'Executing delivery verifier differs from frozen source.'
foreach ($name in @('TODO.md','IMPLEMENTATION_STATUS.md')) {
    $expected = if ($name -ceq 'TODO.md') {'C5AE4822AC1B62A92E66FE4C282035CD441F096CD93358DCE2E79119972C61D1'} else {'580E05F35A0D6EEE150B175E7886FC87F2B2FAB76025B498BC656A7E9830335D'}
    Require ((Hash (Join-Path $repositoryRoot $name)) -ceq $expected -and (Hash (Join-Path $snapshotRoot "source/$name")) -ceq $expected) "Functional document changed: $name"
}
$previousRoot = Join-Path $repositoryRoot 'artifacts/performance/FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C2'
$previousSummaryPath = Join-Path $previousRoot 'validation/validation-summary.json'
Require ((Hash $previousSummaryPath) -ceq '7DD7DE5BB0EB990C640F83F3F8D2B7F80437E41E212F2FE77B8148BD1C71DC44') 'Previous accepted C2 summary changed.'
Require ((Hash (Join-Path $snapshotRoot 'source/FancyWM/Utilities/FocusHelper.cs')) -ceq (Hash (Join-Path $previousRoot 'source/FancyWM/Utilities/FocusHelper.cs'))) 'Production FocusHelper source differs from accepted C2.'

$tests = [Collections.Generic.List[object]]::new()
$targetedNames = @('CompletedWorkerLifetimesReleaseSequenceOwnership','OverlappedWorkerLifetimesReleaseSequenceOwnership','FocusWorkerRetentionCounterScenario') | Sort-Object
foreach ($configuration in @('Debug','Release')) {
    foreach ($scope in @('targeted','affected')) {
        $trx = Join-Path $validationRoot "$configuration-focus-worker-retention-$scope.trx"
        $log = [IO.Path]::ChangeExtension($trx, '.log')
        $stats = Load-TrxStats $trx
        $expectedLeaves = if ($scope -ceq 'targeted') { 3 } else { 250 }
        Require ($stats.Leaves -eq $expectedLeaves -and $stats.Passed -eq $expectedLeaves -and $stats.Failed -eq 0) "Test count differs: $configuration/$scope"
        if ($scope -ceq 'targeted') { Require (($stats.Names -join '|') -ceq ($targetedNames -join '|')) "Targeted composition differs: $configuration" }
        $tests.Add([pscustomobject]@{Configuration=$configuration;Scope=$scope;Raw=$stats.Raw;Leaves=$stats.Leaves;Passed=$stats.Passed;Failed=$stats.Failed;TrxSHA256=$stats.SHA256;LogSHA256=Hash $log})
    }
}
$strictPath = Join-Path $validationRoot 'focus-worker-retention-strict-verification.json'
$strict = Load-Json $strictPath
Require ($strict.Accepted -is [bool] -and $strict.Accepted -and $strict.Verdict -ceq 'ACCEPT' -and $strict.SnapshotId -ceq $SnapshotId -and $strict.ExperimentId -ceq $ExperimentId -and $strict.MeasurementRows -eq 380 -and $strict.Processes -eq 5 -and $strict.CompletedWorkerLifetimes -eq 640 -and $strict.OverlappedWorkerLifetimes -eq 640 -and !$strict.ABPerformanceClaim) 'Strict report differs.'
$measurementsPath = Join-Path $experimentRoot 'measurements.csv'
Require ((Hash $measurementsPath) -ceq $strict.MeasurementsSHA256) 'Measurements changed after strict verification.'
$appendPath = Join-Path $experimentRoot 'ledger-append.json'
$append = Load-Json $appendPath
Require ($append.Verdict -ceq 'ACCEPT' -and $append.OldLength -eq 40597252 -and $append.OldSHA256 -ceq '4EB8974ADA760ADB195853E1C2CABBE4C9F687BF13681D88BD226E3F890F837A' -and $append.OldRows -eq 39820 -and $append.AppendedRows -eq 380 -and $append.MeasurementsSHA256 -ceq $strict.MeasurementsSHA256) 'Ledger append proof differs.'
$ledgerPath = Join-Path $repositoryRoot 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv'
Require ((Get-Item $ledgerPath).Length -eq $append.NewLength -and (Hash $ledgerPath) -ceq $append.NewSHA256) 'Current ledger differs from append proof.'
$summaryPath = Join-Path $validationRoot 'validation-summary.json'
Require (!(Test-Path $summaryPath)) 'Immutable delivery summary already exists.'
$prepared = Load-Json (Join-Path $snapshotRoot 'release-test-binaries.provenance.json')
$summary = [ordered]@{
    Verdict='ACCEPT';ReportVersion=1;SnapshotId=$SnapshotId;PerfId='PERF-030';BoundedStage='Managed thread and aggregate process-handle retention across completed and overlapped RequestSequence worker lifetimes';
    Commit=$head;Tree=$tree;SnapshotManifestSHA256=Hash $manifestPath;SnapshotJSONSHA256=Hash $snapshotPath;SnapshotSourceFiles=$manifest.Count;
    ProductionDelta=$false;ProductionSourceSHA256=$prepared.ProductionSourceSHA256;PreviousAcceptedC2='FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C2';PreviousAcceptedC2SummarySHA256=Hash $previousSummaryPath;
    Measurement=[ordered]@{ExperimentId=$ExperimentId;Rows=380;Processes=5;MeasurementsSHA256=Hash $measurementsPath;StrictReportSHA256=Hash $strictPath;EqualSizeWarmup=$true;WarmupCompletedWorkerLifetimes=640;WarmupOverlappedWorkerLifetimes=320;CompletedWorkerLifetimes=640;OverlappedWorkerLifetimes=640;OverlappedAdmissions=10240;ProcessThreadResult=$strict.ProcessThreadResult;CompletedHandleDeltaMin=$strict.CompletedHandleDeltaMin;CompletedHandleDeltaMax=$strict.CompletedHandleDeltaMax;FirstOverlapHandleDeltaMin=$strict.FirstOverlapHandleDeltaMin;FirstOverlapHandleDeltaMax=$strict.FirstOverlapHandleDeltaMax;RepeatOverlapHandleDeltaMin=$strict.RepeatOverlapHandleDeltaMin;RepeatOverlapHandleDeltaMax=$strict.RepeatOverlapHandleDeltaMax;WorkerReferencesAlive=0;ABPerformanceClaim=$false};
    Tests=@($tests);OptimizationLedger=[ordered]@{Rows=$append.NewRows;Bytes=$append.NewLength;SHA256=$append.NewSHA256;PreviousRows=$append.OldRows;PreviousBytes=$append.OldLength;PreviousSHA256=$append.OldSHA256;AppendedRows=$append.AppendedRows;AppendedBytes=$append.SuffixLength;AppendedSHA256=$append.SuffixSHA256;AppendProofSHA256=Hash $appendPath};
    FunctionalDocuments=[ordered]@{TODO='C5AE4822AC1B62A92E66FE4C282035CD441F096CD93358DCE2E79119972C61D1';IMPLEMENTATION_STATUS='580E05F35A0D6EEE150B175E7886FC87F2B2FAB76025B498BC656A7E9830335D'};
    NativePreflight=[ordered]@{SnapshotId='FWM-ANIMATION-NATIVE-BASELINE-20260908-N1';Verdict='NATIVE_BLOCKED';CPU='NOT_MEASURED';GPU='NOT_MEASURED';PresentationLatency='NOT_MEASURED'};
    Limitations='Aggregate testhost HandleCount does not attribute native handle types. Native UIA timeout, late focus, production scheduling, CPU/GPU, COM/WPF/DPI and presentation latency remain E2E_PENDING / NOT_MEASURED.';
    SummaryWriterSHA256=Hash $PSCommandPath;CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')
}
$summary | ConvertTo-Json -Depth 9 | Set-Content $summaryPath
$summary | ConvertTo-Json -Depth 9
