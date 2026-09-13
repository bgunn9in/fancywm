param(
    [Parameter(Mandatory)][string]$SnapshotId,
    [string]$ExperimentId = 'EXP-CONTINUE-013',
    [ValidateSet('Mica', 'CoordinatorEmpty', 'CoordinatorNonempty', 'CoordinatorDeadlines', 'SettingsDistinct', 'SettingsDispose', 'StartupWindowClose', 'SettingsWindowClose', 'OverlayHostClose', 'OverlayWindowClose', 'OverlayWindowCallbacks', 'TilingOverlayRendererDispose', 'TilingOverlayRendererResources', 'TilingOverlayRendererRemoval', 'TilingOverlayRendererCreation', 'TilingOverlayRendererRecovery', 'TilingWindowLifetime', 'SettingsRuntime', 'ArrangeFailureBookkeeping', 'ThemeReload', 'SequenceHash', 'OverlayUpdates', 'OverlayAnimation', 'ResizeTraversal', 'DiagnosticReuse', 'TransitionRounded', 'TransitionCompletion', 'RuntimeStateCount', 'DisabledLogging', 'OwnershipTimer', 'IconDiscovery', 'FocusFallback', 'FocusSupersession', 'FocusBounded', 'ClockBoost', 'AnimationShutdown', 'DependentShutdown', 'AppAsyncShutdown', 'PreviewCache', 'PostMoveTimer', 'WorkspaceInvariant', 'TransitionDispatch', 'ReleaseChecker', 'TreeStateLookup', 'WindowStateLookup', 'DisplayManageability', 'DiscoveryWindowSnapshot', 'RefreshWindowSnapshot','LayoutCallbackCache','ContainsWindow','FocusRejectedAdmission', 'PanelIndex', 'EmbedIndex', 'SplitPanelMeasure', 'StackPanelMeasure', 'PlaceholderCleanup', 'PreviewCacheManaged', 'ThemeLocalImage', 'AnimationFrame', 'HotkeyCallback', 'GenericPreview', 'SettingsStrings', 'OverlayMaterialization', 'CursorInput', 'ActionBarMaterialization', 'HookReplacement', 'DisplayRemovalGc', 'SvgMaterialization', 'KeyPressDisplay', 'BindingListener', 'MainWindowLifetime', 'ConstructionLifetime', 'AppShutdown', 'LayoutCadence', 'WindowDraggerLifetime', 'ModifierDragLifetime', 'HookLifetime', 'HookDisposal', 'LowLevelPatternLifetime', 'DelayedCommandLifetime', 'VirtualDesktopCallbackLifetime', 'VirtualDesktopRemovalLifetime', 'TilingNotificationLifetime', 'DirectHotkeyLifetime', 'MicaCallbackLifetime', 'LoadedUpdateCheckLifetime')][string]$Scenario = 'Mica',
    [string]$BaselineSnapshotId,
    [ValidateSet('flat-10', 'flat-25', 'flat-50', 'balanced-10', 'balanced-25', 'balanced-50', 'skewed-10', 'skewed-25', 'skewed-50')][string]$GenericPreviewCase
)
$ErrorActionPreference = 'Stop'
$normalizedGenericPreviewCase = if ([string]::IsNullOrWhiteSpace($GenericPreviewCase)) { $null } else { $GenericPreviewCase.ToLowerInvariant() }
if ($normalizedGenericPreviewCase -and $Scenario -ne 'GenericPreview') { throw '-GenericPreviewCase requires -Scenario GenericPreview' }
$repositoryRoot = (git rev-parse --show-toplevel).Trim()
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$experimentRoot = Join-Path $snapshotRoot $ExperimentId
if (Test-Path -LiteralPath $experimentRoot) { throw "Experiment already exists: $experimentRoot" }
$candidateManifest = @(Import-Csv (Join-Path $snapshotRoot 'manifest.csv'))
if ($Scenario -eq 'TilingWindowLifetime') {
    if ([string]::IsNullOrWhiteSpace($BaselineSnapshotId) -or $BaselineSnapshotId -ceq $SnapshotId) {
        throw 'TilingWindowLifetime requires distinct source snapshots and archived Release baseline binaries'
    }
    $tilingWindowLifetimeSources = @(
        'FancyWM/ViewModels/TilingNodeViewModel.cs',
        'FancyWM/ViewModels/TilingWindowViewModel.cs',
        'FancyWM/ViewModels/ViewModelBase.cs',
        'FancyWM/Utilities/WindowExtensions.cs',
        'FancyWM.Layouts/Tiling/WindowNode.cs',
        'FancyWM/AssemblyInfo.cs',
        'FancyWM/FancyWM.csproj',
        'FancyWM.Tests/Models/TilingWindowLifetimeTest.cs',
        'FancyWM.Tests/FancyWM.Tests.csproj',
        'Directory.Build.props',
        'version.json',
        'scripts/performance/Measure-MicaRefresh.ps1'
    )
    $tilingWindowLifetimeBaselineSources = @{
        'FancyWM/ViewModels/TilingNodeViewModel.cs' = '346BBAEF4E9AFDD4A990CB362B61FF94ED8766FAC2E747BCE24C14BDBA2DA405'
        'FancyWM/ViewModels/TilingWindowViewModel.cs' = 'E18A743999A93DC856456106E06AB8E2D396EF7F56D8C06153933FD99E7771EE'
    }
    foreach ($source in $tilingWindowLifetimeSources) {
        $records = @($candidateManifest | Where-Object { $_.Path -eq $source })
        if ($records.Count -ne 1 -or $records[0].Path -cne $source -or $records[0].SHA256 -cnotmatch '^[0-9A-F]{64}$') {
            throw "Candidate TilingWindowLifetime input must appear exactly once: $source"
        }
    }
    $tilingWindowLifetimeCases = @('normal','removal-failure','replacement-failure','removed-reentry')
    $tilingWindowLifetimeUnits = [ordered]@{
        'cycles' = 'independent VM lifetimes/case/process'
        'primary-outcomes' = 'caught original controlled exceptions/case/process'
        'correct-operation-outcomes' = 'correct requested binding or cleanup outcomes/case/process'
        'owned-adds' = 'physically committed owned event additions/case/process'
        'owned-remove-attempts' = 'owned event removal attempts including absent handlers/case/process'
        'cursor-subscriptions-after-operation' = 'sum of live cursor subscriptions at100 operation checkpoints/case/process'
        'window-subscriptions-after-operation' = 'sum of live window subscriptions at100 operation checkpoints/case/process'
        'binding-references-after-operation' = 'sum of public Node,cached node and workspace references at100 checkpoints/case/process'
        'cursor-subscriptions-after-dispose' = 'sum of live cursor subscriptions after repeated production Dispose/case/process'
        'window-subscriptions-after-dispose' = 'sum of live window subscriptions after repeated production Dispose/case/process'
        'models-needing-fixture-repair' = 'models retaining physical subscriptions before explicit fixture repair/case/process'
        'fixture-released-subscriptions' = 'physical subscriptions removed only by test registry repair/case/process'
        'subscriptions-after-fixture-repair' = 'physical owned subscriptions after test registry repair/case/process'
    }
    $tilingWindowLifetimeInstrument = 'Actual production TilingNodeViewModel.Node and TilingWindowViewModel binding,Removed and Dispose paths;100 independent managed VM lifetimes in each of four cases:normal same-workspace replacement,position-start remove-after-effect Dispose failure,position-start add-after-effect replacement failure,and synchronous Removed from the newly acquired window Removed accessor;real delegate registries distinguish both Removed owners,window/workspace source identity and borrowed subscribers;exact primary identity and normal requested Node/title are checked;known incorrect R0 outcomes are counted separately from correct outcomes;ownership checkpoints precede explicit repeated production Dispose and separately counted fixture registry repair;five alternating baseline/candidate pairs share the final fixture and dependencies with only separately archived hashed FancyWM.dll differing;counts only,no native UI,HWND,window movement,icon resolution,sleeps,timing,allocations,retained heap,process CPU/GPU or energy measurement'
    $tilingWindowLifetimeExpected = @{
        'baseline/normal' = @(100,0,100,1100,1100,100,500,300,0,0,0,0,0)
        'candidate/normal' = @(100,0,100,1100,1100,100,500,300,0,0,0,0,0)
        'baseline/removal-failure' = @(100,100,0,600,300,100,200,200,100,200,100,300,0)
        'candidate/removal-failure' = @(100,100,100,600,600,0,0,0,0,0,0,0,0)
        'baseline/replacement-failure' = @(100,100,0,1000,1200,100,300,300,0,0,0,0,0)
        'candidate/replacement-failure' = @(100,100,100,1000,1000,0,0,0,0,0,0,0,0)
        'baseline/removed-reentry' = @(100,0,0,1200,1600,100,400,300,0,0,0,0,0)
        'candidate/removed-reentry' = @(100,0,100,900,1000,0,100,100,0,0,0,0,0)
    }
    function Get-TilingWindowLifetimeExpectedCounter([string]$Variant, [string]$Case, [string]$Metric) {
        $metricIndex = [Array]::IndexOf([string[]]@($tilingWindowLifetimeUnits.Keys),$Metric)
        if ($Variant -cnotin @('baseline','candidate') -or $Case -cnotin $tilingWindowLifetimeCases -or $metricIndex -lt 0) {
            throw "Unknown TilingWindowLifetime counter: $Variant/$Case/$Metric"
        }
        return $tilingWindowLifetimeExpected["$Variant/$Case"][$metricIndex]
    }
    # Reuse the existing strict single-test process evidence collector.
    $overlayRecoveryProcesses = [System.Collections.Generic.List[object]]::new()
    $overlayRecoveryRunIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $overlayRecoveryExecutionIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
}
if ($Scenario -eq 'TilingOverlayRendererRecovery') {
    if ([string]::IsNullOrWhiteSpace($BaselineSnapshotId) -or $BaselineSnapshotId -ceq $SnapshotId) {
        throw 'TilingOverlayRenderer recovery requires distinct baseline/candidate snapshots and archived Release binaries'
    }
    $overlayRecoveryCases = @('normal','removal-failure')
    $overlayRecoveryUnits = [ordered]@{
        'cycles' = 'recovery cycles/case/process'
        'primary-outcomes' = 'caught original Remove notification exceptions/case/process'
        'correct-requested-snapshot-cycles' = 'cycles with exact requested node identities, order, focus and preview/case/process'
        'cursor-subscriptions-after-retry' = 'sum of live cursor subscriptions at100 retry checkpoints/case/process'
        'window-subscriptions-after-retry' = 'sum of window event subscription balances at100 retry checkpoints/case/process'
        'stale-node-references-after-retry' = 'sum of removed-window Node references at100 retry checkpoints/case/process'
        'stale-window-subscriptions-after-retry' = 'sum of removed-window event subscription balances at100 retry checkpoints/case/process'
        'cursor-subscriptions-after-cleanup' = 'sum of cursor subscription balances after100 explicit cleanup checkpoints/case/process'
        'window-subscriptions-after-cleanup' = 'sum of window event subscription balances after100 explicit cleanup checkpoints/case/process'
    }
    $overlayRecoveryInstrument = 'Actual production TilingOverlayRenderer.UpdateOverlay on the existing managed field-initialized renderer and fake workspace/window adapters;100 normal and100 first-Remove-notification-failure cycles on one renderer per case;four initial windows,two requested survivors,the same requested snapshot object retried without InvalidateView,then one identical stable update;exact original exception identity,actual dictionary/collection Node identities and order,requested focus and retained preview checked without reassigning the preview set;known incorrect R0 state is counted explicitly and never treated as equivalent;live cursor and five-per-window event balances plus removed-window Node references are sampled before explicit repeated invalidation after each cycle;values are sums of100 checkpoint observations,not100 simultaneously retained owners;five alternating baseline/candidate pairs share the same current fixture and dependencies with only separately archived hashed FancyWM.dll differing;counts only,no public renderer or OverlayHost constructor,App,Window,HWND,native operations,timing,allocation,retained heap,process CPU/GPU or energy measurement;panel,add,reentry,Dispose and late callback failures remain separate strict regressions'
    $overlayRecoveryProcesses = [System.Collections.Generic.List[object]]::new()
    $overlayRecoveryRunIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $overlayRecoveryExecutionIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    function Get-OverlayRecoveryExpectedCounter([string]$Variant, [string]$Case, [string]$Metric) {
        if ($Variant -cnotin @('baseline','candidate') -or $Case -cnotin @('normal','removal-failure')) {
            throw "Unknown recovery counter dimensions: $Variant/$Case"
        }
        $staleOwner = $Variant -ceq 'baseline' -and $Case -ceq 'removal-failure'
        switch -CaseSensitive ($Metric) {
            'cycles' { return 100 }
            'primary-outcomes' { if ($Case -ceq 'normal') { return 0 }; return 100 }
            'correct-requested-snapshot-cycles' { if ($staleOwner) { return 0 }; return 100 }
            'cursor-subscriptions-after-retry' { if ($staleOwner) { return 300 }; return 200 }
            'window-subscriptions-after-retry' { if ($staleOwner) { return 1500 }; return 1000 }
            'stale-node-references-after-retry' { if ($staleOwner) { return 100 }; return 0 }
            'stale-window-subscriptions-after-retry' { if ($staleOwner) { return 500 }; return 0 }
            'cursor-subscriptions-after-cleanup' { return 0 }
            'window-subscriptions-after-cleanup' { return 0 }
            default { throw "Unknown recovery counter metric: $Metric" }
        }
    }
}
if ($Scenario -eq 'FocusRejectedAdmission') {
    if ([string]::IsNullOrWhiteSpace($BaselineSnapshotId) -or $BaselineSnapshotId -ceq $SnapshotId) {
        throw 'FocusRejectedAdmission requires distinct source snapshots and archived Release baseline binaries'
    }
    $focusRejectedSources = @(
        'FancyWM/Utilities/FocusHelper.cs',
        'FancyWM.Tests/Utilities/FocusHelperTest.RejectedAdmission.cs',
        'FancyWM/FancyWM.csproj',
        'FancyWM.Tests/FancyWM.Tests.csproj',
        'Directory.Build.props',
        'version.json',
        'scripts/performance/Measure-MicaRefresh.ps1',
        'scripts/performance/Archive-FocusRejectedBaseline.ps1',
        'scripts/performance/Verify-FocusRejectedAdmission.ps1',
        'scripts/performance/Append-FocusRejectedMeasurements.ps1'
    )
    foreach ($source in $focusRejectedSources) {
        $records = @($candidateManifest | Where-Object { $_.Path -eq $source })
        if ($records.Count -ne 1 -or $records[0].Path -cne $source -or $records[0].SHA256 -cnotmatch '^[0-9A-F]{64}$') {
            throw "Candidate FocusRejectedAdmission input must appear exactly once: $source"
        }
    }
    $focusRejectedCases = @('superseded','expired','stopped')
    $focusRejectedUnits = [ordered]@{
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
    $focusRejectedInstrument = 'Actual production FocusHelper.RequestSequence.Enqueue with pre-created superseded,expired or stopped tokens and a counted fake native adapter;1000 discarded warmups then100000 calls per mode;GC.GetAllocatedBytesForCurrentThread and Stopwatch with per-process timestamp frequency;exact false completion,rejection,current-token preservation and zero native,start or wait callbacks checked;common final fixture and dependencies with only separately archived hashed baseline FancyWM.dll differing;no admitted worker,native focus,input,window,COM,WPF,DPI,whole-process CPU/GPU or native latency measurement;timing is descriptive without a required timing win'
    $overlayRecoveryProcesses = [System.Collections.Generic.List[object]]::new()
    $overlayRecoveryRunIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $overlayRecoveryExecutionIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
}
if ($Scenario -eq 'ContainsWindow') {
    if ([string]::IsNullOrWhiteSpace($BaselineSnapshotId) -or $BaselineSnapshotId -ceq $SnapshotId) {
        throw 'ContainsWindow requires distinct source snapshots and archived Release baseline binaries'
    }
    $containsWindowSources = @(
        'FancyWM/AlgorithmicLayouts/MasterSatelliteLayoutEngine.cs',
        'FancyWM/AlgorithmicLayouts/MasterSatelliteRuntimeState.cs',
        'FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteContainsWindowTest.cs',
        'FancyWM/FancyWM.csproj',
        'FancyWM.Tests/FancyWM.Tests.csproj',
        'Directory.Build.props',
        'version.json',
        'scripts/performance/Measure-MicaRefresh.ps1',
        'scripts/performance/Archive-ContainsWindowBaseline.ps1',
        'scripts/performance/Verify-ContainsWindowScan.ps1',
        'scripts/performance/Append-ContainsWindowMeasurements.ps1'
    )
    foreach ($source in $containsWindowSources) {
        $records = @($candidateManifest | Where-Object { $_.Path -eq $source })
        if ($records.Count -ne 1 -or $records[0].Path -cne $source -or $records[0].SHA256 -cnotmatch '^[0-9A-F]{64}$') {
            throw "Candidate ContainsWindow input must appear exactly once: $source"
        }
    }
    $containsWindowUnits = [ordered]@{
        'allocated-bytes' = 'bytes/100000 calls'
        'elapsed-ticks' = 'ticks/100000 calls'
        'timestamp-frequency' = 'ticks/second'
        'calls' = 'calls/scenario'
        'matches' = 'matches/scenario'
        'equality-calls' = 'equality-calls/scenario'
        'hash-calls' = 'hash-calls/scenario'
    }
    $containsWindowInstrument = 'Actual production MasterSatelliteLayoutEngine.ContainsWindow through a bound private-method delegate;field-backed IWindow adapters and owned runtime state;master/first/last/absent queries with1/4/9 satellites;1000 discarded warmups then100000 calls;GC.GetAllocatedBytesForCurrentThread and Stopwatch with per-process timestamp frequency;exact matches,ordered equality calls and zero hash calls checked;common final fixture and dependencies with only separately archived hashed baseline FancyWM.dll differing;no transaction or clone reuse,native window/COM/WPF/DPI calls,whole-process CPU/GPU,frame cadence or presentation latency measurement;timing is descriptive without a required timing win'
    $overlayRecoveryProcesses = [System.Collections.Generic.List[object]]::new()
    $overlayRecoveryRunIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $overlayRecoveryExecutionIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
}
if ($Scenario -eq 'LayoutCallbackCache') {
    if ([string]::IsNullOrWhiteSpace($BaselineSnapshotId) -or $BaselineSnapshotId -ceq $SnapshotId) {
        throw 'LayoutCallbackCache requires distinct source snapshots and archived Release baseline binaries'
    }
    $layoutCallbackSources = @(
        'FancyWM/Utilities/LayoutInvalidationQueue.cs',
        'FancyWM.Tests/Utilities/LayoutInvalidationQueueTest.cs',
        'FancyWM.Tests/Utilities/LayoutInvalidationQueueTest.CallbackCache.cs',
        'FancyWM.Tests/Utilities/LayoutInvalidationQueueTest.Shutdown.cs',
        'FancyWM/FancyWM.csproj',
        'FancyWM.Tests/FancyWM.Tests.csproj',
        'Directory.Build.props',
        'version.json',
        'scripts/performance/Measure-MicaRefresh.ps1',
        'scripts/performance/Archive-LayoutCallbackBaseline.ps1',
        'scripts/performance/Verify-LayoutCallbackCache.ps1',
        'scripts/performance/Append-LayoutCallbackMeasurements.ps1'
    )
    foreach ($source in $layoutCallbackSources) {
        $records = @($candidateManifest | Where-Object { $_.Path -eq $source })
        if ($records.Count -ne 1 -or $records[0].Path -cne $source -or $records[0].SHA256 -cnotmatch '^[0-9A-F]{64}$') {
            throw "Candidate LayoutCallbackCache input must appear exactly once: $source"
        }
    }
    $layoutCallbackUnits = [ordered]@{
        'allocated-bytes' = 'bytes/10000 cycles'
        'elapsed-ticks' = 'ticks/10000 cycles'
        'timestamp-frequency' = 'ticks/second'
        'invalidations' = 'invalidations/scenario'
        'posts' = 'scheduled-callbacks/scenario'
        'applies' = 'apply-calls/scenario'
        'eligibility-reads' = 'eligibility-reads/scenario'
        'errors' = 'reported-errors/scenario'
        'callback-instance-changes' = 'delegate-identity-changes/scenario'
        'cycles' = 'cycles/scenario'
    }
    $layoutCallbackInstrument = 'Actual production LayoutInvalidationQueue.Invalidate and Execute;one managed queue with a deterministic single-slot scheduler per case;bursts of1/10/50 invalidations followed by a complete synchronous drain;1000 discarded warmups then10000 cycles;GC.GetAllocatedBytesForCurrentThread and Stopwatch with per-process timestamp frequency;exact invalidations,posts,applies,eligibility reads,errors and callback identity changes counted;common final fixture and dependencies with only separately archived hashed baseline FancyWM.dll differing;no real Dispatcher,HWND,native calls,frame cadence,presentation latency,whole-process CPU/GPU or energy measurement;timing is descriptive without a required timing win'
    $overlayRecoveryProcesses = [System.Collections.Generic.List[object]]::new()
    $overlayRecoveryRunIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $overlayRecoveryExecutionIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
}
if ($Scenario -eq 'RefreshWindowSnapshot') {
    if ([string]::IsNullOrWhiteSpace($BaselineSnapshotId) -or $BaselineSnapshotId -ceq $SnapshotId) {
        throw 'RefreshWindowSnapshot requires distinct source snapshots and archived Release baseline binaries'
    }
    $refreshWindowSnapshotSources = @(
        'FancyWM/TilingService.cs',
        'FancyWM/TilingService.Private.cs',
        'FancyWM/TilingWorkspace.cs',
        'FancyWM.Tests/AlgorithmicLayouts/TilingServiceAlgorithmicIntegrationTest.RefreshSnapshot.cs',
        'FancyWM.Tests/AlgorithmicLayouts/TilingServiceAlgorithmicIntegrationTest.Manageability.cs',
        'FancyWM/FancyWM.csproj',
        'FancyWM.Tests/FancyWM.Tests.csproj',
        'Directory.Build.props',
        'version.json',
        'scripts/performance/Measure-MicaRefresh.ps1'
    )
    foreach ($source in $refreshWindowSnapshotSources) {
        $records = @($candidateManifest | Where-Object { $_.Path -eq $source })
        if ($records.Count -ne 1 -or $records[0].Path -cne $source -or $records[0].SHA256 -cnotmatch '^[0-9A-F]{64}$') {
            throw "Candidate RefreshWindowSnapshot input must appear exactly once: $source"
        }
    }
    $refreshWindowSnapshotUnits = [ordered]@{
        'allocated-bytes' = 'bytes/100000 passes'
        'elapsed-ticks' = 'ticks/100000 passes'
        'timestamp-frequency' = 'ticks/second'
        'passes' = 'passes/scenario'
        'state-reads' = 'state-reads/scenario'
        'position-reads' = 'position-reads/scenario'
        'resize-reads' = 'resize-reads/scenario'
        'move-reads' = 'move-reads/scenario'
        'pin-reads' = 'desktop-pin-reads/scenario'
        'desktop-snapshot-reads' = 'desktop-snapshot-reads/scenario'
        'display-snapshot-reads' = 'display-snapshot-reads/scenario'
        'tracked-windows' = 'tracked-windows/scenario'
    }
    $refreshWindowSnapshotInstrument = 'Actual public TilingService.Refresh with its owned HashSet<IWindow> pass snapshot;field-backed IWorkspace/IWindow/IDisplay/IVirtualDesktop adapters and real Information-level Serilog logger;0/1/10/50 unique tracked restored windows lie inside the managed display and are rejected at CanResize;1000 discarded warmups then100000 complete unchanged refresh passes;all measured property/provider reads and tracked-window input counts are checked;GC.GetAllocatedBytesForCurrentThread and Stopwatch with per-process timestamp frequency;common final fixture and dependencies with only separately archived hashed baseline FancyWM.dll differing;snapshot isolation and complete membership reconciliation remain production behavior;no native window/COM calls,provider discovery,wall-clock polling latency,whole-process CPU/GPU/energy or idle-wakeup measurement;timing is recorded without a required timing win'
    # Reuse the strict single-test process evidence collector and binary-tree checks.
    $overlayRecoveryProcesses = [System.Collections.Generic.List[object]]::new()
    $overlayRecoveryRunIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $overlayRecoveryExecutionIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
}
if ($Scenario -eq 'TransitionCompletion') {
    if ([string]::IsNullOrWhiteSpace($BaselineSnapshotId) -or $BaselineSnapshotId -ceq $SnapshotId) {
        throw 'TransitionCompletion requires distinct source snapshots and archived Release baseline binaries'
    }
    $transitionCompletionModes = @('success','cancelled','mixed')
    $transitionCompletionCounts = @(0,1,4,10,25,50)
    $transitionCompletionUnits = [ordered]@{
        'allocated-bytes' = 'bytes/10000 completions'
        'elapsed-ticks' = 'ticks/10000 completions'
        'timestamp-frequency' = 'ticks/second'
        'completions' = 'completions/scenario'
        'input-tasks' = 'input-tasks/scenario'
        'cancelled-inputs' = 'cancelled-inputs/scenario'
    }
    $transitionCompletionInstrument = 'Actual production Tasks.WhenAllIgnoreCancelled called by TransitionTargetGroup.PerformSmoothTransitionAsync;owned List<Task> inputs containing0/1/4/10/25/50 already completed tasks in success,all-cancelled and alternating-cancelled cases;1000 discarded warmups then10000 awaited completions;GC.GetAllocatedBytesForCurrentThread and Stopwatch with per-process timestamp frequency;input count,completed state and cancellation count checked outside samples;five alternating baseline/candidate pairs share the final fixture and dependencies with only separately archived hashed FancyWM.dll differing;measures completion aggregation only,no native windows,animation cadence,dispatcher latency,whole-process CPU/GPU or energy claim;pending peers,faults and cancellation semantics remain separate strict regressions;timing is recorded without a required timing win'
    # Reuse the strict single-test process evidence collector and binary-tree checks.
    $overlayRecoveryProcesses = [System.Collections.Generic.List[object]]::new()
    $overlayRecoveryRunIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $overlayRecoveryExecutionIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
}
if ($Scenario -eq 'RuntimeStateCount') {
    if ([string]::IsNullOrWhiteSpace($BaselineSnapshotId) -or $BaselineSnapshotId -ceq $SnapshotId) {
        throw 'RuntimeStateCount requires distinct source snapshots and archived Release baseline binaries'
    }
    $runtimeStateCountModes = @('own','mixed')
    $runtimeStateCountCounts = @(0,1,4,10,50)
    $runtimeStateCountChangedSources = @(
        'FancyWM/AlgorithmicLayouts/MasterSatelliteRuntimeRegistry.cs',
        'FancyWM/AlgorithmicLayouts/AlgorithmicLayoutCoordinator.cs',
        'FancyWM/TilingService.MasterSatellite.cs'
    )
    $runtimeStateCountSources = $runtimeStateCountChangedSources + @(
        'FancyWM.Tests/AlgorithmicLayouts/TilingServiceAlgorithmicIntegrationTest.RuntimeStateCount.cs',
        'FancyWM.Tests/AlgorithmicLayouts/TilingServiceAlgorithmicIntegrationTest.Manageability.cs',
        'FancyWM/FancyWM.csproj',
        'FancyWM.Tests/FancyWM.Tests.csproj',
        'Directory.Build.props',
        'version.json',
        'scripts/performance/Measure-MicaRefresh.ps1'
    )
    foreach ($source in $runtimeStateCountSources) {
        $records = @($candidateManifest | Where-Object { $_.Path -eq $source })
        if ($records.Count -ne 1 -or $records[0].Path -cne $source -or $records[0].SHA256 -cnotmatch '^[0-9A-F]{64}$') {
            throw "Candidate RuntimeStateCount input must appear exactly once: $source"
        }
    }
    $runtimeStateCountUnits = [ordered]@{
        'allocated-bytes' = 'bytes/10000 queries'
        'elapsed-ticks' = 'ticks/10000 queries'
        'timestamp-frequency' = 'ticks/second'
        'queries' = 'queries/scenario'
        'count-sum' = 'sum of returned display state counts/scenario'
        'equality-reads' = 'display equality calls/scenario'
    }
    $runtimeStateCountInstrument = 'Actual production MasterSatelliteRuntimeSession.StateCount through AlgorithmicLayoutCoordinator and its MasterSatelliteRuntimeRegistry;0/1/4/10/50 runtime entries with unique desktops,all referencing the queried display in own cases or alternating distinct matching aliases and foreign displays in mixed cases;window_count denotes registry entries,not windows;1000 discarded common warmups then10000 queries;GC.GetAllocatedBytesForCurrentThread and Stopwatch with per-process timestamp frequency;exact returned count sum and complete display equality traffic asserted outside samples;five alternating baseline/candidate pairs share the final fixture and dependencies with only separately archived hashed FancyWM.dll differing;no native discovery,COM,focus,window movement,whole-process CPU/GPU or energy measurement;timing is recorded without a required timing win'
    # Reuse the strict single-test process evidence collector and binary-tree checks.
    $overlayRecoveryProcesses = [System.Collections.Generic.List[object]]::new()
    $overlayRecoveryRunIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $overlayRecoveryExecutionIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
}
$arrangeFailureBookkeepingSources = @(
    'FancyWM/TilingService.Private.cs',
    'FancyWM/TilingService.cs',
    'FancyWM/TilingService.MasterSatellite.cs',
    'FancyWM/TilingService.MasterSatellite.Commands.cs',
    'FancyWM/TilingWorkspace.cs',
    'FancyWM/TilingWorkspace.Algorithmic.cs',
    'FancyWM/ITilingService.cs',
    'FancyWM/AlgorithmicLayouts/MasterSatelliteArrangeFailure.cs',
    'FancyWM/AlgorithmicLayouts/MasterSatelliteLayoutEngine.cs',
    'FancyWM/AlgorithmicLayouts/MasterSatelliteResults.cs',
    'FancyWM/AlgorithmicLayouts/MasterSatelliteRuntimeState.cs',
    'FancyWM/Models/Settings.cs',
    'FancyWM/Models/MasterSatelliteLayoutSettings.cs',
    'FancyWM/Utilities/DebugLock.cs',
    'FancyWM/AssemblyInfo.cs',
    'FancyWM/FancyWM.csproj',
    'FancyWM.Layouts/FancyWM.Layouts.csproj',
    'FancyWM.Layouts/Flex.cs',
    'FancyWM.Layouts/Tiling/DesktopTree.cs',
    'FancyWM.Layouts/Tiling/TilingNode.cs',
    'FancyWM.Layouts/Tiling/PanelNode.cs',
    'FancyWM.Layouts/Tiling/SplitPanelNode.cs',
    'FancyWM.Layouts/Tiling/WindowNode.cs',
    'FancyWM.Tests/AlgorithmicLayouts/TilingServiceAlgorithmicIntegrationTest.cs',
    'FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteArrangeFailureTest.cs',
    'FancyWM.Tests/FancyWM.Tests.csproj',
    'Directory.Build.props',
    'version.json',
    'scripts/performance/Measure-MicaRefresh.ps1'
)
if ($Scenario -eq 'ArrangeFailureBookkeeping') {
    $arrangeFailureBookkeepingCases = @(foreach ($layout in @('ordinary','ms')) {
        foreach ($windowCount in @(1,4,10)) {
            foreach ($state in @('stable','pending')) { "arrange-bookkeeping-$layout-$windowCount-$state" }
        }
    })
    $arrangeFailureBookkeepingUnits = [ordered]@{
        'iterations' = 'UpdateTree calls/scenario'
        'arranged-passes' = 'successful UpdateTree calls/scenario'
        'allocated-bytes' = 'bytes/100 UpdateTree calls'
        'handle-reads' = 'IWindow.Handle reads/100 calls'
        'minsize-reads' = 'IWindow.MinSize reads/100 calls'
        'geometry-checks' = 'complete geometry checks/scenario'
        'geometry-checksum' = 'unsigned checksum/100 calls'
        'identity-checks' = 'complete identity checks/scenario'
        'revision-checks' = 'state revision checks/scenario'
        'revision-delta' = 'revision delta/100 calls'
        'fallback-notifications' = 'fallback notifications/scenario'
        'placement-notifications' = 'placement notifications/scenario'
        'algorithmic-notifications' = 'algorithmic notifications/scenario'
        'floating-windows' = 'floating windows after100 calls'
        'pending-notifications' = 'pending entries after100 calls'
        'new-window-count' = 'new-window entries after100 calls'
    }
    $arrangeFailureBookkeepingProcessTimes = [System.Collections.Generic.List[object]]::new()
    $arrangeFailureBookkeepingInstrument = 'Actual private TilingService.UpdateTree bound once as a delegate;existing ServiceFixture with real DesktopTree and fake window/display/workspace adapters;ordinary and Master+Satellites trees at W=1/4/10;empty m_newWindowSet;stable case has no pending notification and pending control retains one existing HWND;256 discarded warmups then100 allocation samples;GC.GetAllocatedBytesForCurrentThread encloses only UpdateTree and fake adapters;exact geometry,node/window/parent identity,state revision,MinSize/Handle reads,floating/fallback/placement/algorithmic notifications,reservations and transfers asserted outside each sample;common final fixture with separately archived mechanical full PERF-006 seam;no native HWND/desktop/COM,timing,process CPU/GPU/energy or application allocation profile claim'
    foreach ($arrangeFailureBookkeepingSource in $arrangeFailureBookkeepingSources) {
        $candidateSourceMatches = @($candidateManifest | Where-Object { $_.Path -eq $arrangeFailureBookkeepingSource })
        if ($candidateSourceMatches.Count -ne 1 -or $candidateSourceMatches[0].Path -cne $arrangeFailureBookkeepingSource -or
            $candidateSourceMatches[0].SHA256 -cnotmatch '^[0-9A-F]{64}$' -or
            (Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $arrangeFailureBookkeepingSource)).Hash -cne $candidateSourceMatches[0].SHA256) {
            throw "Candidate ArrangeFailureBookkeeping source, fixture or build input must appear exactly once in manifest: $arrangeFailureBookkeepingSource"
        }
    }
}
if ($Scenario -in @('OverlayWindowClose','OverlayWindowCallbacks')) {
    $overlayWindowCloseSources = @(
        'FancyWM/Windows/OverlayHost.OverlayWindow.cs',
        'FancyWM/Windows/OverlayHost.cs',
        'FancyWM/Windows/OverlayHost.Cursor.cs',
        'FancyWM/Utilities/OverlayRefreshLoop.cs',
        'FancyWM/Utilities/LayoutInvalidationQueue.cs',
        'FancyWM/Utilities/LowLevelMouseHook.cs',
        'FancyWM/AssemblyInfo.cs',
        'FancyWM/FancyWM.csproj',
        'FancyWM.Tests/Utilities/OverlayWindowCloseTest.cs',
        'FancyWM.Tests/FancyWM.Tests.csproj',
        'Directory.Build.props',
        'version.json'
    )
    if ($Scenario -eq 'OverlayWindowCallbacks') {
        $overlayWindowCloseSources += 'scripts/performance/Measure-MicaRefresh.ps1'
    }
    foreach ($overlayWindowCloseSource in $overlayWindowCloseSources) {
        $overlayWindowSourceMatches = @($candidateManifest | Where-Object { $_.Path -eq $overlayWindowCloseSource })
        if ($overlayWindowSourceMatches.Count -ne 1 -or
            ($Scenario -eq 'OverlayWindowCallbacks' -and $overlayWindowSourceMatches[0].Path -cne $overlayWindowCloseSource)) {
            throw "Candidate $Scenario source, fixture or build input must appear exactly once in manifest: $overlayWindowCloseSource"
        }
    }
}
if ($Scenario -in @('TilingOverlayRendererDispose','TilingOverlayRendererResources','TilingOverlayRendererRemoval','TilingOverlayRendererCreation','TilingOverlayRendererRecovery')) {
    $tilingOverlayRendererDisposeSources = @(
        'FancyWM/TilingOverlayRenderer.cs',
        'FancyWM/Utilities/Collections.cs',
        'FancyWM/Utilities/Draggable.cs',
        'FancyWM/ViewModels/TilingNodeViewModel.cs',
        'FancyWM/ViewModels/TilingOverlayViewModel.cs',
        'FancyWM/ViewModels/TilingPanelViewModel.cs',
        'FancyWM/ViewModels/TilingWindowViewModel.cs',
        'FancyWM/ViewModels/ViewModelBase.cs',
        'FancyWM/Windows/OverlayHost.cs',
        'FancyWM/Windows/OverlayHost.Cursor.cs',
        'FancyWM/Windows/OverlayHost.OverlayWindow.cs',
        'FancyWM/Utilities/OverlayRefreshLoop.cs',
        'FancyWM/Utilities/LayoutInvalidationQueue.cs',
        'FancyWM/Utilities/LowLevelMouseHook.cs',
        'FancyWM/AssemblyInfo.cs',
        'FancyWM/FancyWM.csproj',
        'FancyWM.Tests/Utilities/TilingOverlayRendererDisposeTest.cs',
        'FancyWM.Tests/Utilities/DraggableTest.cs',
        'FancyWM.Tests/Utilities/OverlayUpdateTest.cs',
        'FancyWM.Tests/FancyWM.Tests.csproj',
        'Directory.Build.props',
        'version.json',
        'scripts/performance/Measure-MicaRefresh.ps1'
    )
    if ($Scenario -eq 'TilingOverlayRendererResources') {
        $tilingOverlayRendererDisposeSources += 'FancyWM/Models/Settings.cs'
    }
    foreach ($tilingOverlayRendererDisposeSource in $tilingOverlayRendererDisposeSources) {
        $candidateSourceMatches = @($candidateManifest | Where-Object { $_.Path -eq $tilingOverlayRendererDisposeSource })
        if ($candidateSourceMatches.Count -ne 1 -or $candidateSourceMatches[0].Path -cne $tilingOverlayRendererDisposeSource) {
            throw "Candidate $Scenario source, fixture or build input must appear exactly once in manifest: $tilingOverlayRendererDisposeSource"
        }
    }
}
$candidateBuildRecords = @($candidateManifest | Where-Object {
    $_.Path -match '^(FancyWM/|FancyWM.Layouts/|FancyWM.Layouts.Tests/|FancyWM.ThemeEngine/|FancyWM.ThemeEngine.Tests/|FancyWM.Tests/|scripts/performance/).+\.(cs|csproj|ps1|xaml)$' -or
    ($Scenario -in @('OverlayHostClose','OverlayWindowClose','OverlayWindowCallbacks','TilingOverlayRendererDispose','TilingOverlayRendererResources','TilingOverlayRendererRemoval','TilingOverlayRendererCreation','TilingOverlayRendererRecovery','TilingWindowLifetime','ArrangeFailureBookkeeping','DiscoveryWindowSnapshot','RefreshWindowSnapshot','LayoutCallbackCache','ContainsWindow','FocusRejectedAdmission','TransitionCompletion','RuntimeStateCount') -and $_.Path -in @('Directory.Build.props','version.json')) -or
    ($Scenario -eq 'ArrangeFailureBookkeeping' -and
        ($_.Path -match '\.(cs|csproj|xaml|props|targets|sln|slnx|ps1)$' -or $_.Path -in @('global.json','NuGet.config'))) -or
    ($Scenario -in @('RuntimeStateCount','LayoutCallbackCache','ContainsWindow','FocusRejectedAdmission') -and
        ($_.Path -match '\.(cs|csproj|xaml|props|targets|sln|slnx|ps1)$' -or $_.Path -match '(^|/)(global\.json|NuGet\.config)$'))
})
foreach ($record in $candidateBuildRecords) {
    if ((Get-FileHash -LiteralPath (Join-Path $repositoryRoot $record.Path)).Hash -ne $record.SHA256) {
        throw "Candidate differs from snapshot: $($record.Path)"
    }
}
New-Item -ItemType Directory -Path $experimentRoot | Out-Null
$testProject = if ($Scenario -eq 'ThemeLocalImage') { 'FancyWM.ThemeEngine.Tests' } elseif ($Scenario -in @('PanelIndex','EmbedIndex','SplitPanelMeasure','StackPanelMeasure','PlaceholderCleanup')) { 'FancyWM.Layouts.Tests' } else { 'FancyWM.Tests' }
$testFramework = if ($Scenario -in @('PanelIndex','EmbedIndex','SplitPanelMeasure','StackPanelMeasure','PlaceholderCleanup')) { 'net10.0' } else { 'net10.0-windows10.0.18362.0' }
$productionAssembly = if ($Scenario -eq 'ThemeLocalImage') { 'FancyWM.ThemeEngine.dll' } elseif ($Scenario -in @('PanelIndex','EmbedIndex','SplitPanelMeasure','StackPanelMeasure','PlaceholderCleanup')) { 'FancyWM.Layouts.dll' } else { 'FancyWM.dll' }
$benchmarkAssembly = "$testProject.dll"
if ($Scenario -in @('DiscoveryWindowSnapshot','RefreshWindowSnapshot','LayoutCallbackCache','ContainsWindow','FocusRejectedAdmission','TransitionCompletion','RuntimeStateCount')) {
    dotnet build "$testProject/$testProject.csproj" --configuration Release --no-restore --no-incremental *> "$experimentRoot/build.log"
}
else {
    dotnet build "$testProject/$testProject.csproj" --configuration Release --no-restore *> "$experimentRoot/build.log"
}
if ($LASTEXITCODE) { throw 'Release test harness build failed' }
if ($Scenario -in @('OverlayWindowCallbacks','ArrangeFailureBookkeeping','TilingOverlayRendererRecovery','TilingWindowLifetime','DiscoveryWindowSnapshot','RefreshWindowSnapshot','LayoutCallbackCache','ContainsWindow','FocusRejectedAdmission','TransitionCompletion','RuntimeStateCount')) {
    foreach ($record in $candidateBuildRecords) {
        if ((Get-FileHash -LiteralPath (Join-Path $repositoryRoot $record.Path)).Hash -ne $record.SHA256) {
            throw "Candidate changed during Release build: $($record.Path)"
        }
    }
}
$binaryRoot = Join-Path $experimentRoot 'binaries'
New-Item -ItemType Directory -Path $binaryRoot | Out-Null
Copy-Item -Path "$testProject/bin/Release/$testFramework/*" -Destination $binaryRoot -Recurse
$binaryHash = (Get-FileHash "$binaryRoot/$productionAssembly").Hash
$benchmarkHash = (Get-FileHash "$binaryRoot/$benchmarkAssembly").Hash
$isComparison = ($Scenario -eq 'ActionBarMaterialization' -and $BaselineSnapshotId) -or $Scenario -notin @('Mica', 'ThemeReload', 'IconDiscovery', 'FocusFallback', 'ReleaseChecker', 'ThemeLocalImage', 'OverlayMaterialization', 'ActionBarMaterialization')
if ($isComparison) {
    if (!$BaselineSnapshotId) { throw 'Comparison requires a validated baseline snapshot and archived Release binaries' }
    $baselineRoot = Join-Path $repositoryRoot "artifacts/performance/$BaselineSnapshotId"
    $baselineBinary = Join-Path $baselineRoot "release-test-binaries/$productionAssembly"
    $binaryRecord = Import-Csv "$baselineRoot/release-test-binaries.csv" | Where-Object { (Split-Path $_.Path -Leaf) -eq $productionAssembly }
    if ($binaryRecord -and !$binaryRecord.Hash -and $binaryRecord.SHA256) { $binaryRecord | Add-Member -NotePropertyName Hash -NotePropertyValue $binaryRecord.SHA256 }
    if (!$binaryRecord -or (Get-FileHash -LiteralPath $baselineBinary).Hash -ne $binaryRecord.Hash) { throw 'Baseline assembly checksum mismatch' }
    if ($Scenario -eq 'OverlayWindowCallbacks') {
        if (@($binaryRecord).Count -ne 1 -or (Split-Path $binaryRecord.Path -Leaf) -cne $productionAssembly) {
            throw 'Baseline OverlayWindow callback assembly must appear exactly once in the archive manifest'
        }
        $overlayWindowCallbacksProvenance = Get-Content -LiteralPath "$baselineRoot/release-test-binaries.provenance.json" -Raw | ConvertFrom-Json
        if ($overlayWindowCallbacksProvenance.Snapshot -cne $BaselineSnapshotId -or
            $overlayWindowCallbacksProvenance.BuildSucceeded -ne $true -or
            $overlayWindowCallbacksProvenance.ExpectedRedTestExitCode -ne 1 -or
            $overlayWindowCallbacksProvenance.SnapshotManifestSHA256 -ne (Get-FileHash -LiteralPath "$baselineRoot/manifest.csv").Hash -or
            $overlayWindowCallbacksProvenance.ArchiveManifestSHA256 -ne (Get-FileHash -LiteralPath "$baselineRoot/release-test-binaries.csv").Hash -or
            $overlayWindowCallbacksProvenance.FancyWMDllSHA256 -ne $binaryRecord.Hash -or
            $overlayWindowCallbacksProvenance.BaselineSource -cne 'source/FancyWM/Windows/OverlayHost.OverlayWindow.cs' -or
            $overlayWindowCallbacksProvenance.RegressionSource -cne 'source/FancyWM.Tests/Utilities/OverlayWindowCloseTest.cs') {
            throw 'Baseline OverlayWindow callback build/archive provenance differs from the frozen snapshot'
        }
        if ($overlayWindowCallbacksProvenance.RedTrx -cne 'validation/overlay-window-callbacks-seam-red.trx' -or
            $overlayWindowCallbacksProvenance.RedTrxSHA256 -ne (Get-FileHash -LiteralPath "$baselineRoot/validation/overlay-window-callbacks-seam-red.trx").Hash) {
            throw 'Baseline OverlayWindow callback regression evidence checksum mismatch'
        }
    }
    $sourcePattern = if ($Scenario -eq 'KeyPressDisplay') { '^FancyWM/(Controls/KeyPressBox\.xaml(?:\.cs)?|Utilities/KeyPatternListener\.cs)$' } elseif ($Scenario -eq 'SvgMaterialization') { '^FancyWM/Controls/SvgIcon\.xaml(?:\.cs)?$' } elseif ($Scenario -eq 'ActionBarMaterialization') { '^FancyWM/Controls/(SvgIcon|TilingWindow)\.xaml(?:\.cs)?$' } elseif ($Scenario -eq 'HookReplacement') { '^FancyWM/Utilities/(LowLevelKeyboardHook|LowLevelMouseHook|HookRegistrationPolicy)\.cs$' } elseif ($Scenario -eq 'DisplayRemovalGc') { '^FancyWM/MultiDisplayTilingService\.cs$' } elseif ($Scenario -eq 'CursorInput') { '^FancyWM/Windows/OverlayHost(?:\.Cursor)?\.cs$' } elseif ($Scenario -eq 'SettingsStrings') { '^FancyWM/Controls/StringsListBox\.xaml(?:\.cs)?$' } elseif ($Scenario -eq 'GenericPreview') { '^FancyWM/TilingWorkspace\.cs$' } elseif ($Scenario -eq 'AnimationFrame') { '^FancyWM/Utilities/AnimationThread\.cs$' } elseif ($Scenario -eq 'HotkeyCallback') { '^FancyWM/Utilities/(LowLevelHotkey|LowLevelKeyboardHook)\.cs$' } elseif ($Scenario -eq 'PreviewCacheManaged') { '^FancyWM/AlgorithmicLayouts/MasterSatelliteDropController\.cs$' } elseif ($Scenario -in @('TreeStateLookup','WindowStateLookup')) { '^FancyWM/TilingWorkspace\.cs$' } elseif ($Scenario -eq 'WorkspaceInvariant') { '^FancyWM/(TilingWorkspace\.Algorithmic|AlgorithmicLayouts/MasterSatelliteResults)\.cs$' } elseif ($Scenario -eq 'TransitionDispatch') { '^FancyWM/Utilities/TransitionTargetGroup\.cs$' } elseif ($Scenario -eq 'PostMoveTimer') { '^FancyWM/TilingService\.MasterSatellite\.cs$' } elseif ($Scenario -eq 'PreviewCache') { '^FancyWM/AlgorithmicLayouts/MasterSatelliteDropController\.cs$' } elseif ($Scenario -eq 'OwnershipTimer') { '^FancyWM/TilingService\.MasterSatellite\.cs$' } elseif ($Scenario -eq 'DisabledLogging') { '^FancyWM/TilingService\.Private\.cs$' } elseif ($Scenario -eq 'DiagnosticReuse') { '^FancyWM/AlgorithmicLayouts/(MasterSatelliteLayoutEngine|MasterSatelliteResults)\.cs$' } elseif ($Scenario -eq 'TransitionRounded') { '^FancyWM/Utilities/TransitionTargetGroup\.cs$' } elseif ($Scenario -eq 'ResizeTraversal') { '^FancyWM/TilingWorkspace\.cs$' } elseif ($Scenario -eq 'OverlayUpdates') { '^FancyWM/(TilingOverlayRenderer|Utilities/Collections)\.cs$' } elseif ($Scenario -eq 'OverlayAnimation') { '^FancyWM/Controls/(TilingOverlay|NonHitTestableTilingOverlay)\.xaml\.cs$' } elseif ($Scenario -eq 'SequenceHash') { '^FancyWM/Utilities/Collections\.cs$' } elseif ($Scenario -eq 'SettingsDistinct') { '^FancyWM/Models/Entities\.cs$' } elseif ($Scenario -eq 'SettingsRuntime') { '^FancyWM/TilingService(\.[^/]+)?\.cs$' } else { '^FancyWM/AlgorithmicLayouts/(AlgorithmicLayoutCoordinator(\.[^/]+)?|PendingWindowTransfer)\.cs$' }
    if ($Scenario -eq 'BindingListener') { $sourcePattern = '^FancyWM/Utilities/BindingErrorListener\.cs$' }
    if ($Scenario -in @('MainWindowLifetime','ConstructionLifetime')) { $sourcePattern = '^FancyWM/(MainWindow\.xaml\.cs|Utilities/MainWindowLifetime\.cs)$' }
    if ($Scenario -in @('AppShutdown','AppAsyncShutdown')) { $sourcePattern = '^FancyWM/App(?:\.xaml|\.Lifetime)\.cs$' }
    if ($Scenario -eq 'LayoutCadence') { $sourcePattern = '^FancyWM/TilingService(?:\.Private)?\.cs$' }
    if ($Scenario -in @('FocusSupersession','FocusBounded','FocusRejectedAdmission')) { $sourcePattern = '^FancyWM/Utilities/FocusHelper\.cs$' }
    if ($Scenario -in @('ClockBoost','AnimationShutdown')) { $sourcePattern = '^FancyWM/Utilities/AnimationThread\.cs$' }
    if ($Scenario -eq 'DependentShutdown') { $sourcePattern = '^FancyWM/(MainWindow\.xaml\.cs|Utilities/(MainWindowLifetime|AnimationThread)\.cs)$' }
    if ($Scenario -eq 'WindowDraggerLifetime') { $sourcePattern = '^FancyWM/Utilities/WindowDragger\.cs$' }
    if ($Scenario -eq 'ModifierDragLifetime') { $sourcePattern = '^FancyWM/Utilities/(ModifierWindowMover|WindowDragger)\.cs$' }
    if ($Scenario -eq 'HookLifetime') { $sourcePattern = '^FancyWM/Utilities/(LowLevelKeyboardHook|LowLevelMouseHook|HookRegistrationPolicy)\.cs$' }
    if ($Scenario -eq 'HookDisposal') { $sourcePattern = '^(FancyWM/Utilities/(LowLevelKeyboardHook|LowLevelMouseHook|HookRegistrationPolicy)\.cs|FancyWM\.Tests/Utilities/LowLevelHookLifetimeTest\.Dispose\.cs)$' }
    if ($Scenario -eq 'LowLevelPatternLifetime') { $sourcePattern = '^FancyWM/Utilities/(LowLevelKeyPatternListener|LowLevelKeyboardHook|KeyPatternListener)\.cs$' }
    if ($Scenario -eq 'DelayedCommandLifetime') { $sourcePattern = '^(FancyWM/(MainWindow\.xaml\.cs|Utilities/MainWindowLifetime\.cs)|FancyWM\.Tests/Utilities/MainWindowLifetimeTest\.DelayedCommand\.cs)$' }
    if ($Scenario -eq 'VirtualDesktopCallbackLifetime') { $sourcePattern = '^(FancyWM/(MainWindow\.xaml\.cs|Utilities/MainWindowLifetime\.cs)|FancyWM\.Tests/Utilities/MainWindowLifetimeTest\.VirtualDesktopChanged\.cs)$' }
    if ($Scenario -eq 'VirtualDesktopRemovalLifetime') { $sourcePattern = '^(FancyWM/(MainWindow\.xaml\.cs|Utilities/MainWindowLifetime\.cs)|FancyWM\.Tests/Utilities/MainWindowLifetimeTest\.VirtualDesktopRemoved\.cs)$' }
    if ($Scenario -eq 'TilingNotificationLifetime') { $sourcePattern = '^(FancyWM/(MainWindow\.xaml\.cs|Utilities/MainWindowLifetime\.cs)|FancyWM\.Tests/Utilities/MainWindowLifetimeTest\.TilingNotifications\.cs)$' }
    if ($Scenario -eq 'DirectHotkeyLifetime') { $sourcePattern = '^(FancyWM/(MainWindow\.xaml\.cs|Utilities/MainWindowLifetime\.cs)|FancyWM\.Tests/Utilities/MainWindowLifetimeTest\.DirectHotkey\.cs)$' }
    if ($Scenario -eq 'MicaCallbackLifetime') { $sourcePattern = '^(FancyWM/(MainWindow\.xaml\.cs|Utilities/MainWindowLifetime\.cs)|FancyWM\.Tests/Utilities/MainWindowLifetimeTest\.MicaCallback\.cs)$' }
    if ($Scenario -eq 'LoadedUpdateCheckLifetime') { $sourcePattern = '^(FancyWM/(MainWindow\.xaml\.cs|Utilities/(MainWindowLifetime|LoadedUpdateCheck)\.cs)|FancyWM\.Tests/Utilities/MainWindowLifetimeTest\.UpdateCheckLoop\.cs)$' }
    if ($Scenario -eq 'DisplayManageability') { $sourcePattern = '^FancyWM/TilingService\.Private\.cs$' }
    if ($Scenario -eq 'DiscoveryWindowSnapshot') { $sourcePattern = '^FancyWM/TilingService\.cs$' }
    if ($Scenario -eq 'RefreshWindowSnapshot') { $sourcePattern = '^FancyWM/TilingService\.cs$' }
    if ($Scenario -eq 'LayoutCallbackCache') { $sourcePattern = '^FancyWM/Utilities/LayoutInvalidationQueue\.cs$' }
    if ($Scenario -eq 'ContainsWindow') { $sourcePattern = '^FancyWM/AlgorithmicLayouts/MasterSatelliteLayoutEngine\.cs$' }
    if ($Scenario -eq 'TransitionCompletion') { $sourcePattern = '^FancyWM/Utilities/Tasks\.cs$' }
    if ($Scenario -eq 'RuntimeStateCount') { $sourcePattern = '^FancyWM/(AlgorithmicLayouts/(MasterSatelliteRuntimeRegistry|AlgorithmicLayoutCoordinator)|TilingService\.MasterSatellite)\.cs$' }
    if ($Scenario -eq 'PanelIndex') { $sourcePattern = '^FancyWM\.Layouts/Tiling/PanelNode\.cs$' }
    if ($Scenario -eq 'EmbedIndex') { $sourcePattern = '^FancyWM\.Layouts/Tiling/(?:TilingNode|PanelNode)\.cs$' }
    if ($Scenario -eq 'SplitPanelMeasure') { $sourcePattern = '^FancyWM\.Layouts/Tiling/SplitPanelNode\.cs$' }
    if ($Scenario -eq 'StackPanelMeasure') { $sourcePattern = '^FancyWM\.Layouts/Tiling/StackPanelNode\.cs$' }
    if ($Scenario -eq 'PlaceholderCleanup') { $sourcePattern = '^FancyWM\.Layouts/Tiling/PanelNode\.cs$' }
    if ($Scenario -eq 'SettingsDispose') { $sourcePattern = '^FancyWM/ViewModels/(SettingsViewModel|ViewModelBase|KeybindingViewModel)\.cs$' }
    if ($Scenario -eq 'StartupWindowClose') { $sourcePattern = '^FancyWM/(Windows/StartupWindow\.xaml(?:\.cs)?|ViewModels/SettingsViewModel\.cs)$' }
    if ($Scenario -eq 'SettingsWindowClose') { $sourcePattern = '^FancyWM/(Windows/SettingsWindow(?:\.xaml(?:\.cs)?|\.PageNavigation\.cs)|ViewModels/SettingsViewModel\.cs|Utilities/GCHelper\.cs|App\.xaml(?:\.cs)?)$' }
    if ($Scenario -eq 'OverlayHostClose') { $sourcePattern = '^(FancyWM/(Windows/OverlayHost(?:\.(?:Cursor|OverlayWindow))?\.cs|Utilities/(OverlayRefreshLoop|LayoutInvalidationQueue|LowLevelMouseHook)\.cs|AssemblyInfo\.cs|FancyWM\.csproj)|FancyWM\.Tests/(Utilities/OverlayHostCloseTest\.cs|FancyWM\.Tests\.csproj)|Directory\.Build\.props|version\.json)$' }
    if ($Scenario -eq 'OverlayWindowClose') { $sourcePattern = '^(FancyWM/(Windows/OverlayHost(?:\.(?:Cursor|OverlayWindow))?\.cs|Utilities/(OverlayRefreshLoop|LayoutInvalidationQueue|LowLevelMouseHook)\.cs|AssemblyInfo\.cs|FancyWM\.csproj)|FancyWM\.Tests/(Utilities/OverlayWindowCloseTest\.cs|FancyWM\.Tests\.csproj)|Directory\.Build\.props|version\.json)$' }
    if ($Scenario -eq 'OverlayWindowCallbacks') {
        $sourcePattern = '^(?:' + (($overlayWindowCloseSources | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')$'
    }
    if ($Scenario -in @('TilingOverlayRendererDispose','TilingOverlayRendererResources','TilingOverlayRendererRemoval','TilingOverlayRendererCreation','TilingOverlayRendererRecovery')) {
        $sourcePattern = '^(?:' + (($tilingOverlayRendererDisposeSources | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')$'
    }
    if ($Scenario -eq 'ArrangeFailureBookkeeping') {
        $sourcePattern = '^(?:' + (($arrangeFailureBookkeepingSources | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')$'
    }
    if ($Scenario -eq 'TilingWindowLifetime') {
        $sourcePattern = '^(?:' + (($tilingWindowLifetimeSources | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')$'
    }
    $sourceRecords = @(Import-Csv "$baselineRoot/manifest.csv" | Where-Object { $_.Path -match $sourcePattern })
    $requiredSource = if ($Scenario -eq 'KeyPressDisplay') { 'FancyWM/Controls/KeyPressBox.xaml.cs' } elseif ($Scenario -in @('SvgMaterialization','ActionBarMaterialization')) { 'FancyWM/Controls/SvgIcon.xaml.cs' } elseif ($Scenario -eq 'HookReplacement') { 'FancyWM/Utilities/HookRegistrationPolicy.cs' } elseif ($Scenario -eq 'DisplayRemovalGc') { 'FancyWM/MultiDisplayTilingService.cs' } elseif ($Scenario -eq 'CursorInput') { 'FancyWM/Windows/OverlayHost.Cursor.cs' } elseif ($Scenario -eq 'SettingsStrings') { 'FancyWM/Controls/StringsListBox.xaml.cs' } elseif ($Scenario -eq 'GenericPreview') { 'FancyWM/TilingWorkspace.cs' } elseif ($Scenario -eq 'AnimationFrame') { 'FancyWM/Utilities/AnimationThread.cs' } elseif ($Scenario -eq 'HotkeyCallback') { 'FancyWM/Utilities/LowLevelHotkey.cs' } elseif ($Scenario -eq 'PreviewCacheManaged') { 'FancyWM/AlgorithmicLayouts/MasterSatelliteDropController.cs' } elseif ($Scenario -in @('TreeStateLookup','WindowStateLookup')) { 'FancyWM/TilingWorkspace.cs' } elseif ($Scenario -eq 'WorkspaceInvariant') { 'FancyWM/TilingWorkspace.Algorithmic.cs' } elseif ($Scenario -eq 'TransitionDispatch') { 'FancyWM/Utilities/TransitionTargetGroup.cs' } elseif ($Scenario -eq 'PostMoveTimer') { 'FancyWM/TilingService.MasterSatellite.cs' } elseif ($Scenario -eq 'PreviewCache') { 'FancyWM/AlgorithmicLayouts/MasterSatelliteDropController.cs' } elseif ($Scenario -eq 'OwnershipTimer') { 'FancyWM/TilingService.MasterSatellite.cs' } elseif ($Scenario -eq 'DisabledLogging') { 'FancyWM/TilingService.Private.cs' } elseif ($Scenario -eq 'DiagnosticReuse') { 'FancyWM/AlgorithmicLayouts/MasterSatelliteLayoutEngine.cs' } elseif ($Scenario -eq 'TransitionRounded') { 'FancyWM/Utilities/TransitionTargetGroup.cs' } elseif ($Scenario -eq 'ResizeTraversal') { 'FancyWM/TilingWorkspace.cs' } elseif ($Scenario -eq 'OverlayUpdates') { 'FancyWM/TilingOverlayRenderer.cs' } elseif ($Scenario -eq 'OverlayAnimation') { 'FancyWM/Controls/TilingOverlay.xaml.cs' } elseif ($Scenario -eq 'SequenceHash') { 'FancyWM/Utilities/Collections.cs' } elseif ($Scenario -eq 'SettingsDistinct') { 'FancyWM/Models/Entities.cs' } elseif ($Scenario -eq 'SettingsRuntime') { 'FancyWM/TilingService.cs' } else { 'FancyWM/AlgorithmicLayouts/AlgorithmicLayoutCoordinator.cs' }
    if ($Scenario -eq 'BindingListener') { $requiredSource = 'FancyWM/Utilities/BindingErrorListener.cs' }
    if ($Scenario -in @('MainWindowLifetime','ConstructionLifetime')) { $requiredSource = 'FancyWM/Utilities/MainWindowLifetime.cs' }
    if ($Scenario -in @('MainWindowLifetime','ConstructionLifetime') -and 'FancyWM/MainWindow.xaml.cs' -notin $sourceRecords.Path) { throw 'Baseline MainWindow cleanup integration missing from manifest' }
    if ($Scenario -in @('AppShutdown','AppAsyncShutdown')) { $requiredSource = 'FancyWM/App.Lifetime.cs' }
    if ($Scenario -eq 'DisplayManageability') { $requiredSource = 'FancyWM/TilingService.Private.cs' }
    if ($Scenario -eq 'DiscoveryWindowSnapshot') { $requiredSource = 'FancyWM/TilingService.cs' }
    if ($Scenario -eq 'RefreshWindowSnapshot') { $requiredSource = 'FancyWM/TilingService.cs' }
    if ($Scenario -eq 'LayoutCallbackCache') { $requiredSource = 'FancyWM/Utilities/LayoutInvalidationQueue.cs' }
    if ($Scenario -eq 'ContainsWindow') { $requiredSource = 'FancyWM/AlgorithmicLayouts/MasterSatelliteLayoutEngine.cs' }
    if ($Scenario -eq 'TransitionCompletion') { $requiredSource = 'FancyWM/Utilities/Tasks.cs' }
    if ($Scenario -eq 'RuntimeStateCount') { $requiredSource = 'FancyWM/AlgorithmicLayouts/MasterSatelliteRuntimeRegistry.cs' }
    if ($Scenario -eq 'PanelIndex') { $requiredSource = 'FancyWM.Layouts/Tiling/PanelNode.cs' }
    if ($Scenario -eq 'EmbedIndex') { $requiredSource = 'FancyWM.Layouts/Tiling/TilingNode.cs' }
    if ($Scenario -eq 'EmbedIndex' -and 'FancyWM.Layouts/Tiling/PanelNode.cs' -notin $sourceRecords.Path) { throw 'Baseline validated panel-index helper source missing from manifest' }
    if ($Scenario -eq 'SplitPanelMeasure') { $requiredSource = 'FancyWM.Layouts/Tiling/SplitPanelNode.cs' }
    if ($Scenario -eq 'StackPanelMeasure') { $requiredSource = 'FancyWM.Layouts/Tiling/StackPanelNode.cs' }
    if ($Scenario -eq 'PlaceholderCleanup') { $requiredSource = 'FancyWM.Layouts/Tiling/PanelNode.cs' }
    if ($Scenario -eq 'SettingsDispose') {
        $requiredSource = 'FancyWM/ViewModels/SettingsViewModel.cs'
        if ('FancyWM/ViewModels/ViewModelBase.cs' -notin $sourceRecords.Path -or
            'FancyWM/ViewModels/KeybindingViewModel.cs' -notin $sourceRecords.Path) {
            throw 'Baseline SettingsViewModel event-owner dependencies missing from manifest'
        }
    }
    if ($Scenario -eq 'StartupWindowClose') {
        $requiredSource = 'FancyWM/Windows/StartupWindow.xaml.cs'
        if ('FancyWM/Windows/StartupWindow.xaml' -notin $sourceRecords.Path -or
            'FancyWM/ViewModels/SettingsViewModel.cs' -notin $sourceRecords.Path) {
            throw 'Baseline StartupWindow markup or owned SettingsViewModel source missing from manifest'
        }
    }
    if ($Scenario -eq 'SettingsWindowClose') {
        $requiredSource = 'FancyWM/Windows/SettingsWindow.xaml.cs'
        if ('FancyWM/Windows/SettingsWindow.xaml' -notin $sourceRecords.Path -or
            'FancyWM/Windows/SettingsWindow.PageNavigation.cs' -notin $sourceRecords.Path -or
            'FancyWM/ViewModels/SettingsViewModel.cs' -notin $sourceRecords.Path -or
            'FancyWM/Utilities/GCHelper.cs' -notin $sourceRecords.Path -or
            'FancyWM/App.xaml' -notin $sourceRecords.Path -or
            'FancyWM/App.xaml.cs' -notin $sourceRecords.Path) {
            throw 'Baseline SettingsWindow markup, page navigation, owned SettingsViewModel, collection helper or application resources missing from manifest'
        }
    }
    if ($Scenario -in @('AppShutdown','AppAsyncShutdown') -and 'FancyWM/App.xaml.cs' -notin $sourceRecords.Path) { throw 'Baseline App shutdown integration missing from manifest' }
    if ($Scenario -eq 'LayoutCadence') { $requiredSource = 'FancyWM/TilingService.Private.cs' }
    if ($Scenario -eq 'ArrangeFailureBookkeeping') { $requiredSource = 'FancyWM/TilingService.Private.cs' }
    if ($Scenario -eq 'LayoutCadence' -and 'FancyWM/TilingService.cs' -notin $sourceRecords.Path) { throw 'Baseline cadence service ownership and clock seam missing from manifest' }
    if ($Scenario -in @('FocusSupersession','FocusBounded','FocusRejectedAdmission')) { $requiredSource = 'FancyWM/Utilities/FocusHelper.cs' }
    if ($Scenario -in @('ClockBoost','AnimationShutdown')) { $requiredSource = 'FancyWM/Utilities/AnimationThread.cs' }
    if ($Scenario -eq 'DependentShutdown') {
        $requiredSource = 'FancyWM/Utilities/MainWindowLifetime.cs'
        if ('FancyWM/MainWindow.xaml.cs' -notin $sourceRecords.Path -or 'FancyWM/Utilities/AnimationThread.cs' -notin $sourceRecords.Path) {
            throw 'Baseline dependent-resource teardown integration or animation worker missing from manifest'
        }
    }
    if ($Scenario -eq 'WindowDraggerLifetime') { $requiredSource = 'FancyWM/Utilities/WindowDragger.cs' }
    if ($Scenario -eq 'ModifierDragLifetime') {
        $requiredSource = 'FancyWM/Utilities/ModifierWindowMover.cs'
        if ('FancyWM/Utilities/WindowDragger.cs' -notin $sourceRecords.Path) {
            throw 'Baseline modifier drag dependency missing from manifest'
        }
    }
    if ($Scenario -eq 'HookLifetime') {
        $requiredSource = 'FancyWM/Utilities/HookRegistrationPolicy.cs'
        if ('FancyWM/Utilities/LowLevelKeyboardHook.cs' -notin $sourceRecords.Path -or 'FancyWM/Utilities/LowLevelMouseHook.cs' -notin $sourceRecords.Path) {
            throw 'Baseline hook lifetime owners missing from manifest'
        }
    }
    if ($Scenario -eq 'HookDisposal') {
        $requiredSource = 'FancyWM/Utilities/HookRegistrationPolicy.cs'
        if ('FancyWM/Utilities/LowLevelKeyboardHook.cs' -notin $sourceRecords.Path -or
            'FancyWM/Utilities/LowLevelMouseHook.cs' -notin $sourceRecords.Path -or
            'FancyWM.Tests/Utilities/LowLevelHookLifetimeTest.Dispose.cs' -notin $sourceRecords.Path) {
            throw 'Baseline hook disposal owners or fixture missing from manifest'
        }
    }
    if ($Scenario -eq 'LowLevelPatternLifetime') {
        $requiredSource = 'FancyWM/Utilities/LowLevelKeyPatternListener.cs'
        if ('FancyWM/Utilities/LowLevelKeyboardHook.cs' -notin $sourceRecords.Path -or
            'FancyWM/Utilities/KeyPatternListener.cs' -notin $sourceRecords.Path) {
            throw 'Baseline borrowed hook event owner or pattern event contract missing from manifest'
        }
    }
    if ($Scenario -eq 'DelayedCommandLifetime') {
        $requiredSource = 'FancyWM/Utilities/MainWindowLifetime.cs'
        if ('FancyWM/MainWindow.xaml.cs' -notin $sourceRecords.Path -or
            'FancyWM.Tests/Utilities/MainWindowLifetimeTest.DelayedCommand.cs' -notin $sourceRecords.Path) {
            throw 'Baseline delayed command integration or counter fixture missing from manifest'
        }
    }
    if ($Scenario -eq 'VirtualDesktopCallbackLifetime') {
        $requiredSource = 'FancyWM/Utilities/MainWindowLifetime.cs'
        if ('FancyWM/MainWindow.xaml.cs' -notin $sourceRecords.Path -or
            'FancyWM.Tests/Utilities/MainWindowLifetimeTest.VirtualDesktopChanged.cs' -notin $sourceRecords.Path) {
            throw 'Baseline virtual desktop callback integration or counter fixture missing from manifest'
        }
    }
    if ($Scenario -eq 'VirtualDesktopRemovalLifetime') {
        $requiredSource = 'FancyWM/Utilities/MainWindowLifetime.cs'
        if ('FancyWM/MainWindow.xaml.cs' -notin $sourceRecords.Path -or
            'FancyWM.Tests/Utilities/MainWindowLifetimeTest.VirtualDesktopRemoved.cs' -notin $sourceRecords.Path) {
            throw 'Baseline virtual desktop removal integration or counter fixture missing from manifest'
        }
    }
    if ($Scenario -eq 'TilingNotificationLifetime') {
        $requiredSource = 'FancyWM/Utilities/MainWindowLifetime.cs'
        if ('FancyWM/MainWindow.xaml.cs' -notin $sourceRecords.Path -or
            'FancyWM.Tests/Utilities/MainWindowLifetimeTest.TilingNotifications.cs' -notin $sourceRecords.Path) {
            throw 'Baseline tiling notification integration or counter fixture missing from manifest'
        }
    }
    if ($Scenario -eq 'DirectHotkeyLifetime') {
        $requiredSource = 'FancyWM/Utilities/MainWindowLifetime.cs'
        if ('FancyWM/MainWindow.xaml.cs' -notin $sourceRecords.Path -or
            'FancyWM.Tests/Utilities/MainWindowLifetimeTest.DirectHotkey.cs' -notin $sourceRecords.Path) {
            throw 'Baseline direct hotkey integration or counter fixture missing from manifest'
        }
    }
    if ($Scenario -eq 'MicaCallbackLifetime') {
        $requiredSource = 'FancyWM/Utilities/MainWindowLifetime.cs'
        if ('FancyWM/MainWindow.xaml.cs' -notin $sourceRecords.Path -or
            'FancyWM.Tests/Utilities/MainWindowLifetimeTest.MicaCallback.cs' -notin $sourceRecords.Path) {
            throw 'Baseline Mica callback integration or counter fixture missing from manifest'
        }
    }
    if ($Scenario -eq 'LoadedUpdateCheckLifetime') {
        $requiredSource = 'FancyWM/Utilities/LoadedUpdateCheck.cs'
        if ('FancyWM/MainWindow.xaml.cs' -notin $sourceRecords.Path -or
            'FancyWM/Utilities/MainWindowLifetime.cs' -notin $sourceRecords.Path -or
            'FancyWM.Tests/Utilities/MainWindowLifetimeTest.UpdateCheckLoop.cs' -notin $sourceRecords.Path) {
            throw 'Baseline loaded update-check integration, lifetime owner or counter fixture missing from manifest'
        }
    }
    if ($Scenario -eq 'OverlayHostClose') {
        $requiredSource = 'FancyWM/Windows/OverlayHost.cs'
        foreach ($overlayHostCloseSource in @(
            'FancyWM/Windows/OverlayHost.Cursor.cs',
            'FancyWM/Windows/OverlayHost.OverlayWindow.cs',
            'FancyWM/Utilities/OverlayRefreshLoop.cs',
            'FancyWM/Utilities/LayoutInvalidationQueue.cs',
            'FancyWM/Utilities/LowLevelMouseHook.cs',
            'FancyWM/AssemblyInfo.cs',
            'FancyWM/FancyWM.csproj',
            'FancyWM.Tests/Utilities/OverlayHostCloseTest.cs',
            'FancyWM.Tests/FancyWM.Tests.csproj',
            'Directory.Build.props',
            'version.json'
        )) {
            if ($overlayHostCloseSource -notin $sourceRecords.Path) {
                throw "Baseline OverlayHost close dependency, counter fixture or build input missing from manifest: $overlayHostCloseSource"
            }
        }
    }
    if ($Scenario -in @('OverlayWindowClose','OverlayWindowCallbacks')) {
        $requiredSource = 'FancyWM/Windows/OverlayHost.OverlayWindow.cs'
        foreach ($overlayWindowCloseSource in $overlayWindowCloseSources) {
            $overlayWindowSourceMatches = @($sourceRecords | Where-Object { $_.Path -eq $overlayWindowCloseSource })
            if ($overlayWindowSourceMatches.Count -ne 1 -or
                ($Scenario -eq 'OverlayWindowCallbacks' -and $overlayWindowSourceMatches[0].Path -cne $overlayWindowCloseSource)) {
                throw "Baseline $Scenario source, fixture or build input must appear exactly once in manifest: $overlayWindowCloseSource"
            }
        }
        if ($Scenario -eq 'OverlayWindowCallbacks' -and
            ($overlayWindowCallbacksProvenance.BaselineSourceSHA256 -ne ($sourceRecords | Where-Object Path -eq $requiredSource).SHA256 -or
             $overlayWindowCallbacksProvenance.RegressionSourceSHA256 -ne ($sourceRecords | Where-Object Path -eq 'FancyWM.Tests/Utilities/OverlayWindowCloseTest.cs').SHA256)) {
            throw 'Baseline OverlayWindow callback production/regression provenance differs from the source manifest'
        }
    }
    if ($Scenario -in @('TilingOverlayRendererDispose','TilingOverlayRendererResources','TilingOverlayRendererRemoval','TilingOverlayRendererCreation','TilingOverlayRendererRecovery')) {
        $requiredSource = 'FancyWM/TilingOverlayRenderer.cs'
        foreach ($tilingOverlayRendererDisposeSource in $tilingOverlayRendererDisposeSources) {
            $baselineSourceMatches = @($sourceRecords | Where-Object { $_.Path -eq $tilingOverlayRendererDisposeSource })
            if ($baselineSourceMatches.Count -ne 1 -or $baselineSourceMatches[0].Path -cne $tilingOverlayRendererDisposeSource) {
                throw "Baseline $Scenario source, fixture or build input must appear exactly once in manifest: $tilingOverlayRendererDisposeSource"
            }
        }
    }
    if ($Scenario -eq 'TilingWindowLifetime') {
        $requiredSource = 'FancyWM/ViewModels/TilingWindowViewModel.cs'
        if (@($binaryRecord).Count -ne 1 -or (Split-Path $binaryRecord.Path -Leaf) -cne $productionAssembly -or
            $binaryRecord.Hash -cnotmatch '^[0-9A-F]{64}$' -or $binaryRecord.Hash -ceq $binaryHash) {
            throw 'TilingWindowLifetime requires one distinct archived baseline production assembly'
        }
        foreach ($source in $tilingWindowLifetimeSources) {
            $baselineSources = @($sourceRecords | Where-Object { $_.Path -eq $source })
            $candidateSource = @($candidateManifest | Where-Object { $_.Path -ceq $source })[0]
            if ($baselineSources.Count -ne 1 -or $baselineSources[0].Path -cne $source -or $baselineSources[0].SHA256 -cnotmatch '^[0-9A-F]{64}$' -or
                (Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $source)).Hash -cne $candidateSource.SHA256) {
                throw "TilingWindowLifetime source manifest or frozen candidate copy differs: $source"
            }
            if ($tilingWindowLifetimeBaselineSources.ContainsKey($source)) {
                if ($baselineSources[0].SHA256 -cne $tilingWindowLifetimeBaselineSources[$source] -or
                    $baselineSources[0].SHA256 -ceq $candidateSource.SHA256) {
                    throw "TilingWindowLifetime requires the pinned pre-edit R0 and changed candidate: $source"
                }
            }
            elseif ($baselineSources[0].SHA256 -cne $candidateSource.SHA256) {
                throw "TilingWindowLifetime common fixture or dependency differs: $source"
            }
        }
    }
    if ($Scenario -eq 'TilingOverlayRendererRecovery') {
        if (@($binaryRecord).Count -ne 1 -or $binaryRecord.Path -cne $productionAssembly -or
            $binaryRecord.Hash -cnotmatch '^[0-9A-F]{64}$' -or $binaryRecord.Hash -ceq $binaryHash) {
            throw 'TilingOverlayRenderer recovery requires one distinct archived baseline production assembly'
        }
        foreach ($recoverySource in $tilingOverlayRendererDisposeSources) {
            $baselineSource = @($sourceRecords | Where-Object { $_.Path -ceq $recoverySource })[0]
            $candidateSource = @($candidateManifest | Where-Object { $_.Path -ceq $recoverySource })[0]
            if ($baselineSource.SHA256 -cnotmatch '^[0-9A-F]{64}$' -or $candidateSource.SHA256 -cnotmatch '^[0-9A-F]{64}$' -or
                (Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $recoverySource)).Hash -cne $candidateSource.SHA256 -or
                (($recoverySource -ceq $requiredSource) -eq ($baselineSource.SHA256 -ceq $candidateSource.SHA256))) {
                throw "TilingOverlayRenderer recovery requires only the renderer to differ and a common frozen fixture, runner and dependencies: $recoverySource"
            }
        }
    }
    if ($Scenario -eq 'ArrangeFailureBookkeeping') {
        $arrangeFailureBookkeepingBaselineManifest = @(Import-Csv -LiteralPath "$baselineRoot/manifest.csv")
        $arrangeFailureBookkeepingBaselineBuildRecords = @($arrangeFailureBookkeepingBaselineManifest | Where-Object {
            $_.Path -match '\.(cs|csproj|xaml|props|targets|sln|slnx|ps1)$' -or
            $_.Path -in @('Directory.Build.props','version.json','global.json','NuGet.config')
        })
        if ($arrangeFailureBookkeepingBaselineBuildRecords.Count -ne $candidateBuildRecords.Count) {
            throw 'ArrangeFailureBookkeeping baseline/candidate build input sets differ'
        }
        $bookkeepingMechanicalSources = @('FancyWM/TilingService.Private.cs','FancyWM/AlgorithmicLayouts/MasterSatelliteArrangeFailure.cs')
        foreach ($candidateInput in $candidateBuildRecords) {
            $baselineInputs = @($arrangeFailureBookkeepingBaselineBuildRecords | Where-Object { $_.Path -eq $candidateInput.Path })
            if ($baselineInputs.Count -ne 1 -or $baselineInputs[0].Path -cne $candidateInput.Path -or
                $baselineInputs[0].SHA256 -cnotmatch '^[0-9A-F]{64}$' -or $candidateInput.SHA256 -cnotmatch '^[0-9A-F]{64}$' -or
                (Get-FileHash -LiteralPath (Join-Path "$baselineRoot/source" $candidateInput.Path)).Hash -cne $baselineInputs[0].SHA256 -or
                (Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $candidateInput.Path)).Hash -cne $candidateInput.SHA256) {
                throw "ArrangeFailureBookkeeping frozen build input differs: $($candidateInput.Path)"
            }
            $sameSource = $baselineInputs[0].SHA256 -ceq $candidateInput.SHA256
            if (($candidateInput.Path -cin $bookkeepingMechanicalSources) -eq $sameSource) {
                throw "ArrangeFailureBookkeeping requires only the two declared mechanical source differences: $($candidateInput.Path)"
            }
        }
        foreach ($arrangeFailureBookkeepingSource in $arrangeFailureBookkeepingSources) {
            $baselineSourceMatches = @($sourceRecords | Where-Object { $_.Path -eq $arrangeFailureBookkeepingSource })
            if ($baselineSourceMatches.Count -ne 1 -or $baselineSourceMatches[0].Path -cne $arrangeFailureBookkeepingSource) {
                throw "Baseline ArrangeFailureBookkeeping source, fixture or build input must appear exactly once in manifest: $arrangeFailureBookkeepingSource"
            }
        }
        if (@($binaryRecord).Count -ne 1 -or (Split-Path $binaryRecord.Path -Leaf) -cne $productionAssembly -or
            $binaryRecord.Hash -cnotmatch '^[0-9A-F]{64}$' -or $binaryRecord.Hash -ceq $binaryHash) {
            throw 'ArrangeFailureBookkeeping requires one distinct archived baseline production assembly'
        }
        $bookkeepingProvenance = Get-Content -LiteralPath "$baselineRoot/release-test-binaries.provenance.json" -Raw | ConvertFrom-Json
        if ($bookkeepingProvenance.Snapshot -cne $BaselineSnapshotId -or
            $bookkeepingProvenance.SnapshotManifestSHA256 -cne (Get-FileHash -LiteralPath "$baselineRoot/manifest.csv").Hash -or
            $bookkeepingProvenance.ArchiveManifestSHA256 -cne (Get-FileHash -LiteralPath "$baselineRoot/release-test-binaries.csv").Hash -or
            $bookkeepingProvenance.BuildSucceeded -isnot [bool] -or !$bookkeepingProvenance.BuildSucceeded -or
            [string]::IsNullOrWhiteSpace($bookkeepingProvenance.BuildCommand) -or
            $bookkeepingProvenance.Configuration -cne 'Release' -or
            $bookkeepingProvenance.TargetFramework -cne $testFramework -or
            $bookkeepingProvenance.Architecture -cne 'x64' -or
            $bookkeepingProvenance.Scope -cne 'full-perf-006-bookkeeping' -or
            $bookkeepingProvenance.FancyWMDllSHA256 -cne $binaryRecord.Hash -or
            $bookkeepingProvenance.FancyWMTestsDllSHA256 -cne (Get-FileHash -LiteralPath "$baselineRoot/release-test-binaries/$benchmarkAssembly").Hash) {
            throw 'ArrangeFailureBookkeeping baseline build/archive provenance differs'
        }
        $bookkeepingProvenanceSources = @{
            BaselineSource = 'FancyWM/TilingService.Private.cs'
            TrackerSource = 'FancyWM/AlgorithmicLayouts/MasterSatelliteArrangeFailure.cs'
            RegressionSource = 'FancyWM.Tests/AlgorithmicLayouts/TilingServiceAlgorithmicIntegrationTest.cs'
        }
        foreach ($property in $bookkeepingProvenanceSources.Keys) {
            $path = $bookkeepingProvenanceSources[$property]
            $sourceHash = ($sourceRecords | Where-Object { $_.Path -ceq $path }).SHA256
            if ($bookkeepingProvenance.$property -cne "source/$path" -or
                $bookkeepingProvenance.($property + 'SHA256') -cne $sourceHash) {
                throw "ArrangeFailureBookkeeping provenance source differs: $property"
            }
        }
        if (@($bookkeepingProvenance.CommonSources).Count -ne $arrangeFailureBookkeepingSources.Count) {
            throw 'ArrangeFailureBookkeeping provenance common source count differs'
        }
        foreach ($path in $arrangeFailureBookkeepingSources) {
            $records = @($bookkeepingProvenance.CommonSources | Where-Object { $_.Path -eq $path })
            if ($records.Count -ne 1 -or $records[0].Path -cne $path -or
                $records[0].SHA256 -cne ($sourceRecords | Where-Object { $_.Path -ceq $path }).SHA256) {
                throw "ArrangeFailureBookkeeping provenance common source differs: $path"
            }
        }
        if ([string]::IsNullOrWhiteSpace($bookkeepingProvenance.BuildLog) -or
            [System.IO.Path]::IsPathRooted($bookkeepingProvenance.BuildLog)) {
            throw 'ArrangeFailureBookkeeping build log must be relative to the baseline snapshot'
        }
        $bookkeepingBuildLog = [System.IO.Path]::GetFullPath((Join-Path $baselineRoot $bookkeepingProvenance.BuildLog))
        $bookkeepingBaselinePrefix = [System.IO.Path]::GetFullPath($baselineRoot).TrimEnd('\','/') + [System.IO.Path]::DirectorySeparatorChar
        if (!$bookkeepingBuildLog.StartsWith($bookkeepingBaselinePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
            $bookkeepingProvenance.BuildLogSHA256 -cne (Get-FileHash -LiteralPath $bookkeepingBuildLog).Hash) {
            throw 'ArrangeFailureBookkeeping archived build log checksum or path differs'
        }
        $bookkeepingArchiveRoot = [System.IO.Path]::GetFullPath("$baselineRoot/release-test-binaries")
        $bookkeepingArchiveFiles = @(Get-ChildItem -LiteralPath $bookkeepingArchiveRoot -Recurse -File)
        $bookkeepingArchiveManifest = @(Import-Csv -LiteralPath "$baselineRoot/release-test-binaries.csv")
        if ($bookkeepingArchiveFiles.Count -ne $bookkeepingArchiveManifest.Count -or
            $bookkeepingArchiveFiles.Count -ne $bookkeepingProvenance.CopiedFileCount -or
            @(Get-ChildItem -LiteralPath $bookkeepingArchiveRoot -File).Count -ne $bookkeepingProvenance.TopLevelFileCount -or
            ($bookkeepingArchiveFiles | Measure-Object Length -Sum).Sum -ne $bookkeepingProvenance.CopiedBytes) {
            throw 'ArrangeFailureBookkeeping baseline archive file count or size differs'
        }
        foreach ($file in $bookkeepingArchiveFiles) {
            $archiveRecords = @($bookkeepingArchiveManifest | Where-Object { $_.Path -eq $file.FullName })
            if ($archiveRecords.Count -ne 1 -or $archiveRecords[0].Path -cne $file.FullName -or
                $archiveRecords[0].Hash -cnotmatch '^[0-9A-F]{64}$' -or
                (Get-FileHash -LiteralPath $file.FullName).Hash -cne $archiveRecords[0].Hash) {
                throw "ArrangeFailureBookkeeping baseline archive input differs: $($file.Name)"
            }
        }
    }
    if ($Scenario -eq 'DiscoveryWindowSnapshot') {
        $baselineManifest = @(Import-Csv -LiteralPath "$baselineRoot/manifest.csv")
        $baselineBuildRecords = @($baselineManifest | Where-Object {
            $_.Path -match '^(FancyWM/|FancyWM.Layouts/|FancyWM.Layouts.Tests/|FancyWM.ThemeEngine/|FancyWM.ThemeEngine.Tests/|FancyWM.Tests/|scripts/performance/).+\.(cs|csproj|ps1|xaml)$' -or
            $_.Path -in @('Directory.Build.props','version.json')
        })
        if ($baselineBuildRecords.Count -ne $candidateBuildRecords.Count) {
            throw 'DiscoveryWindowSnapshot baseline/candidate build input sets differ'
        }
        foreach ($candidateInput in $candidateBuildRecords) {
            $baselineInputs = @($baselineBuildRecords | Where-Object { $_.Path -eq $candidateInput.Path })
            if ($baselineInputs.Count -ne 1 -or $baselineInputs[0].Path -cne $candidateInput.Path -or
                $baselineInputs[0].SHA256 -cnotmatch '^[0-9A-F]{64}$' -or $candidateInput.SHA256 -cnotmatch '^[0-9A-F]{64}$' -or
                (Get-FileHash -LiteralPath (Join-Path "$baselineRoot/source" $candidateInput.Path)).Hash -cne $baselineInputs[0].SHA256 -or
                (Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $candidateInput.Path)).Hash -cne $candidateInput.SHA256) {
                throw "DiscoveryWindowSnapshot frozen build input differs: $($candidateInput.Path)"
            }
            $sameSource = $baselineInputs[0].SHA256 -ceq $candidateInput.SHA256
            if (($candidateInput.Path -ceq 'FancyWM/TilingService.cs') -eq $sameSource) {
                throw "DiscoveryWindowSnapshot requires only TilingService.cs to differ: $($candidateInput.Path)"
            }
        }
        if (@($binaryRecord).Count -ne 1 -or (Split-Path $binaryRecord.Path -Leaf) -cne $productionAssembly -or
            $binaryRecord.Hash -cnotmatch '^[0-9A-F]{64}$' -or $binaryRecord.Hash -ceq $binaryHash) {
            throw 'DiscoveryWindowSnapshot requires one distinct archived baseline FancyWM.dll'
        }
    }
    if ($Scenario -eq 'LayoutCallbackCache') {
        $baselineManifest = @(Import-Csv -LiteralPath "$baselineRoot/manifest.csv")
        $layoutCallbackBaselineBuildRecords = @($baselineManifest | Where-Object {
            $_.Path -match '\.(cs|csproj|xaml|props|targets|sln|slnx|ps1)$' -or
            $_.Path -match '(^|/)(global\.json|NuGet\.config)$' -or $_.Path -ceq 'version.json'
        })
        if ($layoutCallbackBaselineBuildRecords.Count -ne $candidateBuildRecords.Count) {
            throw 'LayoutCallbackCache baseline/candidate build input sets differ'
        }
        foreach ($candidateInput in $candidateBuildRecords) {
            $baselineInputs = @($layoutCallbackBaselineBuildRecords | Where-Object { $_.Path -eq $candidateInput.Path })
            if ($baselineInputs.Count -ne 1 -or $baselineInputs[0].Path -cne $candidateInput.Path -or
                $baselineInputs[0].SHA256 -cnotmatch '^[0-9A-F]{64}$' -or $candidateInput.SHA256 -cnotmatch '^[0-9A-F]{64}$' -or
                (Get-FileHash -LiteralPath (Join-Path "$baselineRoot/source" $candidateInput.Path)).Hash -cne $baselineInputs[0].SHA256 -or
                (Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $candidateInput.Path)).Hash -cne $candidateInput.SHA256) {
                throw "LayoutCallbackCache frozen build input differs: $($candidateInput.Path)"
            }
            $sameSource = $baselineInputs[0].SHA256 -ceq $candidateInput.SHA256
            if (($candidateInput.Path -ceq 'FancyWM/Utilities/LayoutInvalidationQueue.cs') -eq $sameSource) {
                throw "LayoutCallbackCache requires only LayoutInvalidationQueue.cs to differ: $($candidateInput.Path)"
            }
        }
        if (@($binaryRecord).Count -ne 1 -or (Split-Path $binaryRecord.Path -Leaf) -cne $productionAssembly -or
            $binaryRecord.Hash -cnotmatch '^[0-9A-F]{64}$' -or $binaryRecord.Hash -ceq $binaryHash) {
            throw 'LayoutCallbackCache requires one distinct archived baseline FancyWM.dll'
        }
    }
    if ($Scenario -eq 'FocusRejectedAdmission') {
        $baselineManifest = @(Import-Csv -LiteralPath "$baselineRoot/manifest.csv")
        $focusRejectedBaselineBuildRecords = @($baselineManifest | Where-Object {
            $_.Path -match '\.(cs|csproj|xaml|props|targets|sln|slnx|ps1)$' -or
            $_.Path -match '(^|/)(global\.json|NuGet\.config)$' -or $_.Path -ceq 'version.json'
        })
        if ($focusRejectedBaselineBuildRecords.Count -ne $candidateBuildRecords.Count) {
            throw 'FocusRejectedAdmission baseline/candidate build input sets differ'
        }
        $focusRejectedSourcePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($candidateInput in $candidateBuildRecords) {
            $baselineInputs = @($focusRejectedBaselineBuildRecords | Where-Object { $_.Path -eq $candidateInput.Path })
            if (!$focusRejectedSourcePaths.Add($candidateInput.Path) -or
                $baselineInputs.Count -ne 1 -or $baselineInputs[0].Path -cne $candidateInput.Path -or
                $baselineInputs[0].SHA256 -cnotmatch '^[0-9A-F]{64}$' -or $candidateInput.SHA256 -cnotmatch '^[0-9A-F]{64}$' -or
                (Get-FileHash -LiteralPath (Join-Path "$baselineRoot/source" $candidateInput.Path)).Hash -cne $baselineInputs[0].SHA256 -or
                (Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $candidateInput.Path)).Hash -cne $candidateInput.SHA256) {
                throw "FocusRejectedAdmission frozen build input differs: $($candidateInput.Path)"
            }
            $sameSource = $baselineInputs[0].SHA256 -ceq $candidateInput.SHA256
            if (($candidateInput.Path -ceq 'FancyWM/Utilities/FocusHelper.cs') -eq $sameSource) {
                throw "FocusRejectedAdmission requires only FocusHelper.cs to differ: $($candidateInput.Path)"
            }
        }
        if (@($binaryRecord).Count -ne 1 -or (Split-Path $binaryRecord.Path -Leaf) -cne $productionAssembly -or
            $binaryRecord.Hash -cnotmatch '^[0-9A-F]{64}$' -or $binaryRecord.Hash -ceq $binaryHash) {
            throw 'FocusRejectedAdmission requires one distinct archived baseline FancyWM.dll'
        }
    }
    if ($Scenario -eq 'ContainsWindow') {
        $baselineManifest = @(Import-Csv -LiteralPath "$baselineRoot/manifest.csv")
        $containsWindowBaselineBuildRecords = @($baselineManifest | Where-Object {
            $_.Path -match '\.(cs|csproj|xaml|props|targets|sln|slnx|ps1)$' -or
            $_.Path -match '(^|/)(global\.json|NuGet\.config)$' -or $_.Path -ceq 'version.json'
        })
        if ($containsWindowBaselineBuildRecords.Count -ne $candidateBuildRecords.Count) {
            throw 'ContainsWindow baseline/candidate build input sets differ'
        }
        foreach ($candidateInput in $candidateBuildRecords) {
            $baselineInputs = @($containsWindowBaselineBuildRecords | Where-Object { $_.Path -eq $candidateInput.Path })
            if ($baselineInputs.Count -ne 1 -or $baselineInputs[0].Path -cne $candidateInput.Path -or
                $baselineInputs[0].SHA256 -cnotmatch '^[0-9A-F]{64}$' -or $candidateInput.SHA256 -cnotmatch '^[0-9A-F]{64}$' -or
                (Get-FileHash -LiteralPath (Join-Path "$baselineRoot/source" $candidateInput.Path)).Hash -cne $baselineInputs[0].SHA256 -or
                (Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $candidateInput.Path)).Hash -cne $candidateInput.SHA256) {
                throw "ContainsWindow frozen build input differs: $($candidateInput.Path)"
            }
            $sameSource = $baselineInputs[0].SHA256 -ceq $candidateInput.SHA256
            if (($candidateInput.Path -ceq 'FancyWM/AlgorithmicLayouts/MasterSatelliteLayoutEngine.cs') -eq $sameSource) {
                throw "ContainsWindow requires only MasterSatelliteLayoutEngine.cs to differ: $($candidateInput.Path)"
            }
        }
        if (@($binaryRecord).Count -ne 1 -or (Split-Path $binaryRecord.Path -Leaf) -cne $productionAssembly -or
            $binaryRecord.Hash -cnotmatch '^[0-9A-F]{64}$' -or $binaryRecord.Hash -ceq $binaryHash) {
            throw 'ContainsWindow requires one distinct archived baseline FancyWM.dll'
        }
    }
    if ($Scenario -eq 'RefreshWindowSnapshot') {
        $baselineManifest = @(Import-Csv -LiteralPath "$baselineRoot/manifest.csv")
        $refreshWindowSnapshotBaselineBuildRecords = @($baselineManifest | Where-Object {
            $_.Path -match '^(FancyWM/|FancyWM.Layouts/|FancyWM.Layouts.Tests/|FancyWM.ThemeEngine/|FancyWM.ThemeEngine.Tests/|FancyWM.Tests/|scripts/performance/).+\.(cs|csproj|ps1|xaml)$' -or
            $_.Path -in @('Directory.Build.props','version.json')
        })
        if ($refreshWindowSnapshotBaselineBuildRecords.Count -ne $candidateBuildRecords.Count) {
            throw 'RefreshWindowSnapshot baseline/candidate build input sets differ'
        }
        foreach ($candidateInput in $candidateBuildRecords) {
            $baselineInputs = @($refreshWindowSnapshotBaselineBuildRecords | Where-Object { $_.Path -eq $candidateInput.Path })
            if ($baselineInputs.Count -ne 1 -or $baselineInputs[0].Path -cne $candidateInput.Path -or
                $baselineInputs[0].SHA256 -cnotmatch '^[0-9A-F]{64}$' -or $candidateInput.SHA256 -cnotmatch '^[0-9A-F]{64}$' -or
                (Get-FileHash -LiteralPath (Join-Path "$baselineRoot/source" $candidateInput.Path)).Hash -cne $baselineInputs[0].SHA256 -or
                (Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $candidateInput.Path)).Hash -cne $candidateInput.SHA256) {
                throw "RefreshWindowSnapshot frozen build input differs: $($candidateInput.Path)"
            }
            $sameSource = $baselineInputs[0].SHA256 -ceq $candidateInput.SHA256
            if (($candidateInput.Path -ceq 'FancyWM/TilingService.cs') -eq $sameSource) {
                throw "RefreshWindowSnapshot requires only TilingService.cs to differ: $($candidateInput.Path)"
            }
        }
        if (@($binaryRecord).Count -ne 1 -or (Split-Path $binaryRecord.Path -Leaf) -cne $productionAssembly -or
            $binaryRecord.Hash -cnotmatch '^[0-9A-F]{64}$' -or $binaryRecord.Hash -ceq $binaryHash) {
            throw 'RefreshWindowSnapshot requires one distinct archived baseline FancyWM.dll'
        }
    }
    if ($Scenario -eq 'TransitionCompletion') {
        $baselineManifest = @(Import-Csv -LiteralPath "$baselineRoot/manifest.csv")
        $baselineBuildRecords = @($baselineManifest | Where-Object {
            $_.Path -match '^(FancyWM/|FancyWM.Layouts/|FancyWM.Layouts.Tests/|FancyWM.ThemeEngine/|FancyWM.ThemeEngine.Tests/|FancyWM.Tests/|scripts/performance/).+\.(cs|csproj|ps1|xaml)$' -or
            $_.Path -in @('Directory.Build.props','version.json')
        })
        if ($baselineBuildRecords.Count -ne $candidateBuildRecords.Count) {
            throw 'TransitionCompletion baseline/candidate build input sets differ'
        }
        foreach ($candidateInput in $candidateBuildRecords) {
            $baselineInputs = @($baselineBuildRecords | Where-Object { $_.Path -eq $candidateInput.Path })
            if ($baselineInputs.Count -ne 1 -or $baselineInputs[0].Path -cne $candidateInput.Path -or
                $baselineInputs[0].SHA256 -cnotmatch '^[0-9A-F]{64}$' -or $candidateInput.SHA256 -cnotmatch '^[0-9A-F]{64}$' -or
                (Get-FileHash -LiteralPath (Join-Path "$baselineRoot/source" $candidateInput.Path)).Hash -cne $baselineInputs[0].SHA256 -or
                (Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $candidateInput.Path)).Hash -cne $candidateInput.SHA256) {
                throw "TransitionCompletion frozen build input differs: $($candidateInput.Path)"
            }
            $sameSource = $baselineInputs[0].SHA256 -ceq $candidateInput.SHA256
            if (($candidateInput.Path -ceq 'FancyWM/Utilities/Tasks.cs') -eq $sameSource) {
                throw "TransitionCompletion requires only Tasks.cs to differ: $($candidateInput.Path)"
            }
        }
        if (@($binaryRecord).Count -ne 1 -or (Split-Path $binaryRecord.Path -Leaf) -cne $productionAssembly -or
            $binaryRecord.Hash -cnotmatch '^[0-9A-F]{64}$' -or $binaryRecord.Hash -ceq $binaryHash) {
            throw 'TransitionCompletion requires one distinct archived baseline FancyWM.dll'
        }
    }
    if ($Scenario -eq 'RuntimeStateCount') {
        $runtimeStateCountProvenance = Get-Content -LiteralPath "$baselineRoot/release-test-binaries.provenance.json" -Raw | ConvertFrom-Json
        if ($runtimeStateCountProvenance.Snapshot -cne $BaselineSnapshotId -or
            $runtimeStateCountProvenance.BuildSucceeded -isnot [bool] -or !$runtimeStateCountProvenance.BuildSucceeded -or
            $runtimeStateCountProvenance.SnapshotManifestSHA256 -cne (Get-FileHash -LiteralPath "$baselineRoot/manifest.csv").Hash -or
            $runtimeStateCountProvenance.ArchiveManifestSHA256 -cne (Get-FileHash -LiteralPath "$baselineRoot/release-test-binaries.csv").Hash -or
            $runtimeStateCountProvenance.FancyWMDllSHA256 -cne $binaryRecord.Hash) {
            throw 'RuntimeStateCount baseline build/archive provenance differs from the frozen snapshot'
        }
        $baselineManifest = @(Import-Csv -LiteralPath "$baselineRoot/manifest.csv")
        $baselineBuildRecords = @($baselineManifest | Where-Object {
            $_.Path -match '\.(cs|csproj|xaml|props|targets|sln|slnx|ps1)$' -or
            $_.Path -match '(^|/)(global\.json|NuGet\.config)$' -or $_.Path -eq 'version.json'
        })
        if ($baselineBuildRecords.Count -ne $candidateBuildRecords.Count) {
            throw 'RuntimeStateCount baseline/candidate build input sets differ'
        }
        $runtimeStateCountSourcePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($candidateInput in $candidateBuildRecords) {
            $baselineInputs = @($baselineBuildRecords | Where-Object { $_.Path -eq $candidateInput.Path })
            if (!$runtimeStateCountSourcePaths.Add($candidateInput.Path) -or
                $baselineInputs.Count -ne 1 -or $baselineInputs[0].Path -cne $candidateInput.Path -or
                $baselineInputs[0].SHA256 -cnotmatch '^[0-9A-F]{64}$' -or $candidateInput.SHA256 -cnotmatch '^[0-9A-F]{64}$' -or
                (Get-FileHash -LiteralPath (Join-Path "$baselineRoot/source" $candidateInput.Path)).Hash -cne $baselineInputs[0].SHA256 -or
                (Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $candidateInput.Path)).Hash -cne $candidateInput.SHA256) {
                throw "RuntimeStateCount frozen build input differs: $($candidateInput.Path)"
            }
            $sameSource = $baselineInputs[0].SHA256 -ceq $candidateInput.SHA256
            if (($candidateInput.Path -cin $runtimeStateCountChangedSources) -eq $sameSource) {
                throw "RuntimeStateCount requires only the three count-path sources to differ: $($candidateInput.Path)"
            }
        }
        if (@($binaryRecord).Count -ne 1 -or (Split-Path $binaryRecord.Path -Leaf) -cne $productionAssembly -or
            $binaryRecord.Hash -cnotmatch '^[0-9A-F]{64}$' -or $binaryRecord.Hash -ceq $binaryHash) {
            throw 'RuntimeStateCount requires one distinct archived baseline FancyWM.dll'
        }
    }
    if ($requiredSource -notin $sourceRecords.Path) { throw "Baseline source missing from manifest: $requiredSource" }
    if ($Scenario -in @('DiagnosticReuse','WorkspaceInvariant') -and 'FancyWM/AlgorithmicLayouts/MasterSatelliteResults.cs' -notin $sourceRecords.Path) { throw 'Baseline invariant-result source missing from manifest' }
    if ($Scenario -eq 'HookReplacement' -and ('FancyWM/Utilities/LowLevelKeyboardHook.cs' -notin $sourceRecords.Path -or 'FancyWM/Utilities/LowLevelMouseHook.cs' -notin $sourceRecords.Path)) { throw 'Baseline hook owners missing from manifest' }
    if ($Scenario -in @('SvgMaterialization','ActionBarMaterialization') -and 'FancyWM/Controls/SvgIcon.xaml' -notin $sourceRecords.Path) { throw 'Baseline SVG markup missing from manifest' }
    if ($Scenario -eq 'ActionBarMaterialization' -and ('FancyWM/Controls/TilingWindow.xaml' -notin $sourceRecords.Path -or 'FancyWM/Controls/TilingWindow.xaml.cs' -notin $sourceRecords.Path)) { throw 'Baseline actionbar markup/code missing from manifest' }
    if ($Scenario -eq 'KeyPressDisplay' -and ('FancyWM/Controls/KeyPressBox.xaml' -notin $sourceRecords.Path -or 'FancyWM/Utilities/KeyPatternListener.cs' -notin $sourceRecords.Path)) { throw 'Baseline key editor markup/listener missing from manifest' }
    if ($Scenario -eq 'CursorInput' -and 'FancyWM/Windows/OverlayHost.cs' -notin $sourceRecords.Path) { throw 'Baseline overlay owner source missing from manifest' }
    if ($Scenario -eq 'SettingsStrings' -and 'FancyWM/Controls/StringsListBox.xaml' -notin $sourceRecords.Path) { throw 'Baseline strings template missing from manifest' }
    if ($Scenario -eq 'HotkeyCallback' -and 'FancyWM/Utilities/LowLevelKeyboardHook.cs' -notin $sourceRecords.Path) { throw 'Baseline hook source missing from manifest' }
    if ($Scenario -eq 'OverlayUpdates' -and 'FancyWM/Utilities/Collections.cs' -notin $sourceRecords.Path) { throw 'Baseline ordered diff source missing from manifest' }
    if ($Scenario -eq 'OverlayAnimation' -and 'FancyWM/Controls/NonHitTestableTilingOverlay.xaml.cs' -notin $sourceRecords.Path) { throw 'Baseline non-hit overlay source missing from manifest' }
    if ($Scenario -like 'Coordinator*' -and 'FancyWM/AlgorithmicLayouts/PendingWindowTransfer.cs' -notin $sourceRecords.Path) { throw 'Baseline transfer/capacity source missing from manifest' }
    foreach ($sourceRecord in $sourceRecords) {
        if ((Get-FileHash "$baselineRoot/source/$($sourceRecord.Path)").Hash -ne $sourceRecord.SHA256) {
            throw "Baseline source checksum mismatch: $($sourceRecord.Path)"
        }
    }
    New-Item -ItemType Directory -Path "$experimentRoot/baseline" | Out-Null
    Copy-Item -Path "$binaryRoot/*" -Destination "$experimentRoot/baseline" -Recurse
    Copy-Item -LiteralPath $baselineBinary -Destination "$experimentRoot/baseline/$productionAssembly"
    if ($Scenario -in @('OverlayWindowCallbacks','ArrangeFailureBookkeeping','TilingOverlayRendererRecovery','TilingWindowLifetime','DiscoveryWindowSnapshot','RefreshWindowSnapshot','LayoutCallbackCache','ContainsWindow','FocusRejectedAdmission','TransitionCompletion','RuntimeStateCount')) {
        $binaryCheckDescription = if ($Scenario -eq 'FocusRejectedAdmission') { 'FocusRejectedAdmission' } elseif ($Scenario -eq 'ContainsWindow') { 'ContainsWindow' } elseif ($Scenario -eq 'LayoutCallbackCache') { 'LayoutCallbackCache' } elseif ($Scenario -eq 'ArrangeFailureBookkeeping') { 'ArrangeFailureBookkeeping' } elseif ($Scenario -eq 'TilingOverlayRendererRecovery') { 'TilingOverlayRenderer recovery' } elseif ($Scenario -eq 'TilingWindowLifetime') { 'TilingWindowLifetime' } elseif ($Scenario -eq 'DiscoveryWindowSnapshot') { 'DiscoveryWindowSnapshot' } elseif ($Scenario -eq 'RefreshWindowSnapshot') { 'RefreshWindowSnapshot' } elseif ($Scenario -eq 'TransitionCompletion') { 'TransitionCompletion' } elseif ($Scenario -eq 'RuntimeStateCount') { 'RuntimeStateCount' } else { 'OverlayWindow callback' }
        $overlayWindowCallbackCandidateFiles = @(Get-ChildItem -LiteralPath $binaryRoot -Recurse -File)
        $overlayWindowCallbackBaselineFiles = @(Get-ChildItem -LiteralPath "$experimentRoot/baseline" -Recurse -File)
        if ($overlayWindowCallbackCandidateFiles.Count -ne $overlayWindowCallbackBaselineFiles.Count) {
            throw "$binaryCheckDescription baseline/candidate binary tree file counts differ"
        }
        $overlayWindowCallbackBaselinePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($baselineFile in $overlayWindowCallbackBaselineFiles) {
            [void]$overlayWindowCallbackBaselinePaths.Add([System.IO.Path]::GetRelativePath("$experimentRoot/baseline", $baselineFile.FullName).Replace('\','/'))
        }
        $overlayWindowCallbackBinaryChecks = foreach ($candidateFile in $overlayWindowCallbackCandidateFiles) {
            $relativeBinaryPath = [System.IO.Path]::GetRelativePath($binaryRoot, $candidateFile.FullName).Replace('\','/')
            if (!$overlayWindowCallbackBaselinePaths.Contains($relativeBinaryPath)) {
                throw "$binaryCheckDescription baseline binary path differs: $relativeBinaryPath"
            }
            $candidateFileHash = (Get-FileHash -LiteralPath $candidateFile.FullName).Hash
            $baselineFileHash = (Get-FileHash -LiteralPath (Join-Path "$experimentRoot/baseline" $relativeBinaryPath)).Hash
            $expectedBaselineHash = if ($relativeBinaryPath -ceq $productionAssembly) { $binaryRecord.Hash } else { $candidateFileHash }
            if ($baselineFileHash -ne $expectedBaselineHash -or
                ($relativeBinaryPath -ceq $productionAssembly -and $candidateFileHash -ne $binaryHash) -or
                ($relativeBinaryPath -ceq $benchmarkAssembly -and $candidateFileHash -ne $benchmarkHash)) {
                throw "$binaryCheckDescription binary checksum differs: $relativeBinaryPath"
            }
            [pscustomobject]@{ Path = $relativeBinaryPath; CandidateSHA256 = $candidateFileHash; BaselineSHA256 = $baselineFileHash }
        }
        $binaryCheckFileName = if ($Scenario -eq 'FocusRejectedAdmission') { 'focus-rejected-binary-checks.csv' } elseif ($Scenario -eq 'ContainsWindow') { 'contains-window-binary-checks.csv' } elseif ($Scenario -eq 'LayoutCallbackCache') { 'layout-callback-binary-checks.csv' } elseif ($Scenario -eq 'ArrangeFailureBookkeeping') { 'bookkeeping-binary-checks.csv' } elseif ($Scenario -eq 'TilingOverlayRendererRecovery') { 'recovery-binary-checks.csv' } elseif ($Scenario -eq 'TilingWindowLifetime') { 'lifetime-binary-checks.csv' } elseif ($Scenario -eq 'DiscoveryWindowSnapshot') { 'discovery-window-snapshot-binary-checks.csv' } elseif ($Scenario -eq 'RefreshWindowSnapshot') { 'refresh-window-snapshot-binary-checks.csv' } elseif ($Scenario -eq 'TransitionCompletion') { 'transition-completion-binary-checks.csv' } elseif ($Scenario -eq 'RuntimeStateCount') { 'runtime-state-count-binary-checks.csv' } else { 'callback-binary-checks.csv' }
        $overlayWindowCallbackBinaryChecks | Export-Csv -NoTypeInformation (Join-Path $experimentRoot $binaryCheckFileName)
    }
    Get-ChildItem -LiteralPath "$experimentRoot/baseline" -File | Get-FileHash |
        Select-Object Path, Hash | Export-Csv -NoTypeInformation "$experimentRoot/baseline-binaries.csv"
}
Get-ChildItem -LiteralPath $binaryRoot -File | Get-FileHash |
    Select-Object Path, Hash | Export-Csv -NoTypeInformation "$experimentRoot/binaries.csv"
$previousTiering = $env:DOTNET_TieredCompilation
$previousGenericPreviewCase = $env:FWM_PERF_GENERIC_CASE
try {
    $env:DOTNET_TieredCompilation = '0'
    $env:FWM_PERF_GENERIC_CASE = $normalizedGenericPreviewCase
    $rows = foreach ($run in 1..5) {
        $variants = if (!$isComparison) { @('candidate') } elseif ($run % 2) { @('baseline', 'candidate') } else { @('candidate', 'baseline') }
        foreach ($variant in $variants) {
            $runBinaryRoot = if ($variant -eq 'baseline') { "$experimentRoot/baseline" } else { $binaryRoot }
            $runId = if (!$isComparison) { "run-$run" } else { "$variant-$run" }
            $filter = if ($Scenario -eq 'Mica') { 'FullyQualifiedName~FauxMicaProviderTest.UnchangedSolidColor|FullyQualifiedName~FauxMicaProviderTest.FailedCaptureRetries' } elseif ($Scenario -eq 'CoordinatorNonempty') { 'FullyQualifiedName~AlgorithmicLayoutCoordinatorTest.NonemptyCleanupCounterScenario' } elseif ($Scenario -eq 'CoordinatorDeadlines') { 'FullyQualifiedName~AlgorithmicLayoutCoordinatorTest.DeadlineCleanupCounterScenario' } else { 'FullyQualifiedName~AlgorithmicLayoutCoordinatorTest.EmptyCleanupCounterScenario' }
            if ($Scenario -eq 'SettingsDistinct') { $filter = 'FullyQualifiedName~ConcurrentSettingsTest.DistinctSettingsCounterScenario' }
            if ($Scenario -eq 'SettingsDispose') { $filter = 'FullyQualifiedName=FancyWM.Tests.Models.SettingsViewModelTest.SettingsDisposeCounterScenario' }
            if ($Scenario -eq 'StartupWindowClose') { $filter = 'FullyQualifiedName=FancyWM.Tests.Models.SettingsViewModelTest.StartupWindowCloseCounterScenario' }
            if ($Scenario -eq 'SettingsWindowClose') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.SettingsWindowCloseTest.SettingsWindowCloseCounterScenario' }
            if ($Scenario -eq 'OverlayHostClose') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.OverlayHostCloseTest.OverlayHostCloseCounterScenario' }
            if ($Scenario -eq 'OverlayWindowClose') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.OverlayWindowCloseTest.OverlayWindowCloseCounterScenario' }
            if ($Scenario -eq 'OverlayWindowCallbacks') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.OverlayWindowCloseTest.OverlayWindowCallbacksCounterScenario' }
            if ($Scenario -eq 'TilingOverlayRendererDispose') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.TilingOverlayRendererDisposeTest.TilingOverlayRendererDisposeCounterScenario' }
            if ($Scenario -eq 'TilingOverlayRendererResources') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.OverlayUpdateTest.ResourceCallbackCounterScenario' }
            if ($Scenario -eq 'TilingOverlayRendererRemoval') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.OverlayUpdateTest.OverlayRemovalCounterScenario' }
            if ($Scenario -eq 'TilingOverlayRendererCreation') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.OverlayUpdateTest.OverlayCreationCounterScenario' }
            if ($Scenario -eq 'TilingOverlayRendererRecovery') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.OverlayUpdateTest.OverlayRecoveryCounterScenario' }
            if ($Scenario -eq 'TilingWindowLifetime') { $filter = 'FullyQualifiedName=FancyWM.Tests.Models.TilingWindowLifetimeTest.TilingWindowLifetimeCounterScenario' }
            if ($Scenario -eq 'SettingsRuntime') { $filter = 'FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.SettingsRuntimeCounterScenario' }
            if ($Scenario -eq 'ArrangeFailureBookkeeping') { $filter = 'FullyQualifiedName=FancyWM.Tests.AlgorithmicLayouts.TilingServiceAlgorithmicIntegrationTest.ArrangeFailureBookkeepingCounterScenario' }
            if ($Scenario -eq 'ThemeReload') { $filter = 'FullyQualifiedName~ThemeEngineManagerTest.ThemeReloadCounterScenario' }
            if ($Scenario -eq 'PreviewCacheManaged') { $filter = 'FullyQualifiedName~MasterSatelliteDropControllerTest.PreviewCacheManagedCounterScenario' }
            if ($Scenario -eq 'ThemeLocalImage') { $filter = 'FullyQualifiedName~UrlImageLoadingTest.LocalImageCounterScenario' }
            if ($Scenario -eq 'HookReplacement') { $filter = 'FullyQualifiedName~HookRegistrationPolicyTest.HookReplacementCounterScenario' }
            if ($Scenario -eq 'HookLifetime') { $filter = 'FullyQualifiedName~LowLevelHookLifetimeTest.HookLifetimeCounterScenario' }
            if ($Scenario -eq 'HookDisposal') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.LowLevelHookLifetimeTest.HookDisposalCounterScenario' }
            if ($Scenario -eq 'LowLevelPatternLifetime') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.LowLevelKeyPatternListenerTest.LowLevelPatternLifetimeCounterScenario' }
            if ($Scenario -eq 'DelayedCommandLifetime') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.MainWindowLifetimeTest.DelayedCommandLifetimeCounterScenario' }
            if ($Scenario -eq 'VirtualDesktopCallbackLifetime') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.MainWindowLifetimeTest.VirtualDesktopCallbackLifetimeCounterScenario' }
            if ($Scenario -eq 'VirtualDesktopRemovalLifetime') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.MainWindowLifetimeTest.VirtualDesktopRemovedLifetimeCounterScenario' }
            if ($Scenario -eq 'TilingNotificationLifetime') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.MainWindowLifetimeTest.TilingNotificationLifetimeCounterScenario' }
            if ($Scenario -eq 'DirectHotkeyLifetime') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.MainWindowLifetimeTest.DirectHotkeyLifetimeCounterScenario' }
            if ($Scenario -eq 'MicaCallbackLifetime') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.MainWindowLifetimeTest.MicaCallbackLifetimeCounterScenario' }
            if ($Scenario -eq 'LoadedUpdateCheckLifetime') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.MainWindowLifetimeTest.UpdateCheckLoopLifetimeCounterScenario' }
            if ($Scenario -eq 'DisplayRemovalGc') { $filter = 'FullyQualifiedName~MultiDisplayTilingServiceIntegrationTest.DisplayRemovalGarbageCollectionCounterScenario' }
            if ($Scenario -eq 'SettingsStrings') { $filter = 'FullyQualifiedName~StringsListBoxTest.StringsListBoxCounterScenario' }
            if ($Scenario -eq 'OverlayMaterialization') { $filter = 'FullyQualifiedName~OverlayMaterializationTest.NonHitTestableOverlayMaterializationCounterScenario' }
            if ($Scenario -eq 'ActionBarMaterialization') { $filter = 'FullyQualifiedName~ActionBarMaterializationTest.ActionBarMaterializationCounterScenario' }
            if ($Scenario -eq 'BindingListener') { $filter = 'FullyQualifiedName~BindingErrorListenerTest.BindingListenerCounterScenario' }
            if ($Scenario -eq 'MainWindowLifetime') { $filter = 'FullyQualifiedName~MainWindowLifetimeTest.MainWindowLifetimeCounterScenario' }
            if ($Scenario -eq 'ConstructionLifetime') { $filter = 'FullyQualifiedName~MainWindowLifetimeTest.ConstructionLifetimeCounterScenario' }
            if ($Scenario -eq 'AppShutdown') { $filter = 'FullyQualifiedName~AppLifetimeTest.AppShutdownCounterScenario' }
            if ($Scenario -eq 'AppAsyncShutdown') { $filter = 'FullyQualifiedName~AppLifetimeTest.AppAsyncShutdownCounterScenario' }
            if ($Scenario -eq 'LayoutCadence') { $filter = 'FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.CadenceCounterScenario' }
            if ($Scenario -eq 'KeyPressDisplay') { $filter = 'FullyQualifiedName~KeyPressBoxTest.KeyPressDisplayCounterScenario' }
            if ($Scenario -eq 'SvgMaterialization') { $filter = 'FullyQualifiedName~SvgIconMaterializationTest.SvgIconMaterializationCounterScenario' }
            if ($Scenario -eq 'CursorInput') { $filter = 'FullyQualifiedName~OverlayCursorInputTest.CursorInputCounterScenario' }
            if ($Scenario -eq 'GenericPreview') { $filter = 'FullyQualifiedName~GenericPreviewTraversalTest.GenericPreviewCounterScenario' }
            if ($Scenario -eq 'AnimationFrame') { $filter = 'FullyQualifiedName~AnimationThreadFrameTest.AnimationFrameCounterScenario' }
            if ($Scenario -eq 'LayoutCallbackCache') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.LayoutInvalidationQueueTest.LayoutCallbackCounterScenario' }
            if ($Scenario -eq 'ContainsWindow') { $filter = 'FullyQualifiedName=FancyWM.Tests.AlgorithmicLayouts.MasterSatelliteContainsWindowTest.ContainsWindowCounterScenario' }
            if ($Scenario -eq 'HotkeyCallback') { $filter = 'FullyQualifiedName~LowLevelHotkeyTest.HotkeyCallbackCounterScenario' }
            if ($Scenario -eq 'TreeStateLookup') { $filter = 'FullyQualifiedName~TilingWorkspaceStateTest.TreeStateLookupCounterScenario' }
            if ($Scenario -eq 'WindowStateLookup') { $filter = 'FullyQualifiedName~TilingWorkspaceStateTest.WindowStateLookupCounterScenario' }
            if ($Scenario -eq 'DisplayManageability') { $filter = 'FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.CanManageDisplayCounterScenario' }
            if ($Scenario -eq 'DiscoveryWindowSnapshot') { $filter = 'FullyQualifiedName=FancyWM.Tests.AlgorithmicLayouts.TilingServiceAlgorithmicIntegrationTest.DiscoveryWindowSnapshotCounterScenario' }
            if ($Scenario -eq 'RefreshWindowSnapshot') { $filter = 'FullyQualifiedName=FancyWM.Tests.AlgorithmicLayouts.TilingServiceAlgorithmicIntegrationTest.RefreshWindowSnapshotCounterScenario' }
            if ($Scenario -eq 'TransitionCompletion') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.TasksTest.TransitionCompletionCounterScenario' }
            if ($Scenario -eq 'RuntimeStateCount') { $filter = 'FullyQualifiedName=FancyWM.Tests.AlgorithmicLayouts.TilingServiceAlgorithmicIntegrationTest.RuntimeStateCountCounterScenario' }
            if ($Scenario -eq 'PanelIndex') { $filter = 'FullyQualifiedName~PanelNodeIndexTest.PanelNodeIndexCounterScenario' }
            if ($Scenario -eq 'EmbedIndex') { $filter = 'FullyQualifiedName~TilingNodeEmbedTest.TilingNodeEmbedCounterScenario' }
            if ($Scenario -eq 'SplitPanelMeasure') { $filter = 'FullyQualifiedName~SplitPanelMeasureTest.SplitPanelMeasureCounterScenario' }
            if ($Scenario -eq 'StackPanelMeasure') { $filter = 'FullyQualifiedName~StackPanelMeasureTest.StackPanelMeasureCounterScenario' }
            if ($Scenario -eq 'PlaceholderCleanup') { $filter = 'FullyQualifiedName~PanelNodeRemovePlaceholdersTest.PanelNodeRemovePlaceholdersCounterScenario' }
            if ($Scenario -eq 'WorkspaceInvariant') { $filter = 'FullyQualifiedName~MasterSatelliteDiagnosticReuseTest.WorkspaceInvariantCounterScenario' }
            if ($Scenario -eq 'TransitionDispatch') { $filter = 'FullyQualifiedName~TransitionTargetGroupTest.TransitionDispatchCounterScenario' }
            if ($Scenario -eq 'ReleaseChecker') { $filter = 'FullyQualifiedName~ReleaseCheckerTest.FactoryCreatedSingletonClosesItsClientAcrossOneHundredProviderLifetimes' }
            if ($Scenario -eq 'PostMoveTimer') { $filter = 'FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.PostMoveTimerCounterScenario' }
            if ($Scenario -eq 'PreviewCache') { $filter = 'FullyQualifiedName~MasterSatelliteDropControllerTest.PreviewCacheCounterScenario' }
            if ($Scenario -eq 'FocusFallback') { $filter = 'FullyQualifiedName~FocusHelperTest.FocusFallbackCounterScenario' }
            if ($Scenario -eq 'FocusSupersession') { $filter = 'FullyQualifiedName~FocusHelperTest.FocusSupersessionCounterScenario' }
            if ($Scenario -eq 'FocusBounded') { $filter = 'FullyQualifiedName~FocusHelperTest.FocusBoundedCounterScenario' }
            if ($Scenario -eq 'FocusRejectedAdmission') { $filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.FocusHelperTest.FocusRejectedAdmissionCounterScenario' }
            if ($Scenario -eq 'ClockBoost') { $filter = 'FullyQualifiedName~AnimationThreadClockBoostTest.AnimationClockBoostCounterScenario' }
            if ($Scenario -eq 'AnimationShutdown') { $filter = 'FullyQualifiedName~AnimationThreadClockBoostTest.AnimationShutdownCounterScenario' }
            if ($Scenario -eq 'DependentShutdown') { $filter = 'FullyQualifiedName~MainWindowLifetimeTest.DependentShutdownCounterScenario' }
            if ($Scenario -eq 'WindowDraggerLifetime') { $filter = 'FullyQualifiedName~WindowDraggerTest.WindowDraggerLifetimeCounterScenario' }
            if ($Scenario -eq 'ModifierDragLifetime') { $filter = 'FullyQualifiedName~ModifierWindowMoverTest.ModifierWindowMoverLifetimeCounterScenario' }
            if ($Scenario -eq 'IconDiscovery') { $filter = 'FullyQualifiedName~WindowIconCacheTest.IconDiscoveryCounterScenario' }
            if ($Scenario -eq 'SequenceHash') { $filter = 'FullyQualifiedName~SequenceComparerTest.SequenceHashCounterScenario' }
            if ($Scenario -eq 'ResizeTraversal') { $filter = 'FullyQualifiedName~ResizeTraversalTest.ResizeTraversalCounterScenario' }
            if ($Scenario -eq 'DiagnosticReuse') { $filter = 'FullyQualifiedName~MasterSatelliteDiagnosticReuseTest.DiagnosticReuseCounterScenario' }
            if ($Scenario -eq 'DisabledLogging') { $filter = 'FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.DisabledLoggingCounterScenario' }
            if ($Scenario -eq 'OwnershipTimer') { $filter = 'FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.IncomingOwnershipTimerCounterScenario' }
            if ($Scenario -eq 'TransitionRounded') { $filter = 'FullyQualifiedName~TransitionTargetGroupTest.RoundedTransitionCounterScenario' }
            if ($Scenario -eq 'OverlayUpdates') { $filter = 'FullyQualifiedName~OverlayUpdateTest.OverlayUpdatesCounterScenario' }
            if ($Scenario -eq 'OverlayAnimation') { $filter = 'FullyQualifiedName~OverlayAnimationTest.OverlayAnimationCounterScenario' }
            dotnet vstest "$runBinaryRoot/$benchmarkAssembly" "/TestCaseFilter:$filter" "/Logger:trx;LogFileName=$runId.trx" "/ResultsDirectory:$experimentRoot" *> "$experimentRoot/$runId.log"
            if ($LASTEXITCODE) { throw "Counter harness failed: $runId" }
            [xml]$testRun = Get-Content "$experimentRoot/$runId.trx"
            $counters = $testRun.SelectSingleNode('//*[local-name()="Counters"]')
            $expectedTests = if ($Scenario -eq 'Mica') { 2 } else { 1 }
            if ([int]$counters.passed -ne $expectedTests) { throw "Expected $expectedTests scenarios: $runId" }
            if ($Scenario -in @('TilingOverlayRendererRecovery','TilingWindowLifetime','RefreshWindowSnapshot','LayoutCallbackCache','ContainsWindow','FocusRejectedAdmission','TransitionCompletion','RuntimeStateCount')) {
                if (@($testRun.SelectNodes('//*[local-name()="Counters"]')).Count -ne 1 -or
                    $counters.total -cne '1' -or $counters.executed -cne '1' -or $counters.passed -cne '1') {
                    throw "TilingOverlayRenderer recovery requires one executed passing result: $runId"
                }
                foreach ($counterName in @('failed','error','timeout','aborted','inconclusive','passedButRunAborted','notRunnable','notExecuted','disconnected','warning','inProgress','pending')) {
                    if ($counters.GetAttribute($counterName) -cne '0') { throw "Unexpected recovery TRX outcome: $runId/$counterName" }
                }
                if (($counters.HasAttribute('completed') -and $counters.GetAttribute('completed') -cne '0') -or
                    $testRun.SelectSingleNode('//*[local-name()="ResultSummary"]').GetAttribute('outcome') -cne 'Completed') {
                    throw "Unexpected recovery TRX summary: $runId"
                }
                $recoveryResults = @($testRun.SelectNodes('//*[local-name()="UnitTestResult"]'))
                $recoveryDefinitions = @($testRun.SelectNodes('//*[local-name()="UnitTest"]'))
                $recoveryMethods = @($testRun.SelectNodes('//*[local-name()="TestMethod"]'))
                $recoveryOutputs = @($testRun.SelectNodes('//*[local-name()="StdOut"]'))
                $recoveryLeaf = if ($Scenario -eq 'FocusRejectedAdmission') { 'FocusRejectedAdmissionCounterScenario' } elseif ($Scenario -eq 'ContainsWindow') { 'ContainsWindowCounterScenario' } elseif ($Scenario -eq 'LayoutCallbackCache') { 'LayoutCallbackCounterScenario' } elseif ($Scenario -eq 'TilingWindowLifetime') { 'TilingWindowLifetimeCounterScenario' } elseif ($Scenario -eq 'RefreshWindowSnapshot') { 'RefreshWindowSnapshotCounterScenario' } elseif ($Scenario -eq 'TransitionCompletion') { 'TransitionCompletionCounterScenario' } elseif ($Scenario -eq 'RuntimeStateCount') { 'RuntimeStateCountCounterScenario' } else { 'OverlayRecoveryCounterScenario' }
                $recoveryClass = if ($Scenario -eq 'FocusRejectedAdmission') { 'FancyWM.Tests.Utilities.FocusHelperTest' } elseif ($Scenario -eq 'ContainsWindow') { 'FancyWM.Tests.AlgorithmicLayouts.MasterSatelliteContainsWindowTest' } elseif ($Scenario -eq 'LayoutCallbackCache') { 'FancyWM.Tests.Utilities.LayoutInvalidationQueueTest' } elseif ($Scenario -eq 'TilingWindowLifetime') { 'FancyWM.Tests.Models.TilingWindowLifetimeTest' } elseif ($Scenario -eq 'RefreshWindowSnapshot') { 'FancyWM.Tests.AlgorithmicLayouts.TilingServiceAlgorithmicIntegrationTest' } elseif ($Scenario -eq 'TransitionCompletion') { 'FancyWM.Tests.Utilities.TasksTest' } elseif ($Scenario -eq 'RuntimeStateCount') { 'FancyWM.Tests.AlgorithmicLayouts.TilingServiceAlgorithmicIntegrationTest' } else { 'FancyWM.Tests.Utilities.OverlayUpdateTest' }
                if ($recoveryResults.Count -ne 1 -or $recoveryDefinitions.Count -ne 1 -or $recoveryMethods.Count -ne 1 -or $recoveryOutputs.Count -ne 1 -or
                    $recoveryResults[0].outcome -cne 'Passed' -or $recoveryResults[0].testName -cne $recoveryLeaf -or
                    $recoveryDefinitions[0].name -cne $recoveryLeaf -or $recoveryDefinitions[0].id -cne $recoveryResults[0].testId -or
                    $recoveryDefinitions[0].Execution.id -cne $recoveryResults[0].executionId -or
                    $recoveryMethods[0].name -cne $recoveryLeaf -or $recoveryMethods[0].className -cne $recoveryClass -or
                    $recoveryMethods[0].adapterTypeName -cne 'executor://mstestadapter/v2') {
                    throw "TilingOverlayRenderer recovery TRX leaf/FQN differs: $runId"
                }
                $recoveryExpectedAssembly = [System.IO.Path]::GetFullPath("$runBinaryRoot/$benchmarkAssembly")
                foreach ($assemblyPath in @($recoveryMethods[0].codeBase,$recoveryDefinitions[0].storage)) {
                    if ([string]::IsNullOrWhiteSpace($assemblyPath) -or ![System.IO.Path]::IsPathRooted($assemblyPath) -or
                        ![System.IO.Path]::GetFullPath($assemblyPath).Equals($recoveryExpectedAssembly,[System.StringComparison]::OrdinalIgnoreCase)) {
                        throw "TilingOverlayRenderer recovery TRX assembly path differs: $runId"
                    }
                }
                $recoveryProductionHash = if ($variant -ceq 'baseline') { $binaryRecord.Hash } else { $binaryHash }
                if ((Get-FileHash -LiteralPath $recoveryExpectedAssembly).Hash -cne $benchmarkHash -or
                    (Get-FileHash -LiteralPath "$runBinaryRoot/$productionAssembly").Hash -cne $recoveryProductionHash) {
                    throw "TilingOverlayRenderer recovery executed assembly checksum differs: $runId"
                }
                $recoveryTimes = @($testRun.SelectNodes('//*[local-name()="Times"]'))
                $recoveryTrxId = [string]$testRun.DocumentElement.id
                $recoveryExecutionId = [string]$recoveryResults[0].executionId
                if ($recoveryTimes.Count -ne 1 -or [string]::IsNullOrWhiteSpace($recoveryTrxId) -or [string]::IsNullOrWhiteSpace($recoveryExecutionId) -or
                    !$overlayRecoveryRunIds.Add($recoveryTrxId) -or !$overlayRecoveryExecutionIds.Add($recoveryExecutionId)) {
                    throw "TilingOverlayRenderer recovery missing or duplicate process identity: $runId"
                }
                $recoveryStart = [DateTimeOffset]::Parse($recoveryTimes[0].start,[Globalization.CultureInfo]::InvariantCulture)
                $recoveryFinish = [DateTimeOffset]::Parse($recoveryTimes[0].finish,[Globalization.CultureInfo]::InvariantCulture)
                $recoveryTestStart = [DateTimeOffset]::Parse($recoveryResults[0].startTime,[Globalization.CultureInfo]::InvariantCulture)
                $recoveryTestFinish = [DateTimeOffset]::Parse($recoveryResults[0].endTime,[Globalization.CultureInfo]::InvariantCulture)
                if ($recoveryStart -ge $recoveryFinish -or $recoveryStart -gt $recoveryTestStart -or
                    $recoveryTestStart -gt $recoveryTestFinish -or $recoveryTestFinish -gt $recoveryFinish) {
                    throw "TilingOverlayRenderer recovery process/test time interval differs: $runId"
                }
                $overlayRecoveryProcesses.Add([pscustomobject]@{Run=$runId;TrxId=$recoveryTrxId;ExecutionId=$recoveryExecutionId;Start=$recoveryStart;Finish=$recoveryFinish;TrxSHA256=(Get-FileHash -LiteralPath "$experimentRoot/$runId.trx").Hash})
            }
            if ($Scenario -eq 'ArrangeFailureBookkeeping') {
                if ($counters.total -cne '1' -or $counters.executed -cne '1' -or $counters.passed -cne '1') {
                    throw "ArrangeFailureBookkeeping requires one executed passing result: $runId"
                }
                foreach ($counterName in @('failed','error','timeout','aborted','inconclusive','passedButRunAborted','notRunnable','notExecuted','disconnected','warning','inProgress','pending')) {
                    if ($counters.$counterName -cne '0') { throw "Unexpected ArrangeFailureBookkeeping TRX outcome: $runId/$counterName" }
                }
                $bookkeepingResults = @($testRun.SelectNodes('//*[local-name()="UnitTestResult"]'))
                $bookkeepingDefinitions = @($testRun.SelectNodes('//*[local-name()="UnitTest"]'))
                $bookkeepingMethods = @($testRun.SelectNodes('//*[local-name()="TestMethod"]'))
                $bookkeepingOutputs = @($testRun.SelectNodes('//*[local-name()="StdOut"]'))
                $bookkeepingLeaf = 'ArrangeFailureBookkeepingCounterScenario'
                if ($bookkeepingResults.Count -ne 1 -or $bookkeepingDefinitions.Count -ne 1 -or
                    $bookkeepingMethods.Count -ne 1 -or $bookkeepingOutputs.Count -ne 1 -or
                    $bookkeepingResults[0].outcome -cne 'Passed' -or $bookkeepingResults[0].testName -cne $bookkeepingLeaf -or
                    $bookkeepingDefinitions[0].name -cne $bookkeepingLeaf -or
                    $bookkeepingDefinitions[0].id -cne $bookkeepingResults[0].testId -or
                    $bookkeepingMethods[0].name -cne $bookkeepingLeaf -or
                    $bookkeepingMethods[0].className -cne 'FancyWM.Tests.AlgorithmicLayouts.TilingServiceAlgorithmicIntegrationTest') {
                    throw "ArrangeFailureBookkeeping TRX leaf/FQN differs: $runId"
                }
                $bookkeepingExpectedCodeBase = [System.IO.Path]::GetFullPath("$runBinaryRoot/$benchmarkAssembly")
                foreach ($codeBase in @($bookkeepingMethods[0].codeBase, $bookkeepingDefinitions[0].storage)) {
                    if ([string]::IsNullOrWhiteSpace($codeBase) -or ![System.IO.Path]::IsPathRooted($codeBase) -or
                        ![System.IO.Path]::GetFullPath($codeBase).Equals($bookkeepingExpectedCodeBase, [System.StringComparison]::OrdinalIgnoreCase)) {
                        throw "ArrangeFailureBookkeeping TRX assembly path differs: $runId"
                    }
                }
                if ((Get-FileHash -LiteralPath $bookkeepingExpectedCodeBase).Hash -cne $benchmarkHash) {
                    throw "ArrangeFailureBookkeeping executed fixture checksum differs: $runId"
                }
                $bookkeepingTimes = @($testRun.SelectNodes('//*[local-name()="Times"]'))
                if ($bookkeepingTimes.Count -ne 1) { throw "ArrangeFailureBookkeeping process times missing: $runId" }
                $bookkeepingStart = [DateTimeOffset]::Parse($bookkeepingTimes[0].start, [Globalization.CultureInfo]::InvariantCulture)
                $bookkeepingFinish = [DateTimeOffset]::Parse($bookkeepingTimes[0].finish, [Globalization.CultureInfo]::InvariantCulture)
                if ($bookkeepingFinish -le $bookkeepingStart) { throw "ArrangeFailureBookkeeping process times reversed: $runId" }
                $arrangeFailureBookkeepingProcessTimes.Add([pscustomobject]@{ RunId = $runId; Start = $bookkeepingStart; Finish = $bookkeepingFinish })
            }
            $output = ($testRun.SelectNodes('//*[local-name()="StdOut"]') | ForEach-Object { $_.InnerText }) -join "`n"
            $pattern = if ($Scenario -eq 'Mica') { 'MICA_COUNTER,([^,\r\n]+),([^,\r\n]+),(\d+)' } elseif ($Scenario -eq 'CoordinatorNonempty') { 'PERFCOUNTER (coordinator-nonempty) ([\w-]+) (\d+)' } elseif ($Scenario -eq 'CoordinatorDeadlines') { 'PERFCOUNTER (coordinator-deadlines) (calls|cycles) (\d+)' } else { 'PERFCOUNTER (coordinator-empty) ([\w-]+) (\d+)' }
            if ($Scenario -eq 'SettingsDistinct') { $pattern = 'PERFCOUNTER (settings-distinct) (writes|updates) (\d+)' }
            if ($Scenario -eq 'SettingsDispose') { $pattern = '(?m)^PERFCOUNTER (settings-dispose-(?:normal|subscription-failure|logger-failure)) (cycles|subscription-dispose-attempts|released-subscriptions|flush-attempts|primary-outcomes|late-notifications|late-keybinding-mutations|repeat-dispose-errors|active-subscriptions|log-attempts) ([0-9]+)\r?$' }
            if ($Scenario -eq 'StartupWindowClose') { $pattern = '(?m)^PERFCOUNTER (startup-window-close) (cycles|subscription-dispose-attempts|released-subscriptions|flush-attempts|primary-outcomes|closed-callbacks|supplemental-errors|repeat-close-errors|nonzero-handles|active-subscriptions) ([0-9]+)\r?$' }
            if ($Scenario -eq 'SettingsWindowClose') { $pattern = '(?m)^PERFCOUNTER (settings-window-close-(?:normal|viewmodel-failure|combined-failure)) (cycles|subscription-dispose-attempts|released-subscriptions|flush-attempts|primary-outcomes|closed-callbacks|supplemental-errors|retained-logical-focus|collection-posts|repeat-close-errors|nonzero-handles|active-subscriptions) ([0-9]+)\r?$' }
            if ($Scenario -eq 'OverlayHostClose') { $pattern = '(?m)^PERFCOUNTER (normal|cursor-failure|nonhit-close-failure) (lifetimes|invalidations|cursor-attempts|cursor-releases|refresh-attempts|refresh-releases|subscription-attempts|subscription-releases|dispatch-attempts|nonhit-close-attempts|nonhit-close-releases|hit-close-attempts|hit-close-releases|primary-outcomes|supplemental-errors|repeat-actions|active-owners) ([0-9]+)\r?$' }
            if ($Scenario -eq 'OverlayWindowClose') { $pattern = '(?m)^PERFCOUNTER (normal|base-failure|settings-failure) (lifetimes|base-attempts|settings-attempts|settings-releases|workarea-attempts|workarea-releases|scaling-attempts|scaling-releases|content-attempts|content-releases|primary-outcomes|supplemental-errors|active-owners) ([0-9]+)\r?$' }
            if ($Scenario -eq 'OverlayWindowCallbacks') { $pattern = '(?m)^PERFCOUNTER overlay-window-callbacks-((?:settings|scaling|workarea)-(?:active|queued-before-close|captured-after-close)) (lifetimes|posts|drained-callbacks|transform-applies|resource-applies|owner-releases|pending-callbacks) ([0-9]+)\r?$' }
            if ($Scenario -eq 'TilingOverlayRendererDispose') { $pattern = '(?m)^PERFCOUNTER (normal|overlay-failure|viewmodel-failure) (lifetimes|capture-attempts|drag-attempts|drag-releases|overlay-attempts|overlay-releases|viewmodel-attempts|viewmodel-releases|display-attempts|display-releases|subscriptions-attempts|subscriptions-releases|invalidate-attempts|invalidate-releases|events-attempts|events-releases|primary-outcomes|supplemental-errors|repeat-actions|active-owners) ([0-9]+)\r?$' }
            if ($Scenario -eq 'TilingOverlayRendererResources') { $pattern = '(?m)^PERFCOUNTER overlay-resources-(active|queued-before-dispose|captured-after-dispose) (lifetimes|posts|drained-callbacks|scaling-reads|resource-writes|subscription-releases|pending-callbacks) ([0-9]+)\r?$' }
            if ($Scenario -eq 'TilingOverlayRendererRemoval') { $pattern = '(?m)^PERFCOUNTER overlay-removal-(normal|notification-failure) (cycles|removal-notifications|primary-outcomes|detached-models|cursor-subscriptions-after-invalidation|node-references-after-invalidation|command-references-after-invalidation|models-needing-fixture-cleanup|cursor-subscriptions-after-fixture-cleanup) ([0-9]+)\r?$' }
            if ($Scenario -eq 'TilingOverlayRendererCreation') { $pattern = '(?m)^PERFCOUNTER overlay-creation-(normal|title-failure) (cycles|title-reads|created-models|published-models|primary-outcomes|cursor-subscriptions-after-invalidation|window-subscriptions-after-invalidation|node-references-after-invalidation|models-needing-fixture-cleanup|cursor-subscriptions-after-fixture-cleanup|window-subscriptions-after-fixture-cleanup) ([0-9]+)\r?$' }
            if ($Scenario -eq 'TilingOverlayRendererRecovery') { $pattern = '(?m)^PERFCOUNTER overlay-recovery-(normal|removal-failure) (cycles|primary-outcomes|correct-requested-snapshot-cycles|cursor-subscriptions-after-retry|window-subscriptions-after-retry|stale-node-references-after-retry|stale-window-subscriptions-after-retry|cursor-subscriptions-after-cleanup|window-subscriptions-after-cleanup) (0|[1-9][0-9]*)\r?$' }
            if ($Scenario -eq 'TilingWindowLifetime') {
                $pattern = '(?m)^PERFCOUNTER tiling-window-lifetime-(normal|removal-failure|replacement-failure|removed-reentry) (' +
                    (($tilingWindowLifetimeUnits.Keys | ForEach-Object { [regex]::Escape($_) }) -join '|') + ') (0|[1-9][0-9]*)\r?$'
            }
            if ($Scenario -eq 'SettingsRuntime') { $pattern = 'PERFCOUNTER (settings-runtime-(?:enabled|disabled)) ([\w-]+) (\d+)' }
            if ($Scenario -eq 'ArrangeFailureBookkeeping') { $pattern = '(?m)^PERFCOUNTER (arrange-bookkeeping-(?:ordinary|ms)-(?:1|4|10)-(?:stable|pending)) (iterations|arranged-passes|allocated-bytes|handle-reads|minsize-reads|geometry-checks|geometry-checksum|identity-checks|revision-checks|revision-delta|fallback-notifications|placement-notifications|algorithmic-notifications|floating-windows|pending-notifications|new-window-count) ([0-9]+)\r?$' }
            if ($Scenario -eq 'ThemeReload') { $pattern = 'PERFCOUNTER (theme-reload) ([\w-]+) (\d+)' }
            if ($Scenario -eq 'PreviewCacheManaged') { $pattern = 'PERFCOUNTER (preview-managed-(?:1|4|10)) (allocated-bytes|minimum-reads|elapsed-ticks|timestamp-frequency|pointers|accepted-plans|plan-digest) ([0-9A-F]+)' }
            if ($Scenario -eq 'ThemeLocalImage') { $pattern = 'PERFCOUNTER (local-image-(?:16|2048|4096|lifetimes)) (conversion-ticks|conversion-bytes|first-pixels-ticks|cached-reads-ticks|cached-reads-bytes|timestamp-frequency|reuse-reads|pixels|pixel-digest|retained-values-brushes|cycles) ([0-9A-F]+)' }
            if ($Scenario -eq 'HookReplacement') { $pattern = 'PERFCOUNTER (hook-replacement) (cycles|install-calls|preserved-on-failure|zero-unhook-calls|owned-unhook-calls|fixture-normal-exit-cleanups|maximum-fake-live-handles|remaining-fake-live-handles|retry-clock-reads) (\d+)' }
            if ($Scenario -eq 'HookLifetime') { $pattern = '(?m)^PERFCOUNTER (hook-lifetime-(?:keyboard|mouse)) (cycles|installed-hooks|timer-creations|timer-cleanup-attempts|exceptional-unhook-attempts|exceptional-remaining-hooks|original-loop-errors|normal-unhook-attempts|normal-completions|duplicate-unhooks) (\d+)\r?$' }
            if ($Scenario -eq 'HookDisposal') { $pattern = '(?m)^PERFCOUNTER (hook-disposal-(?:keyboard|mouse)) (cycles|quit-post-attempts|observed-post-failures|completion-successes|late-subscriber-callbacks|suppressed-late-input|completed-workers|live-workers-after-join) (\d+)\r?$' }
            if ($Scenario -eq 'LowLevelPatternLifetime') { $pattern = '(?m)^PERFCOUNTER (lowlevel-pattern-lifetime) ([\w-]+) (\d+)\r?$' }
            if ($Scenario -eq 'DelayedCommandLifetime') { $pattern = '(?m)^PERFCOUNTER (delayed-command-lifetime) (cycles|delay-attempts|active-actions|late-actions|cancellations|completed-runs) (\d+)\r?$' }
            if ($Scenario -eq 'VirtualDesktopCallbackLifetime') { $pattern = '(?m)^PERFCOUNTER (virtual-desktop-callback-lifetime) (cycles|active-state-writes|active-feature-probes|active-name-reads|active-toasts|late-state-writes|late-feature-probes|late-name-reads|late-toasts|completed-callbacks) (\d+)\r?$' }
            if ($Scenario -eq 'VirtualDesktopRemovalLifetime') { $pattern = '(?m)^PERFCOUNTER (virtual-desktop-removal-lifetime) (cycles|active-state-reads|active-clears|late-state-reads|late-clears|completed-callbacks) (\d+)\r?$' }
            if ($Scenario -eq 'TilingNotificationLifetime') { $pattern = '(?m)^PERFCOUNTER (tiling-notification-lifetime) (cycles|active-callbacks|late-callbacks|completed-invocations|owned-releases) (\d+)\r?$' }
            if ($Scenario -eq 'DirectHotkeyLifetime') { $pattern = '(?m)^PERFCOUNTER (direct-hotkey-lifetime) (cycles|active-executions|active-presentations|late-executions|late-presentations|completed-invocations|owned-releases) (\d+)\r?$' }
            if ($Scenario -eq 'MicaCallbackLifetime') { $pattern = '(?m)^PERFCOUNTER (mica-primary-color-callback-lifetime) (cycles|active-posts|active-applies|late-posts|late-applies|queued-before-close-posts|queued-after-close-applies|owned-releases|completed-callbacks) (\d+)\r?$' }
            if ($Scenario -eq 'LoadedUpdateCheckLifetime') { $pattern = '(?m)^PERFCOUNTER (loaded-update-check-lifetime) (cycles|active-posts|active-runs|burst-posts|burst-runs|late-posts|late-runs|completed-starts|owned-releases|pending-callbacks) (\d+)\r?$' }
            if ($Scenario -eq 'DisplayRemovalGc') { $pattern = 'PERFCOUNTER (display-removal-gc) (collection-requests|removed-owners|duplicate-events|cycles) (\d+)' }
            if ($Scenario -eq 'SettingsStrings') { $pattern = 'PERFCOUNTER (settings-strings-(?:1|2|10|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|presenter-replacements|source-updates|lost-focus-events|content-digest) ([0-9A-F]+)' }
            if ($Scenario -eq 'OverlayMaterialization') { $pattern = 'PERFCOUNTER (overlay-materialization-(?:1|10|25|50)) (constructor-bytes|constructor-ticks|layout-bytes|layout-ticks|timestamp-frequency|visuals-before-layout|visuals-after-layout|rectangles-before-layout|rectangles-after-layout|presenters-after-layout|vm-count|logical-before-layout|logical-after-layout|materialized-items-before-layout) (\d+)' }
            if ($Scenario -eq 'ActionBarMaterialization') { $pattern = 'PERFCOUNTER (actionbar-materialization-(?:1|10|25|50)) (constructor-bytes|constructor-ticks|layout-bytes|layout-ticks|timestamp-frequency|vm-count|(?:visuals|logical|buttons|svg-icons|active-paths|tooltip-content|context-menus|menu-items|shadow-effects|opacity-masks)-(?:before|after)-layout) (\d+)' }
            if ($Scenario -eq 'BindingListener') { $pattern = 'PERFCOUNTER (binding-listener) (cycles|live-notifications|late-notifications|retained-owners|final-retained-owners|remaining-subscriptions) (\d+)' }
            if ($Scenario -eq 'MainWindowLifetime') { $pattern = 'PERFCOUNTER (main-window-lifetime) (cycles|cleanup-attempts|remaining-owners|original-errors|duplicate-cleanups) (\d+)' }
            if ($Scenario -eq 'ConstructionLifetime') { $pattern = 'PERFCOUNTER (construction-lifetime) (cycles|cleanup-attempts|remaining-owners|original-errors|duplicate-cleanups) (\d+)' }
            if ($Scenario -eq 'AppShutdown') { $pattern = 'PERFCOUNTER (app-shutdown) (cycles|dispose-attempts|close-attempts|mouse-cleanups|original-errors) (\d+)' }
            if ($Scenario -eq 'AppAsyncShutdown') { $pattern = 'PERFCOUNTER (app-async-shutdown) (cycles|early-window-closes|early-shutdowns|dispose-attempts|close-attempts|mouse-cleanups|shutdowns) (\d+)' }
            if ($Scenario -eq 'LayoutCadence') { $pattern = 'PERFCOUNTER (layout-cadence) (cycles|trailing-layouts|cadence-delays|pending-delays) (\d+)' }
            if ($Scenario -eq 'KeyPressDisplay') { $pattern = 'KEYPRESSDISPLAY\|(key-press-display-(?:1|10|50))\|(created-controls|listener-creations|live-hook-subscribers|display-checksum|allocated-bytes|elapsed-ticks|timestamp-frequency)\|(\d+)' }
            if ($Scenario -eq 'SvgMaterialization') { $pattern = 'PERFCOUNTER (svg-(?:materialization-(?:1|10|25|50)|setter-probe)) (constructor-bytes|constructor-ticks|layout-bytes|layout-ticks|timestamp-frequency|svg-count|setter-paths|unique-geometries|frozen-geometries|unique-layout-transforms|frozen-layout-transforms|unique-render-transforms|frozen-render-transforms|pixel-digest|storage-field-available|stored-paths-before-first-getter|first-value-ticks|first-value-bytes|second-value-ticks|second-value-bytes) ([0-9A-F]+)' }
            if ($Scenario -eq 'CursorInput') { $pattern = 'PERFCOUNTER (overlay-cursor-(?:visible|hidden|closed)-(?:1|2|4)) (posts|cursor-reads|conversions|hit-tests|style-reads|style-writes|input-events) (\d+)' }
            if ($Scenario -eq 'GenericPreview') { $pattern = 'PERFCOUNTER (generic-preview-(?:flat|balanced|skewed)-(?:10|25|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|previews|minimum-reads|geometry-digest) ([0-9A-F]+)' }
            if ($Scenario -eq 'AnimationFrame') { $pattern = 'PERFCOUNTER (animation-frame-(?:1|4|10|25|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|updates|frames) (\d+)' }
            if ($Scenario -eq 'LayoutCallbackCache') { $pattern = '(?m)^PERFCOUNTER (layout-callback-(?:1|10|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|invalidations|posts|applies|eligibility-reads|errors|callback-instance-changes|cycles) (0|[1-9][0-9]*)\r?$' }
            if ($Scenario -eq 'ContainsWindow') { $pattern = '(?m)^PERFCOUNTER (contains-window-(?:master|first|last|absent)-(?:1|4|9)) (allocated-bytes|elapsed-ticks|timestamp-frequency|calls|matches|equality-calls|hash-calls) (0|[1-9][0-9]*)\r?$' }
            if ($Scenario -eq 'HotkeyCallback') { $pattern = 'PERFCOUNTER (hotkey-(?:quiet|matching)-(?:0|10|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|input-events|suppressed-events|emitted-events|p50-ticks|p95-ticks|p99-ticks|event-digest) ([0-9A-F]+)' }
            if ($Scenario -eq 'TreeStateLookup') { $pattern = 'PERFCOUNTER (tree-state-index-(?:1|4|10|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|lookups|matched-lookups) (\d+)' }
            if ($Scenario -eq 'WindowStateLookup') { $pattern = '(?m)^PERFCOUNTER (window-state-(?:vdm|tree)-(?:1|4|10|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|lookups|matched-lookups|resident-hits|probes|desktop-hash-reads) (\d+)\r?$' }
            if ($Scenario -eq 'DisplayManageability') { $pattern = '(?m)^PERFCOUNTER (display-manage-provider-(?:1|2|4)) (allocated-bytes|elapsed-ticks|timestamp-frequency|lookups|manageable-results|foreign-results|position-reads|snapshot-reads|equality-reads|bounds-reads|resize-reads|move-reads|pin-reads) (\d+)\r?$' }
            if ($Scenario -eq 'DiscoveryWindowSnapshot') { $pattern = '(?m)^PERFCOUNTER (discovery-window-snapshot-(?:0|1|10|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|passes|changed-passes|state-reads|position-reads|resize-reads|move-reads|pin-reads|desktop-snapshot-reads|display-snapshot-reads) (0|[1-9][0-9]*)\r?$' }
            if ($Scenario -eq 'RefreshWindowSnapshot') { $pattern = '(?m)^PERFCOUNTER (refresh-window-snapshot-(?:0|1|10|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|passes|state-reads|position-reads|resize-reads|move-reads|pin-reads|desktop-snapshot-reads|display-snapshot-reads|tracked-windows) (0|[1-9][0-9]*)\r?$' }
            if ($Scenario -eq 'TransitionCompletion') { $pattern = '(?m)^PERFCOUNTER (transition-completion-(?:success|cancelled|mixed)-(?:0|1|4|10|25|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|completions|input-tasks|cancelled-inputs) (0|[1-9][0-9]*)\r?$' }
            if ($Scenario -eq 'RuntimeStateCount') { $pattern = '(?m)^PERFCOUNTER (runtime-state-count-(?:own|mixed)-(?:0|1|4|10|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|queries|count-sum|equality-reads) (0|[1-9][0-9]*)\r?$' }
            if ($Scenario -eq 'PanelIndex') { $pattern = '(?m)^PERFCOUNTER (panel-index-(?:1|10|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|lookups|last-index-sum|missing-index-sum) (\d+)\r?$' }
            if ($Scenario -eq 'EmbedIndex') { $pattern = '(?m)^PERFCOUNTER (embed-index-(?:1|10|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|embeds|slot-index-sum|parent-links|registrations|ordered-window-sum) (\d+)\r?$' }
            if ($Scenario -eq 'SplitPanelMeasure') { $pattern = '(?m)^PERFCOUNTER (split-measure-(?:hidden|ordinary)-(?:1|10|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|measures|backing-children|published-width-sum|published-height-sum) ([0-9]+)\r?$' }
            if ($Scenario -eq 'StackPanelMeasure') { $pattern = '(?m)^PERFCOUNTER (stack-measure-(?:hidden|ordinary)-(?:1|10|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|measures|backing-children|published-width-sum|published-height-sum) ([0-9]+)\r?$' }
            if ($Scenario -eq 'PlaceholderCleanup') { $pattern = '(?m)^PERFCOUNTER (placeholder-cleanup-(?:split-absent|stack-absent|derived-absent|split-present|stack-present)-(?:1|10|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|operations|retained-children|parent-links|ordered-slot-sum|placeholder-windows-reads) ([0-9]+)\r?$' }
            if ($Scenario -eq 'WorkspaceInvariant') { $pattern = 'PERFCOUNTER (workspace-invariant-(?:1|4|10)) (allocated-bytes|child-reads|minsize-reads|rounds|geometry-digest) ([0-9A-F]+)' }
            if ($Scenario -eq 'TransitionDispatch') { $pattern = 'PERFCOUNTER (transition-dispatch-(?:1|4|10|25|50)) (allocated-bytes|elapsed-ticks|timestamp-frequency|position-writes|transitions) (\d+)' }
            if ($Scenario -eq 'ReleaseChecker') { $pattern = 'PERFCOUNTER (release-checker) (disposed-handlers|cycles) (\d+)' }
            if ($Scenario -eq 'PostMoveTimer') { $pattern = 'PERFCOUNTER (postmove-timer) (timer-instances|deferred-probes|cycles) (\d+)' }
            if ($Scenario -eq 'PreviewCache') { $pattern = 'PERFCOUNTER (preview-cache-(?:1|4|10)) (allocated-bytes|minimum-reads|elapsed-ticks|timestamp-frequency|pointers|accepted-plans|plan-digest) ([0-9A-F]+)' }
            if ($Scenario -eq 'FocusFallback') { $pattern = 'PERFCOUNTER (focus-fallback) (attachments|detachments|completed-workers|cycles) (\d+)' }
            if ($Scenario -eq 'FocusSupersession') { $pattern = 'PERFCOUNTER (focus-supersession) (cycles|old-focus|new-focus|alt|attachments|detachments|completed-workers|superseded-results) (\d+)' }
            if ($Scenario -eq 'FocusBounded') { $pattern = '(?m)^PERFCOUNTER (focus-bounded) ([\w-]+) (\d+)\r?$' }
            if ($Scenario -eq 'FocusRejectedAdmission') { $pattern = '(?m)^PERFCOUNTER (focus-rejected-(?:superseded|expired|stopped)) (allocated-bytes|elapsed-ticks|timestamp-frequency|calls|rejected|native-calls|worker-starts|wait-calls|current-preserved) (0|[1-9][0-9]*)\r?$' }
            if ($Scenario -eq 'IconDiscovery') { $pattern = 'PERFCOUNTER (icon-discovery-(?:1|10|25|50)) (ui-resolver-calls|resource-loads|identity-probes|binding-reads|applied-icons|retained-cache-entries) (\d+)' }
            if ($Scenario -eq 'SequenceHash') { $pattern = 'PERFCOUNTER (sequence-hash-(?:defaults|scaled)) (equality-calls|hash-calls|patterns) (\d+)' }
            if ($Scenario -eq 'OverlayUpdates') { $pattern = 'PERFCOUNTER (overlay-updates-(?:1|10|25|50)) (snapshot-enumerations|focus-enumerations|preview-notifications|allocated-bytes|windows|updates) (\d+)' }
            if ($Scenario -eq 'OverlayAnimation') { $pattern = 'PERFCOUNTER (overlay-animation-(?:hit|nonhit)) (allocated-bytes|updates) (\d+)' }
            if ($Scenario -eq 'ResizeTraversal') { $pattern = 'PERFCOUNTER (resize-depth-(?:1|10|25)) (node-visits|resize-calls) (\d+)' }
            if ($Scenario -eq 'DiagnosticReuse') { $pattern = 'PERFCOUNTER (diagnostic-reuse-(?:1|4|10)) (allocated-bytes|child-reads|rounds|geometry-checksum|geometry-digest) ([0-9A-F]+)' }
            if ($Scenario -eq 'DisabledLogging') { $pattern = 'PERFCOUNTER (disabled-logging-(?:1|4|10|25|50)) (diagnostic-handle-reads|target-count) (\d+)' }
            if ($Scenario -eq 'OwnershipTimer') { $pattern = 'PERFCOUNTER (ownership-timer) (timer-instances|cycles) (\d+)' }
            if ($Scenario -eq 'TransitionRounded') { $pattern = 'PERFCOUNTER (transition-rounded-(?:1|4|10|25|50)) (position-writes|position-reads|transitions) (\d+)' }
            if ($Scenario -eq 'ClockBoost') { $pattern = 'PERFCOUNTER (animation-clock-boost) (cycles|waits|updates|cancellations|enable-calls|disable-calls|remaining-references|zero-progress-updates) (\d+)' }
            if ($Scenario -eq 'AnimationShutdown') { $pattern = 'PERFCOUNTER (animation-shutdown) (cycles|accepted-jobs|waits|updates|queued-updates|cancellations|successful-jobs|enable-calls|disable-calls|remaining-references|worker-exits|late-rejections) (\d+)' }
            if ($Scenario -eq 'DependentShutdown') { $pattern = 'PERFCOUNTER (dependent-shutdown) (cycles|premature-cleanups|pending-completions|cleanup-attempts|cancelled-jobs|worker-exits|duplicate-cleanups|remaining-owners) (\d+)' }
            if ($Scenario -eq 'WindowDraggerLifetime') { $pattern = '(?m)^PERFCOUNTER (window-dragger) (cycles|late-subscriptions|late-writes|normal-writes|normal-starts|normal-ends) (\d+)\r?$' }
            if ($Scenario -eq 'ModifierDragLifetime') { $pattern = '(?m)^PERFCOUNTER (modifier-drag) (cycles|late-subscriptions|late-writes|pending-completions|normal-writes|normal-starts|normal-ends) (\d+)\r?$' }
            $matches = [regex]::Matches($output, $pattern)
            if ($Scenario -eq 'GenericPreview' -and $normalizedGenericPreviewCase) {
                $expectedGenericPreviewScenario = "generic-preview-$normalizedGenericPreviewCase"
                $unexpectedGenericPreviewCounters = @($matches | Where-Object { $_.Groups[1].Value -ne $expectedGenericPreviewScenario })
                if ($unexpectedGenericPreviewCounters.Count -ne 0) { throw "Unexpected generic preview case: $runId" }
            }
            if ($Scenario -eq 'LowLevelPatternLifetime') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER lowlevel-pattern-lifetime(?:[ \t][^\r\n]*)?\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed pattern lifetime counters: $runId" }
            }
            if ($Scenario -eq 'DelayedCommandLifetime') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER delayed-command-lifetime(?:[ \t][^\r\n]*)?\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed delayed command counters: $runId" }
            }
            if ($Scenario -eq 'VirtualDesktopCallbackLifetime') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER virtual-desktop-callback-lifetime(?:[ \t][^\r\n]*)?\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed virtual desktop callback counters: $runId" }
            }
            if ($Scenario -eq 'VirtualDesktopRemovalLifetime') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER virtual-desktop-removal-lifetime(?:[ \t][^\r\n]*)?\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed virtual desktop removal counters: $runId" }
            }
            if ($Scenario -eq 'TilingNotificationLifetime') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER tiling-notification-lifetime(?:[ \t][^\r\n]*)?\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed tiling notification counters: $runId" }
            }
            if ($Scenario -eq 'DirectHotkeyLifetime') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER direct-hotkey-lifetime(?:[ \t][^\r\n]*)?\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed direct hotkey counters: $runId" }
            }
            if ($Scenario -eq 'MicaCallbackLifetime') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER mica-primary-color-callback-lifetime(?:[ \t][^\r\n]*)?\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed Mica callback counters: $runId" }
            }
            if ($Scenario -eq 'LoadedUpdateCheckLifetime') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER loaded-update-check-lifetime(?:[ \t][^\r\n]*)?\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed loaded update-check counters: $runId" }
            }
            if ($Scenario -eq 'DisplayManageability') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER display-manage-provider-[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed display manageability counters: $runId" }
            }
            if ($Scenario -eq 'DiscoveryWindowSnapshot') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER discovery-window-snapshot-[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed discovery window snapshot counters: $runId" }
            }
            if ($Scenario -eq 'LayoutCallbackCache') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed layout callback cache counters: $runId" }
            }
            if ($Scenario -eq 'ContainsWindow') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed ContainsWindow counters: $runId" }
            }
            if ($Scenario -eq 'FocusRejectedAdmission') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed FocusRejectedAdmission counters: $runId" }
            }
            if ($Scenario -eq 'RefreshWindowSnapshot') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed refresh window snapshot counters: $runId" }
            }
            if ($Scenario -eq 'TransitionCompletion') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed transition completion counters: $runId" }
            }
            if ($Scenario -eq 'RuntimeStateCount') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed runtime state count counters: $runId" }
            }
            if ($Scenario -eq 'PanelIndex') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER panel-index-[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed panel index counters: $runId" }
            }
            if ($Scenario -eq 'EmbedIndex') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER embed-index-[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed Embed index counters: $runId" }
            }
            if ($Scenario -eq 'SplitPanelMeasure') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER split-measure[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed SplitPanel measurement counters: $runId" }
            }
            if ($Scenario -eq 'StackPanelMeasure') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER stack-measure[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed StackPanel measurement counters: $runId" }
            }
            if ($Scenario -eq 'PlaceholderCleanup') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER placeholder-cleanup[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed placeholder cleanup counters: $runId" }
            }
            if ($Scenario -eq 'SettingsDispose') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER settings-dispose[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed SettingsViewModel dispose counters: $runId" }
            }
            if ($Scenario -eq 'StartupWindowClose') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER startup-window-close[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed StartupWindow close counters: $runId" }
            }
            if ($Scenario -eq 'SettingsWindowClose') {
                $counterLines = [regex]::Matches($output, '(?m)^PERFCOUNTER settings-window-close[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed SettingsWindow close counters: $runId" }
            }
            if ($Scenario -eq 'OverlayHostClose') {
                $counterLines = [regex]::Matches($output, '(?m)^[ \t]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed OverlayHost close counters: $runId" }
            }
            if ($Scenario -eq 'OverlayWindowClose') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed OverlayWindow close counters: $runId" }
            }
            if ($Scenario -eq 'OverlayWindowCallbacks') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed OverlayWindow callback counters: $runId" }
            }
            if ($Scenario -eq 'TilingOverlayRendererDispose') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed TilingOverlayRenderer dispose counters: $runId" }
            }
            if ($Scenario -eq 'TilingOverlayRendererResources') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed TilingOverlayRenderer resource callback counters: $runId" }
            }
            if ($Scenario -eq 'TilingOverlayRendererRemoval') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed TilingOverlayRenderer removal counters: $runId" }
            }
            if ($Scenario -eq 'TilingOverlayRendererCreation') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed TilingOverlayRenderer creation counters: $runId" }
            }
            if ($Scenario -eq 'TilingOverlayRendererRecovery') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($matches.Count -ne 18 -or $counterLines.Count -ne 18) { throw "Malformed TilingOverlayRenderer recovery counters: $runId" }
                $recoveryRawKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
                foreach ($recoveryMatch in $matches) {
                    $recoveryCase = $recoveryMatch.Groups[1].Value
                    $recoveryMetric = $recoveryMatch.Groups[2].Value
                    $recoveryValue = $recoveryMatch.Groups[3].Value
                    if (!$recoveryRawKeys.Add("$recoveryCase/$recoveryMetric") -or
                        $recoveryValue -cne [string](Get-OverlayRecoveryExpectedCounter $variant $recoveryCase $recoveryMetric)) {
                        throw "Duplicate or incorrect recovery raw counter: $runId/$recoveryCase/$recoveryMetric"
                    }
                }
                foreach ($recoveryCase in $overlayRecoveryCases) {
                    foreach ($recoveryMetric in $overlayRecoveryUnits.Keys) {
                        if (!$recoveryRawKeys.Contains("$recoveryCase/$recoveryMetric")) { throw "Missing recovery raw counter: $runId/$recoveryCase/$recoveryMetric" }
                    }
                }
            }
            if ($Scenario -eq 'TilingWindowLifetime') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($matches.Count -ne 52 -or $counterLines.Count -ne 52) { throw "Malformed TilingWindowLifetime counters: $runId" }
                $lifetimeRawKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
                foreach ($lifetimeMatch in $matches) {
                    $caseName = $lifetimeMatch.Groups[1].Value
                    $metric = $lifetimeMatch.Groups[2].Value
                    if (!$lifetimeRawKeys.Add("$caseName/$metric") -or
                        $lifetimeMatch.Groups[3].Value -cne [string](Get-TilingWindowLifetimeExpectedCounter $variant $caseName $metric)) {
                        throw "Duplicate or incorrect TilingWindowLifetime raw counter: $runId/$caseName/$metric"
                    }
                }
            }
            if ($Scenario -eq 'ArrangeFailureBookkeeping') {
                $counterLines = [regex]::Matches($output, '(?im)^[^\r\n]*PERFCOUNTER[^\r\n]*\r?$')
                if ($counterLines.Count -ne $matches.Count) { throw "Malformed arrange-failure bookkeeping counters: $runId" }
            }
            $expectedCounters = if ($Scenario -eq 'Mica') { 5 } else { 2 }
            if ($Scenario -eq 'SettingsRuntime') { $expectedCounters = 8 }
            if ($Scenario -eq 'ArrangeFailureBookkeeping') { $expectedCounters = 192 }
            if ($Scenario -eq 'ThemeReload') { $expectedCounters = 6 }
            if ($Scenario -eq 'PreviewCacheManaged') { $expectedCounters = 21 }
            if ($Scenario -eq 'ThemeLocalImage') { $expectedCounters = 29 }
            if ($Scenario -eq 'HookReplacement') { $expectedCounters = 9 }
            if ($Scenario -eq 'HookLifetime') { $expectedCounters = 20 }
            if ($Scenario -eq 'HookDisposal') { $expectedCounters = 16 }
            if ($Scenario -eq 'LowLevelPatternLifetime') { $expectedCounters = 9 }
            if ($Scenario -eq 'DelayedCommandLifetime') { $expectedCounters = 6 }
            if ($Scenario -eq 'VirtualDesktopCallbackLifetime') { $expectedCounters = 10 }
            if ($Scenario -eq 'VirtualDesktopRemovalLifetime') { $expectedCounters = 6 }
            if ($Scenario -eq 'TilingNotificationLifetime') { $expectedCounters = 5 }
            if ($Scenario -eq 'DirectHotkeyLifetime') { $expectedCounters = 7 }
            if ($Scenario -eq 'MicaCallbackLifetime') { $expectedCounters = 9 }
            if ($Scenario -eq 'LoadedUpdateCheckLifetime') { $expectedCounters = 10 }
            if ($Scenario -eq 'DisplayRemovalGc') { $expectedCounters = 4 }
            if ($Scenario -eq 'SettingsStrings') { $expectedCounters = 28 }
            if ($Scenario -eq 'OverlayMaterialization') { $expectedCounters = 56 }
            if ($Scenario -eq 'ActionBarMaterialization') { $expectedCounters = 104 }
            if ($Scenario -eq 'BindingListener') { $expectedCounters = 6 }
            if ($Scenario -eq 'MainWindowLifetime') { $expectedCounters = 5 }
            if ($Scenario -eq 'ConstructionLifetime') { $expectedCounters = 5 }
            if ($Scenario -eq 'AppShutdown') { $expectedCounters = 5 }
            if ($Scenario -eq 'AppAsyncShutdown') { $expectedCounters = 7 }
            if ($Scenario -eq 'LayoutCadence') { $expectedCounters = 4 }
            if ($Scenario -eq 'KeyPressDisplay') { $expectedCounters = 21 }
            if ($Scenario -eq 'SvgMaterialization') { $expectedCounters = 62 }
            if ($Scenario -eq 'CursorInput') { $expectedCounters = 63 }
            if ($Scenario -eq 'GenericPreview') { $expectedCounters = if ($normalizedGenericPreviewCase) { 6 } else { 54 } }
            if ($Scenario -eq 'AnimationFrame') { $expectedCounters = 25 }
            if ($Scenario -eq 'LayoutCallbackCache') { $expectedCounters = 30 }
            if ($Scenario -eq 'ContainsWindow') { $expectedCounters = 84 }
            if ($Scenario -eq 'HotkeyCallback') { $expectedCounters = 60 }
            if ($Scenario -eq 'TreeStateLookup') { $expectedCounters = 20 }
            if ($Scenario -eq 'WindowStateLookup') { $expectedCounters = 64 }
            if ($Scenario -eq 'DisplayManageability') { $expectedCounters = 39 }
            if ($Scenario -eq 'DiscoveryWindowSnapshot') { $expectedCounters = 48 }
            if ($Scenario -eq 'RefreshWindowSnapshot') { $expectedCounters = 48 }
            if ($Scenario -eq 'TransitionCompletion') { $expectedCounters = 108 }
            if ($Scenario -eq 'RuntimeStateCount') { $expectedCounters = 60 }
            if ($Scenario -eq 'PanelIndex') { $expectedCounters = 18 }
            if ($Scenario -eq 'EmbedIndex') { $expectedCounters = 24 }
            if ($Scenario -eq 'SplitPanelMeasure') { $expectedCounters = 42 }
            if ($Scenario -eq 'StackPanelMeasure') { $expectedCounters = 42 }
            if ($Scenario -eq 'PlaceholderCleanup') { $expectedCounters = 120 }
            if ($Scenario -eq 'SettingsDispose') { $expectedCounters = 30 }
            if ($Scenario -eq 'StartupWindowClose') { $expectedCounters = 10 }
            if ($Scenario -eq 'SettingsWindowClose') { $expectedCounters = 36 }
            if ($Scenario -eq 'OverlayHostClose') { $expectedCounters = 51 }
            if ($Scenario -eq 'OverlayWindowClose') { $expectedCounters = 39 }
            if ($Scenario -eq 'OverlayWindowCallbacks') { $expectedCounters = 63 }
            if ($Scenario -eq 'TilingOverlayRendererDispose') { $expectedCounters = 60 }
            if ($Scenario -eq 'TilingOverlayRendererResources') { $expectedCounters = 21 }
            if ($Scenario -eq 'TilingOverlayRendererRemoval') { $expectedCounters = 18 }
            if ($Scenario -eq 'TilingOverlayRendererCreation') { $expectedCounters = 22 }
            if ($Scenario -eq 'TilingOverlayRendererRecovery') { $expectedCounters = 18 }
            if ($Scenario -eq 'TilingWindowLifetime') { $expectedCounters = 52 }
            if ($Scenario -eq 'WorkspaceInvariant') { $expectedCounters = 15 }
            if ($Scenario -eq 'TransitionDispatch') { $expectedCounters = 25 }
            if ($Scenario -eq 'ReleaseChecker') { $expectedCounters = 2 }
            if ($Scenario -eq 'PostMoveTimer') { $expectedCounters = 3 }
            if ($Scenario -eq 'PreviewCache') { $expectedCounters = 21 }
            if ($Scenario -eq 'FocusFallback') { $expectedCounters = 4 }
            if ($Scenario -eq 'FocusSupersession') { $expectedCounters = 8 }
            if ($Scenario -eq 'FocusBounded') { $expectedCounters = 8 }
            if ($Scenario -eq 'FocusRejectedAdmission') { $expectedCounters = 27 }
            if ($Scenario -eq 'ClockBoost') { $expectedCounters = 8 }
            if ($Scenario -eq 'AnimationShutdown') { $expectedCounters = 12 }
            if ($Scenario -eq 'DependentShutdown') { $expectedCounters = 8 }
            if ($Scenario -eq 'WindowDraggerLifetime') { $expectedCounters = 6 }
            if ($Scenario -eq 'ModifierDragLifetime') { $expectedCounters = 7 }
            if ($Scenario -eq 'IconDiscovery') { $expectedCounters = 24 }
            if ($Scenario -eq 'SequenceHash') { $expectedCounters = 6 }
            if ($Scenario -eq 'ResizeTraversal') { $expectedCounters = 6 }
            if ($Scenario -eq 'DiagnosticReuse') { $expectedCounters = 15 }
            if ($Scenario -eq 'DisabledLogging') { $expectedCounters = 10 }
            if ($Scenario -eq 'TransitionRounded') { $expectedCounters = 15 }
            if ($Scenario -eq 'OverlayUpdates') { $expectedCounters = 24 }
            if ($Scenario -eq 'OverlayAnimation') { $expectedCounters = 4 }
            if ($matches.Count -ne $expectedCounters) { throw "Missing counters: $runId" }
            $uniqueCounters = @($matches | ForEach-Object { $_.Groups[1].Value + '/' + $_.Groups[2].Value } | Select-Object -Unique)
            if ($uniqueCounters.Count -ne $expectedCounters) { throw "Duplicate counter keys: $runId" }
            foreach ($match in $matches) {
                $counterRow = [pscustomobject]@{
                    experiment_id = $ExperimentId
                    snapshot_id = if ($variant -eq 'baseline') { $BaselineSnapshotId } else { $SnapshotId }
                    scenario = if ($Scenario -eq 'FocusBounded') { 'Actual FocusHelper.ForceActivate;16 expired fallback requests while fake EnsureMessageQueue is blocked, plus newest immediate success and post-release recovery;controlled expiry and worker-start barriers;mechanical thread-per-request seam baseline' } elseif ($Scenario -eq 'AppAsyncShutdown') { 'Actual App.TerminateOwnedWindowsAsync/CloseOwnedWindowsAsync;100 fake window lifetimes with controlled pending completion;count-only comparison without discarded warmup;mechanical pre-fix application-shutdown seam baseline' } elseif ($Scenario -eq 'DependentShutdown') { 'Actual MainWindowLifetime and owned AnimationThread;one discarded warmup then100 reentrant disposals inside blocked fake updates with three dependent resources;mechanical pre-fix dependency-teardown seam baseline' } elseif ($Scenario -eq 'AnimationShutdown') { 'Actual owned AnimationThread.Dispose/RunLoop;one discarded warmup then100 blocked-frame shutdown cycles with two accepted jobs;mechanical pre-fix worker seam baseline' } elseif ($Scenario -eq 'ClockBoost') { 'Actual AnimationThread.RunLoop and ClockBoost;one warmup then100 two-frame bursts with queued second job and controlled cancellation;mechanical per-frame boost seam baseline' } elseif ($Scenario -eq 'FocusSupersession') { 'Actual FocusHelper.ForceActivate;100 old fallbacks blocked before attachment while a newer immediate activation succeeds;mechanical pre-fix focus seam baseline' } elseif ($Scenario -eq 'ConstructionLifetime') { 'Actual MainWindowLifetime constructor ownership;100 partial-construction failures after three acquired owners;first cleanup releases then throws;mechanical constructor seam baseline' } elseif ($Scenario -eq 'LayoutCadence') { 'Actual TilingService and LayoutInvalidationQueue;100 equal cadence cycles with controlled elapsed time and delay;mechanical old-guard baseline' } elseif ($Scenario -eq 'MainWindowLifetime') { 'Actual MainWindowLifetime cleanup adapter;100 partial-failure lifetimes with four owned actions each;second action releases then throws;mechanical pre-fix seam baseline' } elseif ($Scenario -eq 'AppShutdown') { 'Actual App shutdown helper;100 lifetimes with three fake windows each and a throwing Dispose;mechanical pre-fix App.Close seam baseline' } elseif ($Scenario -eq 'BindingListener') { 'Actual BindingErrorListener;100 owned registered/disposed listeners and fake logger owners;standalone retention GC without timing' } elseif ($Scenario -eq 'KeyPressDisplay') { "KeyPressBox actual display; $($match.Groups[1].Value);20 warmup cycles then100 create/format/220x32 layout/Unloaded cycles; mechanical eager acquisition seam baseline" } elseif ($Scenario -eq 'SvgMaterialization') { "SvgIcon; $($match.Groups[1].Value);20 actual control/layout warmups then separate constructor and first layout phases; getter probe after measured groups" } elseif ($Scenario -eq 'HookReplacement') { 'HookRegistrationPolicy.RefreshIfIdle;100 fake-owned lifetimes with one failed and one successful admitted replacement plus one not-due tick' } elseif ($Scenario -eq 'DisplayRemovalGc') { 'MultiDisplayTilingService.OnDisplayRemoved;100 real fake-service owners, each one removal and100 duplicate events then disposal/late event' } elseif ($Scenario -eq 'ActionBarMaterialization') { "TilingWindow; $($match.Groups[1].Value);actual hidden-action constructor and first layout, resource setup excluded; W1 process-cold control initialization" } elseif ($Scenario -eq 'CursorInput') { "OverlayHost cursor factory and existing LayoutInvalidationQueue; $($match.Groups[1].Value);1000 inside/outside inputs per display, one invalidate/drain per input" } elseif ($Scenario -eq 'SettingsStrings') { "StringsListBox.TextBox_LostFocus; $($match.Groups[1].Value);20 warmups then100 unchanged real routed focus events with two-way binding and layout" } elseif ($Scenario -eq 'OverlayMaterialization') { "NonHitTestableTilingOverlay; $($match.Groups[1].Value);actual constructor and first layout; W1 includes process-cold WPF initialization, later sizes share framework caches" } elseif ($Scenario -eq 'GenericPreview') { "TilingWorkspace.MockMoveNode; $($match.Groups[1].Value);30 warmups then300 previews over cross-parent/sibling/source/outside points; real complete cloned placement pipeline" } elseif ($Scenario -eq 'AnimationFrame') { "AnimationThread.Frame.UpdateFrame; $($match.Groups[1].Value);1000 warmups then10000 stable synchronous frames at progress0.5; intermediate mechanical seam baseline" } elseif ($Scenario -eq 'HotkeyCallback') { "LowLevelHotkey native-free multicast; $($match.Groups[1].Value);20 warmups then10000 quiet input pairs or500 matching sequences of27 events; UI queue drained outside callback timing" } elseif ($Scenario -eq 'PreviewCacheManaged') { "MasterSatelliteDropController.CreateWindowDropPlan; $($match.Groups[1].Value);80 warmups then1000 exact pointers; field-backed window/desktop/display adapters" } elseif ($Scenario -eq 'ThemeLocalImage') { "CssValue.As<ImageBrush>/BitmapSource.CopyPixels; $($match.Groups[1].Value);unique locally generated PNG URI,1000 repeated value reads,100 independent small-image lifetimes" } elseif ($Scenario -eq 'TreeStateLookup') { "TilingWorkspaceState.GetState(DesktopTree); $($match.Groups[1].Value) desktops;1000warmups then100000 alternating lookups" } elseif ($Scenario -eq 'WorkspaceInvariant') { "TilingWorkspace.ExecuteAlgorithmicOperation; $($match.Groups[1].Value);20 warmups then100 rounds of four commands" } elseif ($Scenario -eq 'TransitionDispatch') { "TransitionTargetGroup.PerformTransitionAsync; $($match.Groups[1].Value);20 warmups then100 transitions" } elseif ($Scenario -eq 'ReleaseChecker') { 'ReleaseChecker;100 factory-created DI singleton lifetimes with fake HTTP handler' } elseif ($Scenario -eq 'PostMoveTimer') { 'TilingService.ReconcileMasterSatelliteTransferAfterMove; 100 six-attempt transfer lifetimes; actual50ms interval and fixed budget preserved' } elseif ($Scenario -eq 'PreviewCache') { "MasterSatelliteDropController.CreateWindowDropPlan; $($match.Groups[1].Value); 20 warmups per zone then1000 deterministic pointers in four zones; independent exact oracle outside measured sections" } elseif ($Scenario -eq 'FocusFallback') { 'FocusHelper.ForceActivate; 100 successful/throwing fallback worker lifetimes with fake native calls; candidate-only ownership counts' } elseif ($Scenario -eq 'IconDiscovery') { "TilingWindowViewModel.Icon/IconVisibility and WindowExtensions.IconCache; $($match.Groups[1].Value); 100 cold and 100 warm binding read pairs; candidate-only counters" } elseif ($Scenario -eq 'OwnershipTimer') { 'TilingService.TryScheduleMasterSatelliteIncomingOwnershipProbe; 100 controlled six-attempt lifetimes; actual50ms interval preserved; adapter scheduling count only' } elseif ($Scenario -eq 'DisabledLogging') { "TilingService.CalculateRepositionTargets; $($match.Groups[1].Value); Information logger; 20 warmups then 100 target calculations" } elseif ($Scenario -eq 'DiagnosticReuse') { "MasterSatelliteLayoutEngine; $($match.Groups[1].Value); 20 warmup rounds then 100 rounds of side/ratio/promotion/reorder and admission or capacity rejection" } elseif ($Scenario -eq 'TransitionRounded') { "TransitionTargetGroup.PerformSmoothTransitionAsync; $($match.Groups[1].Value); 2 warmups then 100 transitions with 14 controlled logical frames" } elseif ($Scenario -eq 'ResizeTraversal') { "TilingWorkspace.ResizeNode; $($match.Groups[1].Value); 20 warmup resize pairs then 100 positive/negative pairs with exact rectangles and identities" } elseif ($Scenario -eq 'OverlayUpdates') { "TilingOverlayRenderer.UpdateViewModels/OnSetPreviewWindows; $($match.Groups[1].Value); 20 warmups then 100 identical snapshot/focus/equivalent-preview updates" } elseif ($Scenario -eq 'OverlayAnimation') { "OnDataContextPropertyChanged; $($match.Groups[1].Value); 10000 warmups then 10000 unrelated focus-rectangle notifications" } elseif ($Scenario -eq 'SequenceHash') { "Collections.ToSequenceComparer; $($match.Groups[1].Value); 20 warmups then 100 ordered pattern-set build/lookup/duplicate-add cycles" } elseif ($Scenario -eq 'ThemeReload') { 'ThemeEngineManager; controlled identical-content reloads and repeated owned lifetimes; candidate-only counters' } elseif ($Scenario -eq 'SettingsDistinct') { 'ObservableFileEntityBase.SaveAsync; actual temp-file transactions with controlled first serializer barrier; one 100-update warmup then 100 distinct field updates' } elseif ($Scenario -eq 'SettingsRuntime') { "TilingService.OnSettingsChanged; $($match.Groups[1].Value); stable fake workspace; 20 warmups then 100 unrelated settings updates" } elseif ($Scenario -eq 'Mica') { "FauxMicaProvider.Refresh; fake $($match.Groups[1].Value); 600 logical ticks" } elseif ($Scenario -eq 'CoordinatorNonempty') { 'AlgorithmicLayoutCoordinator.CleanupExpired; one active reserved transfer; fixed clock; 2000 warmups and 10000 calls' } elseif ($Scenario -eq 'CoordinatorDeadlines') { 'AlgorithmicLayoutCoordinator.OnCleanupTimerTick; one reserved transfer per cycle; 100 cycles; 10 s timeout and 120 s terminal retention; clock advances by timer.Interval' } else { 'AlgorithmicLayoutCoordinator.CleanupExpired; empty state; 2000 warmups and 10000 calls' }
                    configuration = "Release; $testFramework; x64; no debugger; DOTNET_TieredCompilation=0"
                    window_count = if ($Scenario -eq 'FocusBounded') { 3 } elseif ($Scenario -in @('ClockBoost','AnimationShutdown','DependentShutdown','AppAsyncShutdown')) { 0 } elseif ($Scenario -eq 'FocusSupersession') { 2 } elseif ($Scenario -eq 'LayoutCadence') { 2 } elseif ($Scenario -eq 'KeyPressDisplay') { 0 } elseif ($Scenario -eq 'SvgMaterialization') { 0 } elseif ($Scenario -in @('HookReplacement','DisplayRemovalGc')) { 0 } elseif ($Scenario -eq 'CursorInput') { 0 } elseif ($Scenario -eq 'SettingsStrings') { 0 } elseif ($Scenario -in @('OverlayMaterialization','ActionBarMaterialization')) { [int]($match.Groups[1].Value.Split('-')[-1]) } elseif ($Scenario -eq 'GenericPreview') { [int]($match.Groups[1].Value.Split('-')[-1]) } elseif ($Scenario -eq 'AnimationFrame') { [int]($match.Groups[1].Value.Split('-')[-1]) } elseif ($Scenario -eq 'HotkeyCallback') { 0 } elseif ($Scenario -eq 'PreviewCacheManaged') { [int]($match.Groups[1].Value.Split('-')[-1]) } elseif ($Scenario -eq 'ThemeLocalImage') { 0 } elseif ($Scenario -eq 'TreeStateLookup') { 0 } elseif ($Scenario -in @('WorkspaceInvariant', 'TransitionDispatch')) { [int]($match.Groups[1].Value.Split('-')[-1]) } elseif ($Scenario -eq 'ReleaseChecker') { 0 } elseif ($Scenario -eq 'PostMoveTimer') { 1 } elseif ($Scenario -eq 'PreviewCache') { [int]($match.Groups[1].Value.Split('-')[-1]) } elseif ($Scenario -eq 'FocusFallback') { 1 } elseif ($Scenario -eq 'OwnershipTimer') { 1 } elseif ($Scenario -eq 'IconDiscovery') { [int]($match.Groups[1].Value.Split('-')[-1]) } elseif ($Scenario -in @('DiagnosticReuse', 'TransitionRounded', 'DisabledLogging', 'OwnershipTimer')) { [int]($match.Groups[1].Value.Split('-')[-1]) } elseif ($Scenario -eq 'ResizeTraversal') { 2 } elseif ($Scenario -eq 'OverlayUpdates') { [int]($match.Groups[1].Value.Split('-')[-1]) } elseif ($Scenario -eq 'SettingsRuntime') { 3 } elseif ($Scenario -in @('CoordinatorNonempty', 'CoordinatorDeadlines')) { 1 } else { 0 }
                    metric = $match.Groups[2].Value
                    unit = if ($Scenario -eq 'FocusBounded') { switch ($match.Groups[2].Value) { 'requests' { 'requests/scenario' } 'returned-before-release' { 'requests/scenario' } 'started-workers' { 'workers/scenario' } 'background-workers' { 'workers/scenario' } 'worker-exits' { 'workers/scenario' } 'obsolete-focus' { 'focus-calls/scenario' } default { 'successful-requests/scenario' } } } elseif ($Scenario -eq 'AppAsyncShutdown') { switch ($match.Groups[2].Value) { 'cycles' { 'cycles/scenario' } 'early-window-closes' { 'close-calls/scenario' } 'early-shutdowns' { 'shutdown-calls/scenario' } 'dispose-attempts' { 'dispose-calls/scenario' } 'close-attempts' { 'close-calls/scenario' } 'mouse-cleanups' { 'cleanup-calls/scenario' } 'shutdowns' { 'shutdown-calls/scenario' } } } elseif ($Scenario -eq 'DependentShutdown') { switch ($match.Groups[2].Value) { 'cycles' { 'cycles/scenario' } 'premature-cleanups' { 'cleanup-calls/scenario' } 'pending-completions' { 'completions/scenario' } 'cleanup-attempts' { 'cleanup-calls/scenario' } 'cancelled-jobs' { 'jobs/scenario' } 'worker-exits' { 'workers/scenario' } 'duplicate-cleanups' { 'cleanup-calls/scenario' } 'remaining-owners' { 'owners/scenario' } } } elseif ($Scenario -eq 'AnimationShutdown') { switch ($match.Groups[2].Value) { 'cycles' { 'cycles/scenario' } 'accepted-jobs' { 'jobs/scenario' } 'successful-jobs' { 'jobs/scenario' } 'waits' { 'waits/scenario' } 'updates' { 'updates/scenario' } 'queued-updates' { 'updates/scenario' } 'cancellations' { 'cancellations/scenario' } 'remaining-references' { 'fake-owned-boost-references/after-worker-exit' } 'worker-exits' { 'workers/scenario' } 'late-rejections' { 'rejected-starts/scenario' } default { 'calls/scenario' } } } elseif ($Scenario -eq 'ClockBoost' -and $match.Groups[2].Value -eq 'remaining-references') { 'fake-boost-references/after-loop' } elseif ($Scenario -eq 'ClockBoost' -and $match.Groups[2].Value -eq 'zero-progress-updates') { 'updates/scenario' } elseif ($Scenario -eq 'ClockBoost' -and $match.Groups[2].Value -in @('waits','cancellations')) { "$($match.Groups[2].Value)/scenario" } elseif ($Scenario -eq 'FocusSupersession' -and $match.Groups[2].Value -eq 'superseded-results') { 'superseded-results/scenario' } elseif ($Scenario -eq 'FocusSupersession' -and $match.Groups[2].Value -eq 'completed-workers') { 'workers/scenario' } elseif ($Scenario -eq 'ConstructionLifetime' -and $match.Groups[2].Value -eq 'remaining-owners') { 'owners/after-construction-failure' } elseif ($Scenario -eq 'ConstructionLifetime' -and $match.Groups[2].Value -eq 'original-errors') { 'preserved-errors/scenario' } elseif ($Scenario -eq 'LayoutCadence' -and $match.Groups[2].Value -eq 'pending-delays') { 'delays/after-completion' } elseif ($Scenario -eq 'MainWindowLifetime' -and $match.Groups[2].Value -eq 'remaining-owners') { 'owners/after-dispose' } elseif ($Scenario -eq 'MainWindowLifetime' -and $match.Groups[2].Value -eq 'original-errors') { 'preserved-errors/scenario' } elseif ($Scenario -eq 'AppShutdown' -and $match.Groups[2].Value -eq 'original-errors') { 'preserved-errors/scenario' } elseif ($Scenario -eq 'BindingListener' -and $match.Groups[2].Value -like '*retained-owners') { 'owners/after-dispose-and-GC' } elseif ($Scenario -eq 'BindingListener' -and $match.Groups[2].Value -eq 'remaining-subscriptions') { 'subscriptions/after-dispose' } elseif ($Scenario -eq 'KeyPressDisplay') { if ($match.Groups[2].Value -eq 'display-checksum') { 'text-order-checksum' } elseif ($match.Groups[2].Value -eq 'timestamp-frequency') { 'stopwatch-ticks/second' } elseif ($match.Groups[2].Value -eq 'elapsed-ticks') { 'stopwatch-ticks/scenario' } elseif ($match.Groups[2].Value -eq 'allocated-bytes') { 'bytes/scenario' } elseif ($match.Groups[2].Value -eq 'live-hook-subscribers') { 'subscribers/after-unload' } elseif ($match.Groups[2].Value -eq 'created-controls') { 'controls/scenario' } else { 'listeners/scenario' } } elseif ($Scenario -eq 'SvgMaterialization') { if ($match.Groups[2].Value -eq 'pixel-digest') { 'sha256-pbgra32-pixels' } elseif ($match.Groups[2].Value -eq 'timestamp-frequency') { 'stopwatch-ticks/second' } elseif ($match.Groups[2].Value -like '*-ticks') { 'stopwatch-ticks/scenario' } elseif ($match.Groups[2].Value -like '*-bytes') { 'bytes/scenario' } elseif ($match.Groups[2].Value -eq 'storage-field-available') { 'boolean' } else { 'objects/scenario' } } elseif ($Scenario -eq 'HookReplacement' -and $match.Groups[2].Value -like '*fake-live-handles') { 'fake-owned-handles' } elseif ($Scenario -eq 'DisplayRemovalGc' -and $match.Groups[2].Value -eq 'collection-requests') { 'gc-requests/scenario' } elseif ($Scenario -eq 'CursorInput' -and $match.Groups[2].Value -eq 'posts') { 'dispatch-posts/scenario' } elseif ($Scenario -eq 'CursorInput' -and $match.Groups[2].Value -eq 'input-events') { 'events/scenario' } elseif ($Scenario -in @('SettingsStrings','OverlayMaterialization','ActionBarMaterialization') -and $match.Groups[2].Value -eq 'timestamp-frequency') { 'stopwatch-ticks/second' } elseif ($Scenario -in @('SettingsStrings','OverlayMaterialization','ActionBarMaterialization') -and $match.Groups[2].Value -like '*-ticks') { 'stopwatch-ticks/scenario' } elseif ($Scenario -in @('SettingsStrings','OverlayMaterialization','ActionBarMaterialization') -and $match.Groups[2].Value -like '*-bytes') { 'bytes/scenario' } elseif ($Scenario -eq 'SettingsStrings' -and $match.Groups[2].Value -eq 'content-digest') { 'sha256-ordered-contents' } elseif ($Scenario -eq 'SettingsStrings' -and $match.Groups[2].Value -eq 'presenter-replacements') { 'replaced-presenters/scenario' } elseif ($Scenario -eq 'SettingsStrings' -and $match.Groups[2].Value -eq 'source-updates') { 'bound-source-updates/scenario' } elseif ($Scenario -eq 'SettingsStrings' -and $match.Groups[2].Value -eq 'lost-focus-events') { 'events/scenario' } elseif ($Scenario -in @('OverlayMaterialization','ActionBarMaterialization')) { 'objects/scenario' } elseif ($Scenario -eq 'GenericPreview' -and $match.Groups[2].Value -eq 'geometry-digest') { 'sha256-exact-preview-rectangles' } elseif ($Scenario -eq 'GenericPreview' -and $match.Groups[2].Value -eq 'elapsed-ticks') { 'stopwatch-ticks/scenario' } elseif ($Scenario -eq 'GenericPreview' -and $match.Groups[2].Value -eq 'timestamp-frequency') { 'stopwatch-ticks/second' } elseif ($Scenario -eq 'GenericPreview' -and $match.Groups[2].Value -eq 'previews') { 'previews/scenario' } elseif ($Scenario -eq 'AnimationFrame' -and $match.Groups[2].Value -eq 'frames') { 'frames/scenario' } elseif ($Scenario -eq 'HotkeyCallback' -and $match.Groups[2].Value -like '*-events') { 'events/scenario' } elseif ($Scenario -in @('AnimationFrame','HotkeyCallback') -and $match.Groups[2].Value -eq 'timestamp-frequency') { 'stopwatch-ticks/second' } elseif ($Scenario -eq 'HotkeyCallback' -and $match.Groups[2].Value -match '^p\d+-ticks$') { 'stopwatch-ticks/input-event' } elseif ($Scenario -in @('AnimationFrame','HotkeyCallback') -and $match.Groups[2].Value -eq 'elapsed-ticks') { 'stopwatch-ticks/scenario' } elseif ($Scenario -eq 'HotkeyCallback' -and $match.Groups[2].Value -eq 'event-digest') { 'sha256-suppression-and-emission-sequence' } elseif ($Scenario -eq 'PreviewCacheManaged' -and $match.Groups[2].Value -eq 'plan-digest') { 'sha256-plan-and-state' } elseif ($Scenario -in @('PreviewCacheManaged','ThemeLocalImage') -and $match.Groups[2].Value -eq 'timestamp-frequency') { 'stopwatch-ticks/second' } elseif ($Scenario -in @('PreviewCacheManaged','ThemeLocalImage') -and $match.Groups[2].Value -like '*-ticks') { 'stopwatch-ticks/scenario' } elseif ($Scenario -eq 'ThemeLocalImage' -and $match.Groups[2].Value -like '*-bytes') { 'bytes/scenario' } elseif ($Scenario -eq 'ThemeLocalImage' -and $match.Groups[2].Value -eq 'pixel-digest') { 'sha256-bgra32-pixels' } elseif ($Scenario -eq 'ThemeLocalImage' -and $match.Groups[2].Value -eq 'pixels') { 'pixels/image' } elseif ($Scenario -eq 'ThemeLocalImage' -and $match.Groups[2].Value -eq 'retained-values-brushes') { 'objects/after-owner-release-and-GC' } elseif ($Scenario -eq 'TreeStateLookup' -and $match.Groups[2].Value -eq 'elapsed-ticks') { 'stopwatch-ticks/scenario' } elseif ($Scenario -eq 'TreeStateLookup' -and $match.Groups[2].Value -eq 'timestamp-frequency') { 'stopwatch-ticks/second' } elseif ($Scenario -eq 'WorkspaceInvariant' -and $match.Groups[2].Value -eq 'geometry-digest') { 'sha256-final-rectangles' } elseif ($Scenario -eq 'TransitionDispatch' -and $match.Groups[2].Value -eq 'elapsed-ticks') { 'stopwatch-ticks/scenario' } elseif ($Scenario -eq 'TransitionDispatch' -and $match.Groups[2].Value -eq 'timestamp-frequency') { 'stopwatch-ticks/second' } elseif ($Scenario -eq 'PreviewCache' -and $match.Groups[2].Value -eq 'plan-digest') { 'sha256-plan-and-state' } elseif ($Scenario -eq 'PreviewCache' -and $match.Groups[2].Value -eq 'elapsed-ticks') { 'stopwatch-ticks/scenario' } elseif ($Scenario -eq 'PreviewCache' -and $match.Groups[2].Value -eq 'timestamp-frequency') { 'stopwatch-ticks/second' } elseif ($Scenario -eq 'IconDiscovery' -and $match.Groups[2].Value -eq 'binding-reads') { 'reads/scenario' } elseif ($Scenario -eq 'IconDiscovery' -and $match.Groups[2].Value -eq 'applied-icons') { 'icons/scenario' } elseif ($Scenario -eq 'IconDiscovery' -and $match.Groups[2].Value -eq 'retained-cache-entries') { 'entries/after-dispose' } elseif ($Scenario -eq 'DiagnosticReuse' -and $match.Groups[2].Value -eq 'geometry-digest') { 'sha256-final-rectangles' } elseif ($Scenario -eq 'DisabledLogging' -and $match.Groups[2].Value -eq 'target-count') { 'targets/scenario' } elseif ($Scenario -eq 'DiagnosticReuse' -and $match.Groups[2].Value -eq 'geometry-checksum') { 'uint32-checksum/scenario' } elseif ($Scenario -eq 'SequenceHash' -and $match.Groups[2].Value -eq 'patterns') { 'patterns/cycle' } elseif ($Scenario -eq 'OverlayUpdates' -and $match.Groups[2].Value -eq 'windows') { 'windows/scenario' } elseif ($Scenario -eq 'ThemeReload') { if ($match.Groups[2].Value -eq 'max-concurrency') { 'workers' } elseif ($match.Groups[2].Value -eq 'retained-resources') { 'resources' } else { 'count/scenario' } } elseif ($match.Groups[2].Value -eq 'allocated-bytes') { 'bytes/scenario' } elseif ($match.Groups[2].Value -in @('cycles', 'updates', 'iterations', 'rounds', 'transitions')) { "$($match.Groups[2].Value)/scenario" } else { 'calls/scenario' }
                    run = $run
                    value = $match.Groups[3].Value
                    iterations = if ($Scenario -eq 'FocusBounded') { 16 } elseif ($Scenario -in @('ClockBoost','AnimationShutdown','DependentShutdown','AppAsyncShutdown')) { 100 } elseif ($Scenario -eq 'FocusSupersession') { 100 } elseif ($Scenario -eq 'ConstructionLifetime') { 100 } elseif ($Scenario -eq 'LayoutCadence') { 100 } elseif ($Scenario -in @('BindingListener','MainWindowLifetime')) { 100 } elseif ($Scenario -eq 'AppShutdown') { 100 } elseif ($Scenario -eq 'KeyPressDisplay') { 100 } elseif ($Scenario -eq 'SvgMaterialization') { 1 } elseif ($Scenario -in @('HookReplacement','DisplayRemovalGc')) { 100 } elseif ($Scenario -eq 'CursorInput') { 1000 } elseif ($Scenario -eq 'SettingsStrings') { 100 } elseif ($Scenario -in @('OverlayMaterialization','ActionBarMaterialization')) { 1 } elseif ($Scenario -eq 'GenericPreview') { 300 } elseif ($Scenario -eq 'AnimationFrame') { 10000 } elseif ($Scenario -eq 'HotkeyCallback') { if ($match.Groups[1].Value -like '*quiet*') { 20000 } else { 13500 } } elseif ($Scenario -eq 'PreviewCacheManaged') { 1000 } elseif ($Scenario -eq 'ThemeLocalImage') { if ($match.Groups[1].Value -eq 'local-image-lifetimes') { 100 } else { 1 } } elseif ($Scenario -eq 'TreeStateLookup') { 100000 } elseif ($Scenario -in @('WorkspaceInvariant', 'TransitionDispatch', 'ReleaseChecker')) { 100 } elseif ($Scenario -eq 'PostMoveTimer') { 100 } elseif ($Scenario -eq 'PreviewCache') { 1000 } elseif ($Scenario -eq 'FocusFallback') { 100 } elseif ($Scenario -eq 'IconDiscovery') { 200 } elseif ($Scenario -eq 'ResizeTraversal') { 200 } elseif ($Scenario -eq 'Mica') { 600 } elseif ($Scenario -in @('CoordinatorDeadlines', 'SettingsDistinct', 'SettingsRuntime', 'ThemeReload', 'SequenceHash', 'OverlayUpdates', 'DiagnosticReuse', 'TransitionRounded', 'DisabledLogging', 'OwnershipTimer')) { 100 } else { 10000 }
                    instrument = if ($Scenario -eq 'FocusBounded') { 'Actual managed fallback admission and caller completion with fake native EnsureMessageQueue, controlled expiry/start barriers and bounded completion observation;all started workers released and joined;16 burst requests exclude newest/recovery controls;fallback bound only;no elapsed timing/native focus/desktop operations;initial synchronous native calls, check-to-call race and already-entered effects remain outside this evidence' } elseif ($Scenario -eq 'AppAsyncShutdown') { 'Actual production App termination and owned-window cleanup helpers;100 identical fake window lifetimes with controlled incomplete task and immediate fake Dispatcher;early Close and Shutdown counted before completion is released;both variants must perform exactly100 disposals,closes,mouse cleanups and shutdowns;separate compatible mechanical seam and same current fixture;no discarded warmup,timed region,Application/Window/HWND construction,native hook/desktop calls,physical shutdown latency,process memory,CPU/GPU or energy claim' } elseif ($Scenario -eq 'DependentShutdown') { 'Actual production MainWindowLifetime wired into MainWindow.Dispose with owned AnimationThread/RunLoop and three fake dependent resources;fake Update calls Dispose reentrantly and remains blocked on controlled task completion;one warmup then100 lifetimes with exact eventual cleanup,job cancellation,worker exit and zero duplicates;premature cleanup and pending lifetime Completion observed before release;separate mechanical pre-fix seam and same current fixture;PERF-016 ownership and associated PERF-010 worker shutdown;no MainWindow/App/HWND construction,native compositor/desktop calls,timed region,native shutdown bound,process memory,CPU/GPU or energy claim' } elseif ($Scenario -eq 'AnimationShutdown') { 'Actual production AnimationThread owns its worker,queue,Frame and ClockBoost;fake compositor wait/boost adapters and self-cancelling jobs;one warmup then100 lifetimes with active first wait held,second job queued,Dispose on separate caller,first wait released only after queue admission closes;worker joined and disposer Task awaited;exact terminal cancellations,late Start rejection and zero retained owned fake boost references checked,three borrowed references preserved per cycle;same current fixture against separate hashed mechanical seam/candidate binaries;PERF-016 shutdown ownership and PERF-010 no-after-stop frame admission;counts only,no timed region,native compositor,application window,desktop change,physical cadence,process CPU/GPU/energy or termination of hung native operations is measured' } elseif ($Scenario -eq 'ClockBoost') { 'Actual production RunLoop,Frame and boost lifetime on the invoking test thread with fake boost/wait adapters and a frozen Stopwatch;one discarded warmup then100 two-frame bursts;first update queues second job,both cancel after one exact zero-progress update,second completes queue;all200 waits retain6ms timeout at144Hz;no native compositor,application window,desktop change,timed region,actual refresh cadence or process CPU/GPU/energy measurement;fake outstanding-reference counter models documented native request ownership' } elseif ($Scenario -eq 'FocusSupersession') { 'Actual production focus admission/fallback with fake native adapters and controlled EnsureMessageQueue barriers;each of100 cycles completes and joins the old worker after a newer immediate success;same current fixture with separately archived mechanical seam and hashed production/test DLLs;old-focus includes the common100 initial attempts,so obsolete fallback calls equal old-focus minus cycles;observed supersession skips later checked stages,but IsCurrent and the next native invocation are separate so the check-to-call race remains;no native focus,input,desktop change,timed region,process CPU,atomic latest-focus admission or native cancellation is measured' } elseif ($Scenario -eq 'ConstructionLifetime') { 'Actual existing production lifetime helper wired into MainWindow constructor;three fake owned adapters per partial-construction cycle, then an acquisition error and a distinct throwing first cleanup;100 cycles preserve original acquisition error identity/stack and zero duplicate cleanup;same current fixture with separately archived mechanical construction seam and hashed source/production/test DLLs;no MainWindow,App,HWND or native construction,no timing,process memory,CPU or native-shutdown E2E claim' } elseif ($Scenario -eq 'LayoutCadence') { 'Actual production service and its existing invalidation queue;fake elapsed-time/delay completion with fake window adapters on owned Dispatcher;100 cycles and exact trailing-layout/delay/pending counts;separately archived mechanical clock seam with original lost guard and identical current fixture;no native windows,timed region,animation cadence or process CPU/GPU claim' } elseif ($Scenario -eq 'MainWindowLifetime') { 'Actual production cleanup adapter wired into MainWindow.Dispose;fake owned actions without MainWindow/App/HWND/native construction;100 lifetimes with a releasing/throwing second owner and repeated Dispose;exact original exception identity and zero duplicate cleanup checked;separately archived mechanical pre-fix seam and same current fixture;counts only,no timing,CPU,native handles,partial native startup or application-shutdown E2E claim' } elseif ($Scenario -eq 'AppShutdown') { 'Actual static production shutdown method wired into App.Close;three fake windows per lifetime with one throwing Dispose and a fake mouse cleanup callback;100 lifetimes,exact original exceptions and disposal/close ordering checked;separately archived mechanical APP-SEAM baseline and same current fixture;no Application,Window,HWND or native service construction;required cleanup counts only,no timing,CPU,process memory or native-shutdown E2E claim' } elseif ($Scenario -eq 'BindingListener') { 'Actual global TraceListener registration, direct WriteLine and Dispose with fake logger owners;exact100 live callbacks and zero final registrations/owners;retained disposed listeners are deliberately held through standalone GC to probe delegate ownership;late delivery/owner retention may differ;no App/HWND/native activity, timed GC, latency, native heap or whole-process leak claim;same current fixture and archived C1 baseline DLL' } elseif ($Scenario -eq 'KeyPressDisplay') { 'actual compiled KeyPressBox XAML and KeyPatternListener with per-instance factory and native-free borrowed hook event owner on owned STA; mechanical eager baseline explicitly adds same injection/focus seams; no App/HWND/real hook/input or recording enabled; exact displayed text/order/220x32 layout asserted, common assertions/arrays/checksum included in allocation/time; subscriber retention inspection outside measured regions; same current fixture both DLLs; editor materialization only, not whole-page latency/native hook-thread or process CPU savings' } elseif ($Scenario -eq 'SvgMaterialization') { 'actual compiled SvgIcon constructor/BAML and first layout in owned STA child; process-local existing WPF headless-render switch before MediaContext, no Application or shown HWND; frozen original markup oracle only for separate behavior tests, never timed; counters/render digests/private Setter diagnostic outside timing; same current fixture with archived baseline DLL; cumulative managed allocation and offscreen pixels, not native heap/render cadence/process CPU' } elseif ($Scenario -eq 'HookReplacement') { 'actual synchronous shared policy called by existing keyboard/mouse message loops; fake install/unhook/UTC callbacks, no actual hook or message loop; explicit C9-HOOK-SEAM mechanical baseline; exact200installs/200ownedreleases/100fixtureexitcleanups/500clockreads; max2fakehandles preserves old until new succeeds, not a native leak; normal fixture cleanup is not actual shutdown proof;1s watchdog/strict5s cadence unchanged' } elseif ($Scenario -eq 'DisplayRemovalGc') { 'actual multi-display removal/routing/Stop/Dispose path with existing fake workspace/service adapters and injected collection-request callback; mandatory real-removal cleanup and routing asserted, no actual display/user-window/GC operation; late/reentrant/error/readd cases separately tested; call counts do not measure GC pauses/native heap' } elseif ($Scenario -eq 'ActionBarMaterialization') { 'actual production TilingWindow/SvgIcon XAML with generic WPF Application and real ModernWpf resources in a bounded child test process; no FancyWM.App, Window.Show, native icon discovery, real input or open menu; fixed app-local Mica inputs; all windows/PresentationSource absent, framework notification HWND not ruled out; counts distinguish eager logical objects and active visual paths; cumulative managed allocation/cost only, no retained/native heap or rendering benefit' } elseif ($Scenario -eq 'CursorInput') { 'actual production cursor scheduling/lifetime factory with fake native adapters; exact transformed point and WS_DISABLED/other style bits checked for every visible input; mechanical old-policy factory baseline separately identified, no original-C8 timing/allocation/native/hit-tree traversal measurement; failure/reentry/producer barriers covered separately' } elseif ($Scenario -eq 'SettingsStrings') { 'actual XAML control/template, owned no-HWND STA, real routed LostFocus/Measure/Arrange/UpdateLayout/two-way binding; fake bound-list host records exact source writes and presenter identity; source persistence/global App/native input not executed' } elseif ($Scenario -eq 'OverlayMaterialization') { 'actual production XAML and real Node-null viewmodels with deterministic CSS on owned STA, no App/HWND/hook; containers and VM identity checked before/after first layout; object counts distinguish logical declarations from deferred templates; allocation is cumulative managed cost, not retained/native heap or DWM/GPU/render timing' } elseif ($Scenario -eq 'GenericPreview') { 'actual public generic preview; measured allocation/timing/MinSize reads exclude explicitly labeled historical test model oracle and full original-tree/constraints/registration checks; exact preArrange/postArrange sequence digest compared across variants; no native desktop or persistent clone cache' } elseif ($Scenario -eq 'AnimationFrame') { 'actual production Frame, Stopwatch and IAnimationJob; synchronous stable frames, exact updates/progress/completion and separate asynchronous barrier/failure/retention tests; GC.GetAllocatedBytesForCurrentThread; no native clock/window/thread loop executed or cadence/CPU claim' } elseif ($Scenario -eq 'HotkeyCallback') { 'actual LowLevelHotkey subscribers and captured UI Dispatcher; native-free hook event owner with finalizer suppressed, bounded STA producer/UI barriers; exact suppression/emission digest; producer-thread allocation; timing covers all subscribers per input event; no real hook, native marshalling, CallNextHookEx, OS timeout or input latency claim' } elseif ($Scenario -eq 'PreviewCacheManaged') { 'actual public controller and workspace with field-backed adapters; fresh MinSize sampling/exact cache key/full uncached per-point oracle; GC.GetAllocatedBytesForCurrentThread and Stopwatch; no Moq invocation traffic or artificial native delays; new methodology reruns both assemblies, not directly comparable to Moq metrics' } elseif ($Scenario -eq 'ThemeLocalImage') { 'actual converter and first full pixel copy on owned STA without HWND;16/2048/4096 generated PNGs,exact BGRA digest/dimensions/tiling and cache identity; freshly written files imply warm OS cache,not cold disk I/O;GC only in separate100-lifetime ownership check;native heap/render cadence not measured' } elseif ($Scenario -eq 'TreeStateLookup') { 'actual tree-to-desktop-state lookup; owned fake desktop identities and real trees; exact matched state references, alias/null/mutation/lifetime tests separately; GC.GetAllocatedBytesForCurrentThread and Stopwatch, no native window/desktop calls' } elseif ($Scenario -eq 'WorkspaceInvariant') { 'actual workspace transactions; counted child traversal/MinSize adapters; full state, identities, registration,400 revisions and final rectangle digest asserted; GC.GetAllocatedBytesForCurrentThread includes adapter and assertions; no native desktop' } elseif ($Scenario -eq 'TransitionDispatch') { 'actual public transition scheduling on ThreadPool; fake SetPosition callbacks; process-wide GC.GetTotalAllocatedBytes precise includes worker allocations and adapter invocation tracking; Stopwatch with exact writes/final rectangles asserted after measurement; no native cadence or process CPU claim' } elseif ($Scenario -eq 'ReleaseChecker') { 'actual DI provider and ReleaseChecker with fake HTTP handler; exact stable version, singleton identity and idempotent Dispose asserted; no network or latency claim' } elseif ($Scenario -eq 'PostMoveTimer') { 'actual service/coordinator/timer scheduling and callback methods; deterministic manual ticks and fake window/desktop adapters; exact failure reason, released reservation,600 ownership probes and100 lifetimes asserted; no native COM or latency claim' } elseif ($Scenario -eq 'PreviewCache') { 'actual public preview preparation, exact cache key comparison and simulation with built-in nodes/fake MinSize; GC.GetAllocatedBytesForCurrentThread and Stopwatch include all public-call work, independent uncached semantic oracle outside timing; fresh preparation every pointer and full mouse-up preflight retained; no native cadence or process CPU claim' } elseif ($Scenario -eq 'FocusFallback') { 'real managed focus fallback/thread completion; fake native adapter only, no input injection or actual focus; exact successful attachment/detachment and returned bool checked; no native-hang/timeout/thread-pool bound or latency benefit claim' } elseif ($Scenario -eq 'IconDiscovery') { 'real public VM getter and bounded production cache; controlled blocked resolver, frozen pixels and managed fake windows; no native window or desktop operations; no compatible baseline or whole-process CPU measurement' } elseif ($Scenario -eq 'OwnershipTimer') { 'real DispatcherTimer scheduling/rearm/cancel methods; fake windows and deterministic manual stop between attempts; timer identity counters;6 attempts and50ms interval unchanged; no native COM or wall-time latency claim' } elseif ($Scenario -eq 'DisabledLogging') { 'real target calculation and Serilog level filter; fake window adapters; exact target count, identity and computed rectangles asserted; native property read proxy counters; isolated MSTest process' } elseif ($Scenario -eq 'DiagnosticReuse') { 'real engine transactions and invariant checks; CountingPanel child-access counters including clones; allocations include Moq and behavior assertions; full nonoverlapping rectangle coverage, exact master width, order and node/window identities verified; per-round checksum and full final rectangle SHA256 compared between variants; isolated MSTest process' } elseif ($Scenario -eq 'TransitionRounded') { 'real production transition group; fake window position adapters and controlled frames; exact final rectangles and final state asserted; native-call proxy counters only; isolated MSTest process' } elseif ($Scenario -eq 'ResizeTraversal') { 'real production ResizeNode; CountingPanel subtree enumeration; fake windows; exact target/sibling rectangles and node/window identity asserted; isolated MSTest process' } elseif ($Scenario -eq 'OverlayUpdates') { 'real renderer managed methods; fake display/window adapters; allocation includes adapter invocation tracking; exact VM identity/order/focus/preview assertions; no native overlay constructed' } elseif ($Scenario -eq 'OverlayAnimation') { 'real WPF code-behind handler; no-HWND STA controls and PropertyChanged inputs; GC.GetAllocatedBytesForCurrentThread; unchanged opacity/no-animation asserted' } elseif ($Scenario -eq 'SequenceHash') { 'real production sequence comparer; counted KeyCode element equality/hash calls; exact retained pattern identity/order and duplicate behavior asserted; isolated MSTest process' } elseif ($Scenario -eq 'ThemeReload') { 'real manager and converter for stable burst; controlled file/UI/event/timer adapters; synthetic converter for disposal cycles; isolated MSTest process; no compatible baseline' } elseif ($Scenario -eq 'SettingsDistinct') { 'real persistence owner and file replacement; overridden JSON serializer with barrier/write counter; isolated MSTest process' } elseif ($Scenario -eq 'SettingsRuntime') { 'real settings subscription/apply; fake display scaling, overlay and capacity counters; isolated MSTest process' } elseif ($Scenario -eq 'Mica') { 'real production Refresh; injected wallpaper and counters; isolated MSTest process' } elseif ($Scenario -eq 'CoordinatorDeadlines') { 'real production OnCleanupTimerTick and scheduling decisions; controlled clock; callback counter; isolated MSTest process' } else { 'real production CleanupExpired; GC.GetAllocatedBytesForCurrentThread; isolated MSTest process' }
                    perf_id = if ($Scenario -eq 'FocusBounded') { 'PERF-030' } elseif ($Scenario -eq 'AppAsyncShutdown') { 'PERF-016' } elseif ($Scenario -eq 'DependentShutdown') { 'PERF-016' } elseif ($Scenario -eq 'AnimationShutdown') { 'PERF-016' } elseif ($Scenario -eq 'ClockBoost') { 'PERF-010' } elseif ($Scenario -eq 'FocusSupersession') { 'PERF-030' } elseif ($Scenario -eq 'ConstructionLifetime') { 'PERF-016' } elseif ($Scenario -eq 'LayoutCadence') { 'PERF-004' } elseif ($Scenario -in @('BindingListener','MainWindowLifetime')) { 'PERF-016' } elseif ($Scenario -eq 'AppShutdown') { 'PERF-016' } elseif ($Scenario -eq 'KeyPressDisplay') { 'PERF-032' } elseif ($Scenario -eq 'SvgMaterialization') { 'PERF-028' } elseif ($Scenario -eq 'HookReplacement') { 'PERF-017' } elseif ($Scenario -eq 'DisplayRemovalGc') { 'PERF-018' } elseif ($Scenario -eq 'CursorInput') { 'PERF-021' } elseif ($Scenario -eq 'SettingsStrings') { 'PERF-032' } elseif ($Scenario -in @('OverlayMaterialization','ActionBarMaterialization')) { 'PERF-028' } elseif ($Scenario -eq 'GenericPreview') { 'PERF-009' } elseif ($Scenario -eq 'AnimationFrame') { 'PERF-010' } elseif ($Scenario -eq 'HotkeyCallback') { 'PERF-031' } elseif ($Scenario -eq 'PreviewCacheManaged') { 'PERF-007' } elseif ($Scenario -eq 'ThemeLocalImage') { 'PERF-035' } elseif ($Scenario -eq 'TreeStateLookup') { 'PERF-022' } elseif ($Scenario -eq 'WorkspaceInvariant') { 'PERF-008' } elseif ($Scenario -eq 'TransitionDispatch') { 'PERF-010' } elseif ($Scenario -eq 'ReleaseChecker') { 'PERF-016' } elseif ($Scenario -eq 'PostMoveTimer') { 'PERF-022' } elseif ($Scenario -eq 'PreviewCache') { 'PERF-007' } elseif ($Scenario -eq 'FocusFallback') { 'PERF-030' } elseif ($Scenario -eq 'IconDiscovery') { 'PERF-023' } elseif ($Scenario -eq 'OwnershipTimer') { 'PERF-022' } elseif ($Scenario -eq 'DisabledLogging') { 'PERF-019' } elseif ($Scenario -eq 'DiagnosticReuse') { 'PERF-008' } elseif ($Scenario -eq 'TransitionRounded') { 'PERF-010' } elseif ($Scenario -eq 'ResizeTraversal') { 'PERF-009' } elseif ($Scenario -like 'Overlay*') { 'PERF-029' } elseif ($Scenario -eq 'SequenceHash') { 'PERF-033' } elseif ($Scenario -eq 'ThemeReload') { 'PERF-024' } elseif ($Scenario -like 'Settings*') { 'PERF-014' } elseif ($Scenario -eq 'Mica') { 'PERF-013' } else { 'PERF-020' }
                    variant = $variant
                    pair = if (!$isComparison) { 0 } else { $run }
                    binary_sha256 = if ($variant -eq 'baseline') { $binaryRecord.Hash } else { $binaryHash }
                    benchmark_sha256 = $benchmarkHash
                }
                if ($Scenario -eq 'LayoutCallbackCache') {
                    $counterRow.scenario = "Actual LayoutInvalidationQueue callback scheduling; $($match.Groups[1].Value);1000 discarded warmups then10000 completed invalidation/drain cycles"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 0
                    $counterRow.unit = $layoutCallbackUnits[$counterRow.metric]
                    $counterRow.iterations = 10000
                    $counterRow.instrument = $layoutCallbackInstrument
                    $counterRow.perf_id = 'PERF-004'
                }
                if ($Scenario -eq 'FocusRejectedAdmission') {
                    $counterRow.scenario = "Actual FocusHelper.RequestSequence.Enqueue; $($match.Groups[1].Value);1000 discarded warmups then100000 calls"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 0
                    $counterRow.unit = $focusRejectedUnits[$counterRow.metric]
                    $counterRow.iterations = 100000
                    $counterRow.instrument = $focusRejectedInstrument
                    $counterRow.perf_id = 'PERF-030'
                }
                if ($Scenario -eq 'ContainsWindow') {
                    $counterRow.scenario = "Actual MasterSatelliteLayoutEngine.ContainsWindow; $($match.Groups[1].Value);1000 discarded warmups then100000 calls"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 1 + [int]$match.Groups[1].Value.Split('-')[-1]
                    $counterRow.unit = $containsWindowUnits[$counterRow.metric]
                    $counterRow.iterations = 100000
                    $counterRow.instrument = $containsWindowInstrument
                    $counterRow.perf_id = 'PERF-008'
                }
                if ($Scenario -eq 'ArrangeFailureBookkeeping') {
                    $bookkeepingParts = $match.Groups[1].Value.Split('-')
                    $counterRow.scenario = "Actual TilingService.UpdateTree arrange-failure bookkeeping; $($match.Groups[1].Value);256 discarded warmups then100 successful calls;empty new-window set;pending-state verified after every call"
                    $counterRow.window_count = [int]$bookkeepingParts[-2]
                    if ($counterRow.metric -cnotin $arrangeFailureBookkeepingUnits.Keys) { throw "Unknown arrange-failure bookkeeping metric: $($counterRow.metric)" }
                    $counterRow.unit = $arrangeFailureBookkeepingUnits[$counterRow.metric]
                    $counterRow.iterations = 100
                    $counterRow.instrument = $arrangeFailureBookkeepingInstrument
                    $counterRow.perf_id = 'PERF-006'
                }
                if ($Scenario -eq 'WindowStateLookup') {
                    $lookupParts = $match.Groups[1].Value.Split('-')
                    $counterRow.scenario = "TilingWorkspaceState.FindByVdm/FindByTree; $($match.Groups[1].Value);1000 alternating resident/missing warmups then100000 alternating lookups"
                    $counterRow.window_count = [int]$lookupParts[-1]
                    $counterRow.unit = switch ($counterRow.metric) {
                        'allocated-bytes' { 'bytes/100000 lookups' }
                        'elapsed-ticks' { 'ticks/100000 lookups' }
                        'timestamp-frequency' { 'ticks/second' }
                        'lookups' { 'lookups/scenario' }
                        'matched-lookups' { 'exact-results/scenario' }
                        'resident-hits' { 'resident-results/scenario' }
                        'probes' { 'membership-probes/scenario' }
                        'desktop-hash-reads' { 'hash-reads/scenario' }
                        default { throw "Unknown window-state lookup metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100000
                    $counterRow.instrument = 'Actual TilingWorkspaceState.FindByVdm/FindByTree over Dictionary.Keys/Values;field-backed IVirtualDesktop/IWindow adapters and real DesktopTree dictionaries;alternating last-resident and missing queries force the same complete D-entry scan;exact selected state/null,probe count and final VDM dictionary hash reads asserted;GC.GetAllocatedBytesForCurrentThread and Stopwatch;common current fixture with separately archived hashed R0 FancyWM.dll;no cache,index,native window/COM call,whole-process CPU/GPU/energy or discovery-latency claim'
                    $counterRow.perf_id = 'PERF-022'
                }
                if ($Scenario -eq 'DisplayManageability') {
                    $counterRow.scenario = "TilingService.CanManage display scan; $($match.Groups[1].Value) foreign entries with primary display outside the snapshot;1000 alternating resident/missing warmups then100000 alternating lookups"
                    $counterRow.window_count = [int]$match.Groups[1].Value.Split('-')[-1]
                    $counterRow.unit = switch ($counterRow.metric) {
                        'allocated-bytes' { 'bytes/100000 lookups' }
                        'elapsed-ticks' { 'ticks/100000 lookups' }
                        'timestamp-frequency' { 'ticks/second' }
                        'lookups' { 'lookups/scenario' }
                        'manageable-results' { 'accepted-results/scenario' }
                        'foreign-results' { 'foreign-display-results/scenario' }
                        'position-reads' { 'position-reads/scenario' }
                        'snapshot-reads' { 'display-snapshot-reads/scenario' }
                        'equality-reads' { 'display-equality-reads/scenario' }
                        'bounds-reads' { 'display-bounds-reads/scenario' }
                        'resize-reads' { 'resize-reads/scenario' }
                        'move-reads' { 'move-reads/scenario' }
                        'pin-reads' { 'desktop-pin-reads/scenario' }
                        default { throw "Unknown display manageability metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100000
                    $counterRow.instrument = 'Actual private TilingService.CanManage bound once as a delegate;field-backed IWorkspace/IDisplayManager/IVirtualDesktopManager/IWindow adapters and covariant List<derived IDisplay> matching the pinned provider shape;window_count is M foreign snapshot entries and excludes the separately probed primary display;alternating last-display resident and outside-all windows force complete M-entry scans;exact result and property/probe counts asserted;GC.GetAllocatedBytesForCurrentThread and Stopwatch;common current fixture with separately archived hashed R0 FancyWM.dll;the real provider snapshot construction,COM/native calls,full discovery latency and whole-process CPU/GPU/energy are not measured'
                    $counterRow.perf_id = 'PERF-022'
                }
                if ($Scenario -eq 'DiscoveryWindowSnapshot') {
                    $counterRow.scenario = "Actual TilingService.DiscoverWindows owned pass snapshot; $($match.Groups[1].Value) tracked restored windows rejected by CanResize;1000 discarded warmups then100000 complete unchanged passes"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = [int]$match.Groups[1].Value.Split('-')[-1]
                    $counterRow.unit = switch ($counterRow.metric) {
                        'allocated-bytes' { 'bytes/100000 passes' }
                        'elapsed-ticks' { 'ticks/100000 passes' }
                        'timestamp-frequency' { 'ticks/second' }
                        'passes' { 'passes/scenario' }
                        'changed-passes' { 'changed-passes/scenario' }
                        'state-reads' { 'state-reads/scenario' }
                        'position-reads' { 'position-reads/scenario' }
                        'resize-reads' { 'resize-reads/scenario' }
                        'move-reads' { 'move-reads/scenario' }
                        'pin-reads' { 'desktop-pin-reads/scenario' }
                        'desktop-snapshot-reads' { 'desktop-snapshot-reads/scenario' }
                        'display-snapshot-reads' { 'display-snapshot-reads/scenario' }
                        default { throw "Unknown discovery window snapshot metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100000
                    $counterRow.instrument = 'Actual public TilingService.DiscoverWindows with its owned HashSet<IWindow> pass snapshot;field-backed IWorkspace/IWindow/IDisplay/IVirtualDesktop adapters;0/1/10/50 unique tracked restored windows lie inside the managed display and are rejected at CanResize;all property and provider reads plus unchanged return values are asserted;GC.GetAllocatedBytesForCurrentThread and Stopwatch;common current fixture with separately archived hashed R0 FancyWM.dll;full250ms sweep and snapshot isolation are retained;no native window/COM calls,provider discovery,wall-clock polling latency,whole-process CPU/GPU/energy or idle-wakeup claim'
                    $counterRow.perf_id = 'PERF-002'
                }
                if ($Scenario -eq 'RefreshWindowSnapshot') {
                    $counterRow.scenario = "Actual TilingService.Refresh owned pass snapshot; $($match.Groups[1].Value) tracked restored windows rejected by CanResize;1000 discarded warmups then100000 complete unchanged passes"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = [int]$match.Groups[1].Value.Split('-')[-1]
                    if ($counterRow.metric -cnotin $refreshWindowSnapshotUnits.Keys) { throw "Unknown refresh window snapshot metric: $($counterRow.metric)" }
                    $counterRow.unit = $refreshWindowSnapshotUnits[$counterRow.metric]
                    $counterRow.iterations = 100000
                    $counterRow.instrument = $refreshWindowSnapshotInstrument
                    $counterRow.perf_id = 'PERF-002'
                }
                if ($Scenario -eq 'TransitionCompletion') {
                    $counterRow.scenario = "Actual Tasks.WhenAllIgnoreCancelled completion aggregation; $($match.Groups[1].Value);1000 discarded warmups then10000 awaited completions"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = [int]$match.Groups[1].Value.Split('-')[-1]
                    if ($counterRow.metric -cnotin $transitionCompletionUnits.Keys) { throw "Unknown transition completion metric: $($counterRow.metric)" }
                    $counterRow.unit = $transitionCompletionUnits[$counterRow.metric]
                    $counterRow.iterations = 10000
                    $counterRow.instrument = $transitionCompletionInstrument
                    $counterRow.perf_id = 'PERF-010'
                }
                if ($Scenario -eq 'RuntimeStateCount') {
                    $counterRow.scenario = "Actual MasterSatelliteRuntimeSession.StateCount through coordinator and registry; $($match.Groups[1].Value);1000 discarded common warmups then10000 queries"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = [int]$match.Groups[1].Value.Split('-')[-1]
                    if ($counterRow.metric -cnotin $runtimeStateCountUnits.Keys) { throw "Unknown runtime state count metric: $($counterRow.metric)" }
                    $counterRow.unit = $runtimeStateCountUnits[$counterRow.metric]
                    $counterRow.iterations = 10000
                    $counterRow.instrument = $runtimeStateCountInstrument
                    $counterRow.perf_id = 'PERF-022'
                }
                if ($Scenario -eq 'PanelIndex') {
                    $counterRow.scenario = "PanelNode.IndexOf exact built-in List<TilingNode>; $($match.Groups[1].Value) children;1000 alternating last/missing warmups then100000 alternating lookups"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = [int]$match.Groups[1].Value.Split('-')[-1]
                    $counterRow.unit = switch ($counterRow.metric) {
                        'allocated-bytes' { 'bytes/100000 lookups' }
                        'elapsed-ticks' { 'ticks/100000 lookups' }
                        'timestamp-frequency' { 'ticks/second' }
                        'lookups' { 'lookups/scenario' }
                        'last-index-sum' { 'summed-indices/scenario' }
                        'missing-index-sum' { 'summed-indices/scenario' }
                        default { throw "Unknown panel index metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100000
                    $counterRow.instrument = 'Actual public PanelNode.IndexOf on a SplitPanelNode whose built-in Children storage is an exact List<TilingNode>;alternating last-child and absent-node queries both force the expected scan depth;exact returned-index sums asserted;GC.GetAllocatedBytesForCurrentThread and Stopwatch;common current FancyWM.Layouts.Tests fixture with separately archived hashed R0 FancyWM.Layouts.dll;custom enumerable callbacks,exceptions,mutation,disposal,StackPanelNode and reference identity are covered by regressions outside timing;no FancyWM service,native window/desktop,COM,whole-process CPU/GPU/energy or application-latency claim'
                    $counterRow.perf_id = 'PERF-009'
                }
                if ($Scenario -eq 'EmbedIndex') {
                    $counterRow.scenario = "TilingNode.Embed last-child lookup and wrapping; $($match.Groups[1].Value) WindowNode children;1000 discarded warmups then10000 independent embeds in40 prebuilt batches"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = [int]$match.Groups[1].Value.Split('-')[-1]
                    $counterRow.unit = switch ($counterRow.metric) {
                        'allocated-bytes' { 'bytes/10000 embeds' }
                        'elapsed-ticks' { 'ticks/10000 embeds' }
                        'timestamp-frequency' { 'ticks/second' }
                        'embeds' { 'embeds/scenario' }
                        'slot-index-sum' { 'summed-original-slot-indices/scenario' }
                        'parent-links' { 'verified-parent-links/scenario' }
                        'registrations' { 'verified-window-registrations/scenario' }
                        'ordered-window-sum' { 'summed-ordered-window-ordinals/scenario' }
                        default { throw "Unknown Embed index metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 10000
                    $counterRow.instrument = 'Actual public TilingNode.Embed on independent DesktopTree/SplitPanelNode trees with built-in exact List<TilingNode> children and a new empty StackPanelNode wrapper;target is the last real WindowNode;1000 separate warmup fixtures;40 batches of250 fixtures are fully created before each timed/allocation region;setup and post-state assertions are excluded;all replacement slots,parent links,DesktopTree registrations and original ordered windows are verified afterward;GC.GetAllocatedBytesForCurrentThread and Stopwatch;common current FancyWM.Layouts.Tests fixture with separately archived hashed R0 FancyWM.Layouts.dll;custom enumeration,mutation,exception/disposal and split/stack behavior are covered by regressions;no native windows/desktops,COM,whole-process CPU/GPU/energy or application-latency claim'
                    $counterRow.perf_id = 'PERF-009'
                }
                if ($Scenario -eq 'SplitPanelMeasure') {
                    $counterRow.scenario = "SplitPanelNode.Measure post-traversal spacing count; $($match.Groups[1].Value) backing placeholder children;100000 repeated measures"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = [int]$match.Groups[1].Value.Split('-')[-1]
                    $counterRow.unit = switch ($counterRow.metric) {
                        'allocated-bytes' { 'bytes/100000 measures' }
                        'elapsed-ticks' { 'ticks/100000 measures' }
                        'timestamp-frequency' { 'ticks/second' }
                        'measures' { 'measures/scenario' }
                        'backing-children' { 'children/scenario' }
                        'published-width-sum' { 'summed-width-pixels/scenario' }
                        'published-height-sum' { 'summed-height-pixels/scenario' }
                        default { throw "Unknown SplitPanel measurement metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100000
                    $counterRow.instrument = 'Actual public SplitPanelNode.Measure;hidden custom Children isolates the post-traversal private backing-list spacing count,ordinary built-in Children includes the retained first interface enumeration;window_count records N backing PlaceholderNode children;spacing7 and padding1/2/3/4;published width and height sums are asserted;GC.GetAllocatedBytesForCurrentThread and Stopwatch;common current FancyWM.Layouts.Tests fixture with separately archived hashed R0 FancyWM.Layouts.dll;windows,derived nodes,callbacks,mutation,disposal and publication failures are covered by regressions outside timing;no native windows/desktops,COM,application process,whole-process CPU/GPU/energy or application-latency claim'
                    $counterRow.perf_id = 'PERF-009'
                }
                if ($Scenario -eq 'StackPanelMeasure') {
                    $counterRow.scenario = "StackPanelNode.Measure first virtual Children traversal; $($match.Groups[1].Value) backing fixed-size children;100000 repeated measures"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = [int]$match.Groups[1].Value.Split('-')[-1]
                    $counterRow.unit = switch ($counterRow.metric) {
                        'allocated-bytes' { 'bytes/100000 measures' }
                        'elapsed-ticks' { 'ticks/100000 measures' }
                        'timestamp-frequency' { 'ticks/second' }
                        'measures' { 'measures/scenario' }
                        'backing-children' { 'children/scenario' }
                        'published-width-sum' { 'summed-width-pixels/scenario' }
                        'published-height-sum' { 'summed-height-pixels/scenario' }
                        default { throw "Unknown StackPanel measurement metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100000
                    $counterRow.instrument = 'Actual public StackPanelNode.Measure;hidden custom Children is an empty-view control over a populated private backing list,ordinary Children traverses the owned backing list;window_count records N backing fixed-size SizedNode children with ContentMinSize 7x9;hidden published width/height sums are zero,ordinary sums are700000/900000;setup,warmup and assertions outside measurement;GC.GetAllocatedBytesForCurrentThread and Stopwatch;common current FancyWM.Layouts.Tests fixture with separately archived hashed R0 FancyWM.Layouts.dll;no native windows/desktops,COM,application process,whole-process CPU/GPU/energy or application-latency claim'
                    $counterRow.perf_id = 'PERF-009'
                }
                if ($Scenario -eq 'PlaceholderCleanup') {
                    $counterRow.scenario = "PanelNode.RemovePlaceholders; $($match.Groups[1].Value) fixed marker children;100000 operations"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = [int]$match.Groups[1].Value.Split('-')[-1]
                    $counterRow.unit = switch ($counterRow.metric) {
                        'allocated-bytes' { 'bytes/100000 operations' }
                        'elapsed-ticks' { 'ticks/100000 operations' }
                        'timestamp-frequency' { 'ticks/second' }
                        'operations' { 'operations/scenario' }
                        'retained-children' { 'children/scenario' }
                        'parent-links' { 'links/scenario' }
                        'ordered-slot-sum' { 'ordered-slot-checksum/scenario' }
                        'placeholder-windows-reads' { 'reads/100000 operations' }
                        default { throw "Unknown placeholder cleanup metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100000
                    $counterRow.instrument = 'Actual public PanelNode.RemovePlaceholders on fixed MarkerNode children;absent operation calls RemovePlaceholders,present operation includes Attach of one reusable CountingPlaceholder followed by RemovePlaceholders;derived-absent uses DerivedSplitPanel returning the exact base List<TilingNode>,which baseline enumerates through LINQ and candidate handles through the equivalent exact-list snapshot path;window_count records N retained MarkerNode children;1000 warmup operations,setup and assertions outside measurement;retained children,parent links,ordered-slot checksum and placeholder Windows reads are asserted;GC.GetAllocatedBytesForCurrentThread and Stopwatch;common current FancyWM.Layouts.Tests fixture with separately archived hashed R0 FancyWM.Layouts.dll;DOTNET_TieredCompilation=0;no native windows/desktops,COM,application process,whole-process CPU/GPU/energy or application-latency claim'
                    $counterRow.perf_id = 'PERF-009'
                }
                if ($Scenario -eq 'SettingsDispose') {
                    $counterRow.scenario = "SettingsViewModel.Dispose failure cleanup; $($match.Groups[1].Value);100 lifetimes"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'cycles' { 'lifetimes/scenario' }
                        'subscription-dispose-attempts' { 'dispose-calls/scenario' }
                        'released-subscriptions' { 'subscriptions/scenario' }
                        'flush-attempts' { 'flush-calls/scenario' }
                        'primary-outcomes' { 'verified-outcomes/scenario' }
                        'late-notifications' { 'lifetimes-with-notification/after-dispose' }
                        'late-keybinding-mutations' { 'lifetimes-with-mutation/after-dispose' }
                        'repeat-dispose-errors' { 'errors/scenario' }
                        'active-subscriptions' { 'subscriptions/after-cycles' }
                        'log-attempts' { 'log-calls/scenario' }
                        default { throw "Unknown SettingsViewModel dispose metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual public SettingsViewModel.Dispose with real KeybindingViewModel subscriptions and managed RecordingSettingsEntity/ILogger fakes;100 independent lifetimes per normal,subscription-failure and logger-failure case;failure fakes release before throwing and subscription-failure reenters Dispose;common current test fixture against separately archived hashed R0 FancyWM.dll;counts only,no file IO,FancyWM.App,shown windows or desktop-management operations,no timing,allocation,retained heap,process CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'StartupWindowClose') {
                    $counterRow.scenario = 'Actual StartupWindow.Close before first Show; throwing SettingsViewModel cleanup and throwing Closed subscriber;100 lifetimes'
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'cycles' { 'lifetimes/scenario' }
                        'subscription-dispose-attempts' { 'dispose-calls/scenario' }
                        'released-subscriptions' { 'subscriptions/scenario' }
                        'flush-attempts' { 'flush-calls/scenario' }
                        'primary-outcomes' { 'verified-outcomes/scenario' }
                        'closed-callbacks' { 'callbacks/scenario' }
                        'supplemental-errors' { 'recorded-errors/scenario' }
                        'repeat-close-errors' { 'errors/scenario' }
                        'nonzero-handles' { 'handle-observations/scenario' }
                        'active-subscriptions' { 'subscriptions/after-cycles' }
                        default { throw "Unknown StartupWindow close metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual public StartupWindow.Close on a dedicated STA before Show with real XAML control construction and actual SettingsViewModel.Dispose;WindowInteropHelper.Handle is read before and after Close and required to remain zero;managed settings failures release before throwing and the Closed subscriber throws;common current test fixture against separately archived hashed R0 FancyWM.dll;counts only,no Application.Run,Show,ShowDialog,EnsureHandle,user-desktop operations,timing,allocation,retained heap,process CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'SettingsWindowClose') {
                    $counterRow.scenario = "Actual SettingsWindow.Close before first Show; $($match.Groups[1].Value);100 lifetimes"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'cycles' { 'lifetimes/scenario' }
                        'subscription-dispose-attempts' { 'dispose-calls/scenario' }
                        'released-subscriptions' { 'subscriptions/scenario' }
                        'flush-attempts' { 'flush-calls/scenario' }
                        'primary-outcomes' { 'verified-outcomes/scenario' }
                        'closed-callbacks' { 'callbacks/scenario' }
                        'supplemental-errors' { 'recorded-errors/scenario' }
                        'retained-logical-focus' { 'lifetimes-with-logical-focus/after-close' }
                        'collection-posts' { 'posts/scenario' }
                        'repeat-close-errors' { 'errors/scenario' }
                        'nonzero-handles' { 'handle-observations/scenario' }
                        'active-subscriptions' { 'subscriptions/after-cycles' }
                        default { throw "Unknown SettingsWindow close metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual public SettingsWindow.Close on a dedicated STA in an isolated child process with owned FancyWM.App resources and real XAML control construction before Show;actual SettingsViewModel.Dispose with managed settings subscriptions that release before throwing;100 independent normal,viewmodel-failure and combined Closed/viewmodel-failure lifetimes each;Closed reenters Close and each owner is closed again;exact first and supplemental exception identities,logical focus and Dispatcher ApplicationIdle posts observed;posted collection operations are counted then aborted without running collection;WindowInteropHelper.Handle is read before and after Close and required to remain zero;common current test fixture against separately archived hashed R0 FancyWM.dll;counts only,no Application.Run,Show,ShowDialog,EnsureHandle,user-desktop operations,settings-file IO,timing,allocation,retained heap,process CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'OverlayHostClose') {
                    $counterRow.scenario = "Actual OverlayHost.CompleteClose managed ownership; $($match.Groups[1].Value);100 lifetimes per case per process"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'lifetimes' { 'lifetimes/case/process' }
                        'invalidations' { 'state-invalidations/case/process' }
                        'cursor-attempts' { 'dispose-attempts/case/process' }
                        'cursor-releases' { 'managed-owners-released/case/process' }
                        'refresh-attempts' { 'dispose-attempts/case/process' }
                        'refresh-releases' { 'managed-owners-released/case/process' }
                        'subscription-attempts' { 'unsubscribe-attempts/case/process' }
                        'subscription-releases' { 'managed-subscriptions-released/case/process' }
                        'dispatch-attempts' { 'managed-dispatch-callbacks/case/process' }
                        'nonhit-close-attempts' { 'managed-close-attempts/case/process' }
                        'nonhit-close-releases' { 'managed-owners-released/case/process' }
                        'hit-close-attempts' { 'managed-close-attempts/case/process' }
                        'hit-close-releases' { 'managed-owners-released/case/process' }
                        'primary-outcomes' { 'caught-exceptions/case/process' }
                        'supplemental-errors' { 'recorded-errors/case/process' }
                        'repeat-actions' { 'cleanup-actions/repeated-close/case/process' }
                        'active-owners' { 'managed-owners/after-case/process' }
                        default { throw "Unknown OverlayHost close metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production OverlayHost.CompleteClose with managed cleanup delegates;integration into public Close is code-confirmed;100 independent lifetimes per normal,cursor-failure and nonhit-close-failure case per process;each lifetime owns one cursor input,one refresh loop,five subscriptions and two window-close adapters;controlled failures release their managed owner before throwing;primary-outcomes counts caught exceptions,supplemental-errors counts attached later errors and active-owners counts unreleased managed adapters;repeated Close admission is probed after every lifetime;five alternating baseline/candidate pairs using the same current fixture and separately archived hashed mechanical sequential-close-seam FancyWM.dll;counts only,no actual OverlayHost,Window,App or HWND construction,WPF Dispatcher execution,native close success,timing,allocation,retained heap,process CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'OverlayWindowClose') {
                    $counterRow.scenario = "Actual OverlayHost.CompleteOverlayWindowClose managed ownership; $($match.Groups[1].Value);100 lifetimes per case per process"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'lifetimes' { 'lifetimes/case/process' }
                        'base-attempts' { 'managed-base-close-callbacks/case/process' }
                        'settings-attempts' { 'dispose-attempts/case/process' }
                        'settings-releases' { 'managed-owners-released/case/process' }
                        'workarea-attempts' { 'unsubscribe-attempts/case/process' }
                        'workarea-releases' { 'managed-subscriptions-released/case/process' }
                        'scaling-attempts' { 'unsubscribe-attempts/case/process' }
                        'scaling-releases' { 'managed-subscriptions-released/case/process' }
                        'content-attempts' { 'managed-content-clear-attempts/case/process' }
                        'content-releases' { 'managed-owners-released/case/process' }
                        'primary-outcomes' { 'caught-exceptions/case/process' }
                        'supplemental-errors' { 'recorded-errors/case/process' }
                        'active-owners' { 'managed-owners/after-case/process' }
                        default { throw "Unknown OverlayWindow close metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production OverlayHost.CompleteOverlayWindowClose with managed fake owners only;integration into OverlayWindow.OnClosed is code-confirmed;100 independent lifetimes per normal,base-failure and settings-failure case per process;four managed owners represent settings,WorkArea,Scaling and Content;the controlled settings failure releases its owner before throwing;primary-outcomes counts caught primary exceptions with exact identity asserted,supplemental-errors counts attached later errors and active-owners counts unreleased managed adapters;five alternating baseline/candidate pairs using the same current fixture and separately archived hashed mechanical sequential-OnClosed-seam FancyWM.dll;counts only,no actual OverlayHost,Window,App or HWND construction,WPF Dispatcher execution,native close success,timing,allocation,retained heap,process CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'OverlayWindowCallbacks') {
                    $counterRow.scenario = "Actual OverlayHost queued callback admission; $($match.Groups[1].Value);100 lifetimes per case per process"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 0
                    $counterRow.unit = switch -CaseSensitive ($counterRow.metric) {
                        'lifetimes' { 'lifetimes/case/process' }
                        'posts' { 'managed-queue-posts/case/process' }
                        'drained-callbacks' { 'managed-callbacks-drained/case/process' }
                        'transform-applies' { 'managed-transform-adapter-calls/case/process' }
                        'resource-applies' { 'managed-resource-adapter-calls/case/process' }
                        'owner-releases' { 'managed-owners-released/case/process' }
                        'pending-callbacks' { 'managed-callbacks/after-case/process' }
                        default { throw "Unknown OverlayWindow callback metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production OverlayHost.QueueOverlayWindowCallback,QueueOverlayWindowSettingsCallback,TryBeginOverlayWindowClose and CompleteOverlayWindowClose with managed state and delegates;integration into nested OverlayWindow settings,ScalingChanged,WorkAreaChanged and OnClosed paths is code-confirmed;100 independent lifetimes for each settings,scaling,workarea input in active,queued-before-close,captured-after-close states per process;caller-drained Queue<Action> replaces Dispatcher delivery;transform-applies and resource-applies count managed adapter calls,not native work or actual resource writes;four released managed owners represent settings,WorkArea,Scaling and Content;exact per-lifetime callback order and full outcomes are asserted;five alternating baseline/candidate pairs use the same current fixture and separately archived hashed mechanical unguarded callback SEAM-R2 FancyWM.dll with accepted failure-safe cleanup retained;counts only,no OverlayHost or OverlayWindow construction,Window,App or HWND,actual Dispatcher or native display execution,timing,allocation,retained heap,process CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'TilingOverlayRendererDispose') {
                    $counterRow.scenario = "Actual TilingOverlayRenderer.CompleteDispose managed ownership; $($match.Groups[1].Value);100 lifetimes per case per process"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 0
                    $counterRow.unit = switch -CaseSensitive ($counterRow.metric) {
                        'lifetimes' { 'lifetimes/case/process' }
                        'capture-attempts' { 'managed-content-capture-attempts/case/process' }
                        'drag-attempts' { 'managed-drag-handler-release-attempts/case/process' }
                        'drag-releases' { 'managed-owners-released/case/process' }
                        'overlay-attempts' { 'managed-close-attempts/case/process' }
                        'overlay-releases' { 'managed-owners-released/case/process' }
                        'viewmodel-attempts' { 'dispose-attempts/case/process' }
                        'viewmodel-releases' { 'managed-owners-released/case/process' }
                        'display-attempts' { 'unsubscribe-attempts/case/process' }
                        'display-releases' { 'managed-owners-released/case/process' }
                        'subscriptions-attempts' { 'managed-subscription-group-dispose-attempts/case/process' }
                        'subscriptions-releases' { 'managed-owners-released/case/process' }
                        'invalidate-attempts' { 'managed-invalidation-attempts/case/process' }
                        'invalidate-releases' { 'managed-owners-released/case/process' }
                        'events-attempts' { 'managed-event-clear-attempts/case/process' }
                        'events-releases' { 'managed-owners-released/case/process' }
                        'primary-outcomes' { 'caught-exceptions/case/process' }
                        'supplemental-errors' { 'recorded-errors/case/process' }
                        'repeat-actions' { 'cleanup-actions/repeated-dispose/case/process' }
                        'active-owners' { 'managed-owners/after-case/process' }
                        default { throw "Unknown TilingOverlayRenderer dispose metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production TilingOverlayRenderer.CompleteDispose with managed fake owners only;integration into public Dispose is code-confirmed;100 independent lifetimes per normal,overlay-failure and viewmodel-failure case per process;seven representative managed owners denote drag handlers,overlay,viewmodel,display subscription,settings subscription group,invalidation and outgoing events after content capture;controlled failures release their managed owner before throwing;primary-outcomes counts caught primary exceptions with exact identity asserted,supplemental-errors counts attached later errors and active-owners counts unreleased managed adapters;repeated Dispose admission is probed after every lifetime;five alternating baseline/candidate pairs use the same current fixture and separately archived hashed mechanical sequential-Dispose-seam FancyWM.dll;counts only,no actual renderer or OverlayHost construction,Window,App or HWND,WPF Dispatcher execution,routed drag-handler removal,actual viewmodel invalidation,native close success,timing,allocation,retained heap,process CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'TilingOverlayRendererResources') {
                    $counterRow.scenario = "Actual TilingOverlayRenderer.UpdateResourcesCore resource callback admission; $($match.Groups[1].Value);100 lifetimes per case per process"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 0
                    $counterRow.unit = switch -CaseSensitive ($counterRow.metric) {
                        'lifetimes' { 'lifetimes/case/process' }
                        'posts' { 'managed-queue-posts/case/process' }
                        'drained-callbacks' { 'managed-callbacks-drained/case/process' }
                        'scaling-reads' { 'fake-display-scaling-reads/case/process' }
                        'resource-writes' { 'resource-assignment-attempts/case/process' }
                        'subscription-releases' { 'managed-subscriptions-released/case/process' }
                        'pending-callbacks' { 'managed-callbacks/after-case/process' }
                        default { throw "Unknown TilingOverlayRenderer resource callback metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production TilingOverlayRenderer.UpdateResourcesCore and DisposeCore with managed field-initialized renderer,viewmodel and fake display;integration into UpdateResources and settings/display callbacks is code-confirmed;100 independent lifetimes per active,queued-before-dispose and captured-after-dispose case per process;injected access check and caller-drained Queue<Action> replace Dispatcher delivery;scaling-reads counts fake display getter calls,resource-writes counts scalar assignment attempts and subscription-releases counts an owned managed subscription released by the real disposal path;exact resource values are checked separately from counters;five alternating baseline/candidate pairs use the same current fixture and separately archived hashed mechanical unguarded-resource-callback-seam FancyWM.dll;counts only,no public renderer or OverlayHost constructor,Window,App or HWND,actual Dispatcher execution,native display read,timing,allocation,retained heap,process CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'TilingOverlayRendererRemoval') {
                    $counterRow.scenario = "Actual TilingOverlayRenderer.UpdateViewModels detached-model cleanup; $($match.Groups[1].Value);100 create/remove cycles per case per process"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 1
                    $counterRow.unit = switch -CaseSensitive ($counterRow.metric) {
                        'cycles' { 'create/remove cycles/case/process' }
                        'removal-notifications' { 'collection notifications/case/process' }
                        'primary-outcomes' { 'caught original exceptions/case/process' }
                        'detached-models' { 'models removed from dictionary and collection/case/process' }
                        'cursor-subscriptions-after-invalidation' { 'subscriptions after100 cycles and repeated invalidation' }
                        'node-references-after-invalidation' { 'owned node references after100 cycles and repeated invalidation' }
                        'command-references-after-invalidation' { 'owned command references after100 cycles and repeated invalidation' }
                        'models-needing-fixture-cleanup' { 'models requiring extra fixture cleanup/case/process' }
                        'cursor-subscriptions-after-fixture-cleanup' { 'subscriptions after extra fixture cleanup/case/process' }
                        default { throw "Unknown TilingOverlayRenderer removal metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production TilingOverlayRenderer.UpdateViewModels and InvalidateView with existing managed field-initialized renderer and fake workspace/window adapters;100 normal and100 removal-notification-failure cycles on one renderer per case,then repeated invalidation;exact original error identity and empty dictionary/collection/snapshot checked;bounded list deliberately holds100 models for field inspection;cursor subscriptions,node and command references are observed before explicit fixture repair and zero owners verified afterwards;five alternating baseline/candidate pairs use the same current fixture and separately archived hashed R0 FancyWM.dll;counts only,no public renderer or OverlayHost constructor,App,Window,HWND,native display/window operations,timing,allocation,retained heap,process CPU/GPU or energy measurement;panel,double-failure and reentrant cleanup covered by separate strict regressions,not these counters'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'TilingOverlayRendererCreation') {
                    $counterRow.scenario = "Actual TilingOverlayRenderer.CreateViewModel unpublished-model cleanup; $($match.Groups[1].Value);100 creation cycles per case per process"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 1
                    $counterRow.unit = switch -CaseSensitive ($counterRow.metric) {
                        'cycles' { 'creation cycles/case/process' }
                        'title-reads' { 'fake window Title getter calls/case/process' }
                        'created-models' { 'captured created models/case/process' }
                        'published-models' { 'models published to dictionary and collection/case/process' }
                        'primary-outcomes' { 'caught original Title exceptions/case/process' }
                        'cursor-subscriptions-after-invalidation' { 'cursor subscriptions after100 cycles and repeated invalidation' }
                        'window-subscriptions-after-invalidation' { 'window event subscription balance after100 cycles and repeated invalidation' }
                        'node-references-after-invalidation' { 'owned node references after100 cycles and repeated invalidation' }
                        'models-needing-fixture-cleanup' { 'models requiring extra fixture cleanup/case/process' }
                        'cursor-subscriptions-after-fixture-cleanup' { 'cursor subscriptions after extra fixture cleanup/case/process' }
                        'window-subscriptions-after-fixture-cleanup' { 'window event subscription balance after extra fixture cleanup/case/process' }
                        default { throw "Unknown TilingOverlayRenderer creation metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production TilingOverlayRenderer.UpdateOverlay/CreateViewModel and InvalidateView with existing managed field-initialized renderer and fake workspace/window adapters;100 normal and100 Title-read-failure creation cycles on one renderer per case,with repeated invalidation after each cycle;exact original error identity and failed publication checked,normal Node/title/focus/model identity preserved;bounded list deliberately holds100 models for field inspection;cursor subscriptions,window-event add/remove balances and node references are observed before explicit fixture repair and zero balances verified afterwards;window events count Removed twice plus PositionChangeStart,PositionChangeEnd and TitleChanged once each per created model;five alternating baseline/candidate pairs use the same current fixture and separately archived hashed R0 FancyWM.dll;counts only,no public renderer or OverlayHost constructor,App,Window,HWND,native operations,timing,allocation,retained heap,process CPU/GPU or energy measurement;bounds,panel,duplicate registration,reentry and double failures covered by separate strict regressions,not these counters'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'TilingOverlayRendererRecovery') {
                    $counterRow.scenario = "Actual TilingOverlayRenderer.UpdateOverlay same-snapshot recovery; $($match.Groups[1].Value);100 cycles with four initial windows and two requested survivors per case per process"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = 4
                    if ($counterRow.metric -cnotin $overlayRecoveryUnits.Keys) { throw "Unknown TilingOverlayRenderer recovery metric: $($counterRow.metric)" }
                    $counterRow.unit = $overlayRecoveryUnits[$counterRow.metric]
                    $counterRow.iterations = 100
                    $counterRow.instrument = $overlayRecoveryInstrument
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'TilingWindowLifetime') {
                    $counterRow.scenario = "Actual TilingWindowViewModel lifetime; $($match.Groups[1].Value);100 independent managed lifetimes per case per process"
                    $counterRow.configuration = "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
                    $counterRow.window_count = if ($match.Groups[1].Value -ceq 'removal-failure') { 1 } else { 2 }
                    if ($counterRow.metric -cnotin $tilingWindowLifetimeUnits.Keys) { throw "Unknown TilingWindowLifetime metric: $($counterRow.metric)" }
                    $counterRow.unit = $tilingWindowLifetimeUnits[$counterRow.metric]
                    $counterRow.iterations = 100
                    $counterRow.instrument = $tilingWindowLifetimeInstrument
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'GenericPreview') {
                    $genericPreviewProcessMode = if ($normalizedGenericPreviewCase) { "single case $normalizedGenericPreviewCase" } else { 'all nine cases in fixed order' }
                    $counterRow.scenario += "; process mode: $genericPreviewProcessMode"
                    $counterRow.instrument += "; process mode: $genericPreviewProcessMode"
                }
                if ($Scenario -eq 'HookLifetime') {
                    $counterRow.scenario = "Actual production low-level hook loop; $($match.Groups[1].Value);100 dispatch-failure and100 normal exits"
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'cycles' { 'cycles/scenario' }
                        'installed-hooks' { 'fake-handles/scenario' }
                        'timer-creations' { 'fake-timers/scenario' }
                        'exceptional-remaining-hooks' { 'fake-handles/after-loop' }
                        'original-loop-errors' { 'errors/scenario' }
                        'normal-completions' { 'completions/scenario' }
                        default { 'cleanup-calls/scenario' }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production keyboard/mouse HookThreadMessageLoop with controlled message/native delegates and fake owned handles/timers;100 dispatch failures and100 normal exits per owner;exact primary error identity and balanced normal cleanup;mechanical adapter-only old-loop baseline and same current fixture against separately archived hashed FancyWM.dll;PERF-017 replacement and1s watchdog/strict5s retry preserved;counts only,no native hook,input,window,thread startup/Dispose completion,retained native handles,latency or CPU/timer wakeup measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'HookDisposal') {
                    $counterRow.scenario = "Actual production low-level hook owner; $($match.Groups[1].Value);100 failed-post disposals and late callbacks after completion"
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'cycles' { 'cycles/scenario' }
                        'quit-post-attempts' { 'post-calls/scenario' }
                        'observed-post-failures' { 'errors/scenario' }
                        'completion-successes' { 'completions/scenario' }
                        'late-subscriber-callbacks' { 'callback-calls/scenario' }
                        'suppressed-late-input' { 'events/scenario' }
                        'completed-workers' { 'workers/scenario' }
                        'live-workers-after-join' { 'workers/after-join' }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual keyboard/mouse constructors,managed worker wrappers,Dispose,ThreadLifetime and callback admission;100 lifetimes per owner without discarded warmup;controlled publication and worker-release gates,fake queue identities and failed quit posts;repeated Dispose,observed post failures,late callback/suppression and worker completion counts;bounded managed joins;mechanical startup/disposal seam baseline and same current fixture against separately archived hashed FancyWM.dll;counts only,no native hook,message queue,input,window,retained native handles,shutdown latency,watchdog wakeup or process CPU measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'LowLevelPatternLifetime') {
                    $counterRow.scenario = 'Actual LowLevelKeyPatternListener;100 cycles of six normal,queued,disposed,stale,captured-release and repeat-down lifetimes on one borrowed hook event owner'
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'cycles' { 'cycles/scenario' }
                        'normal-notifications' { 'notifications/scenario' }
                        'queued-notifications' { 'notifications/scenario' }
                        'incorrect-queued-snapshots' { 'patterns/scenario' }
                        'late-notifications' { 'notifications/scenario' }
                        'stale-unrelated-suppressions' { 'events/scenario' }
                        'lost-owned-releases' { 'events/scenario' }
                        'retained-drain-subscriptions' { 'subscriptions/after-captured-release' }
                        'remaining-subscriptions' { 'subscriptions/after-fixture-cleanup' }
                        default { throw "Unknown pattern lifetime metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual listener constructor,managed hook event callback,Dispose and queued WPF Dispatcher notification on an owned bounded STA;600 listener lifetimes without discarded warmup;normal Ctrl+A suppression and first-release set checked;late-notifications aggregates queued-before-Dispose,stale unrelated input and captured owned release;retained subscriptions observed before an identical extra fixture cleanup release in both variants;zero subscriptions checked between every lifetime;unchanged current fixture with separately archived hashed R0/candidate FancyWM.dll;counts only,no real hook,input injection,FancyWM.App,shown window,timing,allocation,retained memory,native handles or process CPU/GPU measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'DelayedCommandLifetime') {
                    $counterRow.scenario = 'Actual MainWindowLifetime delayed-command admission used by MainWindow.OnCommandKey;100 normal and100 shutdown-during-delay lifetimes'
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'cycles' { 'cycles/scenario' }
                        'delay-attempts' { 'delays/scenario' }
                        'cancellations' { 'cancellations/scenario' }
                        'completed-runs' { 'completions/scenario' }
                        default { 'action-calls/scenario' }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production MainWindowLifetime.RunDelayedIfActiveAsync wired into MainWindow.OnCommandKey;one immediate normal and one controlled accepted-then-shutdown delay per cycle;same current fixture against a separately archived hashed mechanical old-order FancyWM.dll;strict regressions separately cover pre-cancel,queued continuation,token/error identity and Dispatcher affinity;counts only,no MainWindow/App/HWND,real 50ms wait,command execution,native input,focus,latency,allocation,retained memory,CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'VirtualDesktopCallbackLifetime') {
                    $counterRow.scenario = 'Actual MainWindowLifetime callback admission used by MainWindow.OnVirtualDesktopChanged;100 active and100 disposed lifetimes'
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'cycles' { 'cycles/scenario' }
                        'active-state-writes' { 'state-writes/scenario' }
                        'late-state-writes' { 'state-writes/scenario' }
                        'active-feature-probes' { 'feature-probes/scenario' }
                        'late-feature-probes' { 'feature-probes/scenario' }
                        'active-name-reads' { 'name-reads/scenario' }
                        'late-name-reads' { 'name-reads/scenario' }
                        'active-toasts' { 'toast-calls/scenario' }
                        'late-toasts' { 'toast-calls/scenario' }
                        'completed-callbacks' { 'completions/scenario' }
                        default { throw "Unknown virtual desktop callback metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production MainWindowLifetime callback admission wired into MainWindow.OnVirtualDesktopChanged;one active and one disposed callback per cycle with fake state,feature,name and toast delegates;same current fixture against a separately archived hashed mechanical old-order FancyWM.dll;counts only,no MainWindow/App/HWND,native virtual desktop or COM access,real toast,input,focus,timing,allocation,retained memory,CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'VirtualDesktopRemovalLifetime') {
                    $counterRow.scenario = 'Actual MainWindowLifetime callback admission used by MainWindow.OnVirtualDesktopRemoved;100 active and100 disposed lifetimes'
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'cycles' { 'cycles/scenario' }
                        'active-state-reads' { 'state-reads/scenario' }
                        'late-state-reads' { 'state-reads/scenario' }
                        'active-clears' { 'state-clears/scenario' }
                        'late-clears' { 'state-clears/scenario' }
                        'completed-callbacks' { 'completions/scenario' }
                        default { throw "Unknown virtual desktop removal metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production MainWindowLifetime callback admission wired into MainWindow.OnVirtualDesktopRemoved;one active and one disposed callback per cycle with strict fake desktop identities and fake previous-state getter/clear delegates;same current fixture against a separately archived hashed mechanical old-order FancyWM.dll;exact reference comparison and zero desktop member reads are asserted;counts only,no MainWindow/App/HWND,native virtual desktop or COM access,input,focus,dispatch,waiting,timing,allocation,retained memory,CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'TilingNotificationLifetime') {
                    $counterRow.scenario = 'Actual MainWindow.HandleTilingNotificationAsync admission;100 active and100 disposed lifetimes'
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'cycles' { 'cycles/scenario' }
                        'active-callbacks' { 'callbacks/scenario' }
                        'late-callbacks' { 'callbacks/scenario' }
                        'completed-invocations' { 'completions/scenario' }
                        'owned-releases' { 'cleanup-calls/scenario' }
                        default { throw "Unknown tiling notification metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production MainWindow.HandleTilingNotificationAsync and MainWindowLifetime callback admission;one active and one disposed invocation per cycle with injected callback bodies;integration into OnTilingFailed and OnAlgorithmicLayoutChanged is code-confirmed;same current fixture against separately archived hashed mechanical unconditional-seam FancyWM.dll;completed-invocations includes rejected helper invocations and owned-releases counts only closing-owner cleanup;counts only,no MainWindow/App/HWND,actual notification formatting,sound,toast,native window or COM access,input,focus,timing,allocation,retained memory,CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'DirectHotkeyLifetime') {
                    $counterRow.scenario = 'Actual MainWindow.HandleDirectHotkeyPressedAsync admission;100 active and100 disposed lifetimes'
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'cycles' { 'cycles/scenario' }
                        'active-executions' { 'executions/scenario' }
                        'late-executions' { 'executions/scenario' }
                        'active-presentations' { 'presentations/scenario' }
                        'late-presentations' { 'presentations/scenario' }
                        'completed-invocations' { 'completions/scenario' }
                        'owned-releases' { 'cleanup-calls/scenario' }
                        default { throw "Unknown direct hotkey metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production MainWindow.HandleDirectHotkeyPressedAsync and MainWindowLifetime callback admission;one active and one disposed invocation per cycle with injected by-ref action executor and TilingFailedException presenter;integration into OnDirectHotkeyPressed is code-confirmed;same current fixture against separately archived hashed mechanical unconditional-seam FancyWM.dll;completed-invocations includes rejected helper invocations and owned-releases counts only closing-owner cleanup;exact action,friendly name,exception identity,presenter await and Dispatcher affinity are separate regressions;counts only,no MainWindow/App/HWND,actual command,layout,focus,virtual desktop,toast,native input or COM access,timing,allocation,retained memory,CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'MicaCallbackLifetime') {
                    $counterRow.scenario = 'Actual MainWindow.HandleMicaPrimaryColorChanged admission;100 lifetimes with one active,one queued-before-close and one captured-after-close callback'
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'cycles' { 'cycles/scenario' }
                        'active-posts' { 'posts/scenario' }
                        'late-posts' { 'posts/scenario' }
                        'queued-before-close-posts' { 'posts/scenario' }
                        'active-applies' { 'applies/scenario' }
                        'late-applies' { 'applies/scenario' }
                        'queued-after-close-applies' { 'applies/scenario' }
                        'owned-releases' { 'cleanup-calls/scenario' }
                        'completed-callbacks' { 'completions/scenario' }
                        default { throw "Unknown Mica callback metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production MainWindow.HandleMicaPrimaryColorChanged and MainWindowLifetime callback admission;injected post/apply delegates and a caller-drained queue;integration into OnMicaProviderPrimaryColorChanged is code-confirmed;same current fixture against separately archived hashed mechanical unconditional-post-seam FancyWM.dll;only late-posts changes,the existing inner guard rejects queued-after-close applies in both variants;completed-callbacks includes rejected returned helper invocations and owned-releases counts closing-owner cleanup;counts only,no MainWindow/App/HWND,WPF Dispatcher,FauxMicaProvider,actual color update,native access,timing,allocation,retained memory,CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                if ($Scenario -eq 'LoadedUpdateCheckLifetime') {
                    $counterRow.scenario = 'Actual LoadedUpdateCheck Start and owned callback completion;100 lifetimes with one normal start,three overlapping burst starts and one start after close'
                    $counterRow.window_count = 0
                    $counterRow.unit = switch ($counterRow.metric) {
                        'cycles' { 'cycles/scenario' }
                        'active-posts' { 'posts/scenario' }
                        'burst-posts' { 'posts/scenario' }
                        'late-posts' { 'posts/scenario' }
                        'active-runs' { 'callback-starts/scenario' }
                        'burst-runs' { 'callback-starts/scenario' }
                        'late-runs' { 'callback-starts/scenario' }
                        'completed-starts' { 'returned-start-calls/scenario' }
                        'owned-releases' { 'cleanup-calls/scenario' }
                        'pending-callbacks' { 'queued-callbacks/after-fixture-drain' }
                        default { throw "Unknown loaded update-check metric: $($counterRow.metric)" }
                    }
                    $counterRow.iterations = 100
                    $counterRow.instrument = 'Actual production LoadedUpdateCheck and MainWindowLifetime;injected task-returning post,caller-drained queue and controlled pending callback;two burst Start calls occur before drain and a third while callbacks are pending;every posted operation and owned Completion is awaited;same current fixture against separately archived hashed mechanical unconditional-start-seam FancyWM.dll;OnLoaded/update-loop/shutdown integration is code-confirmed;completed-starts counts returned Start calls including coalesced and rejected calls,owned-releases counts closing-owner cleanup;counts only,no MainWindow/App/HWND,WPF Dispatcher,ReleaseChecker,HTTP,real three-hour delay,update toast,native access,timing,allocation,retained memory,CPU/GPU or energy measurement'
                    $counterRow.perf_id = 'PERF-016'
                }
                $counterRow
            }
        }
    }
    if ($Scenario -in @('WindowDraggerLifetime','ModifierDragLifetime')) {
        foreach ($row in $rows) {
            $row.scenario = if ($Scenario -eq 'WindowDraggerLifetime') {
                'WindowDragger Begin/End lifetime with one fake production IWindow/IWorkspace;100 reentrant-stop and100 normal cycles'
            } else {
                'ModifierWindowMover hook-to-drag lifetime with one fake production IWindow/IWorkspace;100 reentrant-Dispose and100 normal cycles'
            }
            $row.window_count = 1
            $row.unit = switch ($row.metric) {
                'cycles' { 'cycles/scenario' }
                'late-subscriptions' { 'subscriptions/after-stop' }
                'late-writes' { 'position-writes/after-stop' }
                'pending-completions' { 'completions/pending-inside-dispose' }
                'normal-writes' { 'position-writes/scenario' }
                default { 'notifications/scenario' }
            }
            $row.iterations = 100
            $row.instrument = if ($Scenario -eq 'WindowDraggerLifetime') {
                'Actual production WindowDragger Begin/End;fake window/workspace/focus/dispatcher adapters;stopping inside focus probes late cursor subscription/write,normal cycle checks exact rectangle and balanced start/end;same current fixture against separately archived hashed R1-PRE/candidate FancyWM.dll;counts only,no real mouse hook,WPF Dispatcher,focus,desktop,latency,allocation,CPU/GPU or retained-memory measurement'
            } else {
                'Actual production ModifierWindowMover callback/Dispose and WindowDragger dependency;fake mouse-hook/window/workspace/focus/dispatcher adapters;Dispose inside focus probes pending ownership and late cursor subscription/write,normal cycle checks exact rectangle and balanced start/end;same current fixture against separately archived hashed R1-PRE/candidate FancyWM.dll;counts only,no native hook,WPF Dispatcher,focus,desktop,latency,allocation,CPU/GPU or retained-memory measurement'
            }
            $row.perf_id = 'PERF-016'
        }
    }
    if ($Scenario -eq 'AppAsyncShutdown') {
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'early-window-closes' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'early-shutdowns' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'dispose-attempts' { 100 }
                'close-attempts' { 100 }
                'mouse-cleanups' { 100 }
                'shutdowns' { 100 }
            }
            if ([int]$row.value -ne $expected) { throw "Async application shutdown result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'DependentShutdown') {
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'premature-cleanups' { if ($row.variant -eq 'baseline') { 300 } else { 0 } }
                'pending-completions' { if ($row.variant -eq 'baseline') { 0 } else { 100 } }
                'cleanup-attempts' { 300 }
                'cancelled-jobs' { 100 }
                'worker-exits' { 100 }
                'duplicate-cleanups' { 0 }
                'remaining-owners' { 0 }
            }
            if ([int]$row.value -ne $expected) { throw "Dependent shutdown result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
        foreach ($process in ($rows | Group-Object variant, run)) {
            $cycles = [int]($process.Group | Where-Object metric -eq 'cycles').value
            $attempted = [int]($process.Group | Where-Object metric -eq 'cleanup-attempts').value
            $remaining = [int]($process.Group | Where-Object metric -eq 'remaining-owners').value
            if ($attempted + $remaining -ne 3 * $cycles) { throw "Dependent shutdown resource ownership differs: $($process.Name)" }
        }
    }
    if ($Scenario -eq 'AnimationShutdown') {
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'accepted-jobs' { 200 }
                'waits' { if ($row.variant -eq 'baseline') { 200 } else { 100 } }
                'updates' { if ($row.variant -eq 'baseline') { 200 } else { 0 } }
                'queued-updates' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'cancellations' { 200 }
                'successful-jobs' { 0 }
                'enable-calls' { 100 }
                'disable-calls' { 100 }
                'remaining-references' { 0 }
                'worker-exits' { 100 }
                'late-rejections' { 100 }
            }
            if ([int]$row.value -ne $expected) { throw "Animation shutdown result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
        foreach ($process in ($rows | Group-Object variant, run)) {
            $accepted = [int]($process.Group | Where-Object metric -eq 'accepted-jobs').value
            $cancelled = [int]($process.Group | Where-Object metric -eq 'cancellations').value
            $successful = [int]($process.Group | Where-Object metric -eq 'successful-jobs').value
            $enabled = [int]($process.Group | Where-Object metric -eq 'enable-calls').value
            $disabled = [int]($process.Group | Where-Object metric -eq 'disable-calls').value
            $remaining = [int]($process.Group | Where-Object metric -eq 'remaining-references').value
            if ($accepted -ne $cancelled + $successful) { throw "Animation shutdown terminal jobs differ: $($process.Name)" }
            if ($enabled - $disabled -ne $remaining) { throw "Animation shutdown boost ownership differs: $($process.Name)" }
        }
    }
    if ($Scenario -eq 'ClockBoost') {
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'waits' { 200 }
                'updates' { 200 }
                'cancellations' { 200 }
                'enable-calls' { if ($row.variant -eq 'baseline') { 200 } else { 100 } }
                'disable-calls' { 100 }
                'remaining-references' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'zero-progress-updates' { 200 }
            }
            if ([int]$row.value -ne $expected) { throw "Animation clock boost result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
        foreach ($process in ($rows | Group-Object variant, run)) {
            $enabled = [int]($process.Group | Where-Object metric -eq 'enable-calls').value
            $disabled = [int]($process.Group | Where-Object metric -eq 'disable-calls').value
            $remaining = [int]($process.Group | Where-Object metric -eq 'remaining-references').value
            if ($enabled - $disabled -ne $remaining) { throw "Animation clock boost ownership differs: $($process.Name)" }
        }
    }
    if ($Scenario -eq 'FocusBounded') {
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'requests' { 16 }
                'returned-before-release' { if ($row.variant -eq 'baseline') { 0 } else { 16 } }
                'started-workers' { if ($row.variant -eq 'baseline') { 16 } else { 1 } }
                'background-workers' { if ($row.variant -eq 'baseline') { 0 } else { 1 } }
                'obsolete-focus' { 0 }
                'worker-exits' { if ($row.variant -eq 'baseline') { 16 } else { 1 } }
                'newest-successes' { 1 }
                'recovery-successes' { 1 }
                default { throw "Unexpected bounded focus metric: $($row.metric)" }
            }
            if ([int]$row.value -ne $expected) { throw "Bounded focus result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
        foreach ($process in ($rows | Group-Object variant, run)) {
            $started = [int]($process.Group | Where-Object metric -eq 'started-workers').value
            $exited = [int]($process.Group | Where-Object metric -eq 'worker-exits').value
            if ($started -ne $exited) { throw "Bounded focus worker ownership differs: $($process.Name)" }
        }
    }
    if ($Scenario -eq 'FocusSupersession') {
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'old-focus' { if ($row.variant -eq 'baseline') { 200 } else { 100 } }
                'new-focus' { 100 }
                'alt' { 0 }
                'attachments' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'detachments' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'completed-workers' { 100 }
                'superseded-results' { if ($row.variant -eq 'baseline') { 0 } else { 100 } }
            }
            if ([int]$row.value -ne $expected) { throw "Focus supersession result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
        foreach ($process in ($rows | Group-Object variant, run)) {
            $oldFocus = [int]($process.Group | Where-Object metric -eq 'old-focus').value
            $cycles = [int]($process.Group | Where-Object metric -eq 'cycles').value
            $expectedStale = if ($process.Group[0].variant -eq 'baseline') { 100 } else { 0 }
            if ($oldFocus - $cycles -ne $expectedStale) { throw "Obsolete fallback focus calls differ: $($process.Name)" }
        }
    }
    if ($Scenario -eq 'LayoutCadence') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('cycles','pending-delays') } | Group-Object metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Layout cadence workload differs: $($invariant.Name)" }
        }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'trailing-layouts' { if ($row.variant -eq 'baseline') { 0 } else { 100 } }
                'cadence-delays' { if ($row.variant -eq 'baseline') { 0 } else { 100 } }
                'pending-delays' { 0 }
            }
            if ([int]$row.value -ne $expected) { throw "Layout cadence result differs: $($row.variant)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'AppShutdown') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('cycles','dispose-attempts','original-errors') } | Group-Object metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "App shutdown workload differs: $($invariant.Name)" }
        }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'dispose-attempts' { 300 }
                'close-attempts' { if ($row.variant -eq 'baseline') { 200 } else { 300 } }
                'mouse-cleanups' { if ($row.variant -eq 'baseline') { 0 } else { 100 } }
                'original-errors' { 100 }
            }
            if ([int]$row.value -ne $expected) { throw "App shutdown result differs: $($row.variant)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'ConstructionLifetime') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('cycles','original-errors','duplicate-cleanups') } | Group-Object metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Constructor ownership workload differs: $($invariant.Name)" }
        }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'cleanup-attempts' { if ($row.variant -eq 'baseline') { 0 } else { 300 } }
                'remaining-owners' { if ($row.variant -eq 'baseline') { 300 } else { 0 } }
                'original-errors' { 100 }
                'duplicate-cleanups' { 0 }
            }
            if ([int]$row.value -ne $expected) { throw "Constructor ownership result differs: $($row.variant)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'MainWindowLifetime') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('cycles','original-errors','duplicate-cleanups') } | Group-Object metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "MainWindow cleanup workload differs: $($invariant.Name)" }
        }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'cleanup-attempts' { if ($row.variant -eq 'baseline') { 200 } else { 400 } }
                'remaining-owners' { if ($row.variant -eq 'baseline') { 200 } else { 0 } }
                'original-errors' { 100 }
                'duplicate-cleanups' { 0 }
            }
            if ([int]$row.value -ne $expected) { throw "MainWindow cleanup result differs: $($row.variant)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'BindingListener') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -notin @('late-notifications','retained-owners') } | Group-Object metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Binding listener workload differs: $($invariant.Name)" }
        }
        foreach ($row in ($rows | Where-Object { $_.metric -in @('late-notifications','retained-owners') })) {
            $expected = if ($row.variant -eq 'baseline') { 100 } else { 0 }
            if ([int]$row.value -ne $expected) { throw "Binding listener ownership result differs: $($row.variant)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'KeyPressDisplay') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('created-controls','live-hook-subscribers','display-checksum','timestamp-frequency') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Key editor display/lifetime result differs: $($invariant.Name)" }
        }
    }
    if ($Scenario -eq 'HookReplacement') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -notin @('preserved-on-failure','zero-unhook-calls','maximum-fake-live-handles') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Hook retry/owned cleanup workload differs: $($invariant.Name)" }
        }
    }
    if ($Scenario -eq 'DisplayRemovalGc') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -ne 'collection-requests' } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Display removal ownership/event workload differs: $($invariant.Name)" }
        }
    }
    if ($Scenario -eq 'CursorInput') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -eq 'input-events' -or $_.scenario -like '*overlay-cursor-visible-*' } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Cursor visible behavior/input workload differs: $($invariant.Name)" }
        }
    }
    if ($Scenario -eq 'SettingsStrings') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('timestamp-frequency','content-digest','lost-focus-events') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Strings editor semantics differ: $($invariant.Name)" }
        }
    }
    if ($Scenario -in @('OverlayMaterialization','ActionBarMaterialization','SvgMaterialization')) {
        foreach ($invariant in ($rows | Where-Object { $_.metric -notlike '*-bytes' -and $_.metric -notlike '*-ticks' } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Overlay materialization differs: $($invariant.Name)" }
        }
    }
    if ($Scenario -eq 'GenericPreview') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('previews','minimum-reads','timestamp-frequency','geometry-digest') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Generic preview results differ: $($invariant.Name)" }
        }
    }
    if ($Scenario -in @('AnimationFrame','HotkeyCallback')) {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('updates','frames','timestamp-frequency','input-events','suppressed-events','emitted-events','event-digest') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Frame/hotkey semantics differ: $($invariant.Name)" }
        }
    }
    if ($Scenario -eq 'TreeStateLookup') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('lookups', 'matched-lookups', 'timestamp-frequency') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Tree state lookup semantics differ: $($invariant.Name)" }
        }
    }
    if ($Scenario -eq 'WindowStateLookup') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Window state lookup semantics differ: $($invariant.Name)" }
        }
        foreach ($row in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks', 'timestamp-frequency') })) {
            $expected = switch ($row.metric) {
                'lookups' { 100000 }
                'matched-lookups' { 100000 }
                'resident-hits' { 50000 }
                'probes' { 100000 * [int]$row.window_count }
                'desktop-hash-reads' { if ($row.scenario -like '*window-state-vdm-*') { 50000 } else { 0 } }
                default { throw "Unknown window-state lookup invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "Window state lookup workload differs: $($row.variant)/$($row.scenario)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'DisplayManageability') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Display manageability semantics differ: $($invariant.Name)" }
        }
        foreach ($row in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks', 'timestamp-frequency') })) {
            $expected = switch ($row.metric) {
                'lookups' { 100000 }
                'manageable-results' { 50000 }
                'foreign-results' { 50000 }
                'position-reads' { 100000 }
                'snapshot-reads' { 100000 }
                'equality-reads' { 100000 * [int]$row.window_count }
                'bounds-reads' { 100000 * [int]$row.window_count }
                'resize-reads' { 50000 }
                'move-reads' { 50000 }
                'pin-reads' { 50000 }
                default { throw "Unknown display manageability invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "Display manageability workload differs: $($row.variant)/$($row.scenario)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'DiscoveryWindowSnapshot') {
        if ($rows.Count -ne 480) {
            throw "DiscoveryWindowSnapshot comparison requires exactly480 rows, got $($rows.Count)"
        }
        $discoveryUnits = @{
            'allocated-bytes' = 'bytes/100000 passes'
            'elapsed-ticks' = 'ticks/100000 passes'
            'timestamp-frequency' = 'ticks/second'
            'passes' = 'passes/scenario'
            'changed-passes' = 'changed-passes/scenario'
            'state-reads' = 'state-reads/scenario'
            'position-reads' = 'position-reads/scenario'
            'resize-reads' = 'resize-reads/scenario'
            'move-reads' = 'move-reads/scenario'
            'pin-reads' = 'desktop-pin-reads/scenario'
            'desktop-snapshot-reads' = 'desktop-snapshot-reads/scenario'
            'display-snapshot-reads' = 'display-snapshot-reads/scenario'
        }
        $discoveryRowKeys = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::Ordinal)
        foreach ($row in $rows) {
            $scenarioMatch = [regex]::Match(
                $row.scenario,
                '^Actual TilingService\.DiscoverWindows owned pass snapshot; discovery-window-snapshot-(0|1|10|50) tracked restored windows rejected by CanResize;1000 discarded warmups then100000 complete unchanged passes$')
            if (!$scenarioMatch.Success) {
                throw "Unknown DiscoveryWindowSnapshot scenario: $($row.scenario)"
            }
            $windowCount = [int]$scenarioMatch.Groups[1].Value
            $expectedSnapshot = if ($row.variant -ceq 'baseline') { $BaselineSnapshotId } else { $SnapshotId }
            $expectedBinaryHash = if ($row.variant -ceq 'baseline') { $binaryRecord.Hash } else { $binaryHash }
            if ($row.metric -cnotin $discoveryUnits.Keys -or $row.variant -cnotin @('baseline','candidate') -or
                [string]$row.run -cnotmatch '^[1-5]$' -or [string]$row.pair -cne [string]$row.run -or
                [string]$row.window_count -cne [string]$windowCount -or [string]$row.iterations -cne '100000' -or
                $row.unit -cne $discoveryUnits[$row.metric] -or $row.perf_id -cne 'PERF-002' -or
                $row.experiment_id -cne $ExperimentId -or $row.snapshot_id -cne $expectedSnapshot -or
                $row.configuration -cne "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0" -or
                $row.binary_sha256 -cne $expectedBinaryHash -or $row.benchmark_sha256 -cne $benchmarkHash -or
                [string]$row.value -cnotmatch '^(0|[1-9][0-9]*)$') {
                throw "DiscoveryWindowSnapshot row dimensions or provenance differ: $($row.variant)/$($row.run)/$windowCount/$($row.metric)"
            }
            $key = "$($row.variant)/$($row.run)/$windowCount/$($row.metric)"
            if (!$discoveryRowKeys.TryAdd($key, $row)) {
                throw "Duplicate DiscoveryWindowSnapshot row: $key"
            }
            $value = [long]$row.value
            $expected = switch -CaseSensitive ($row.metric) {
                'passes' { 100000L }
                'changed-passes' { 0L }
                { $_ -cin @('state-reads','position-reads','resize-reads') } { 100000L * $windowCount }
                { $_ -cin @('move-reads','pin-reads','desktop-snapshot-reads','display-snapshot-reads') } { 0L }
                'allocated-bytes' {
                    $candidateBytesPerPass = switch ($windowCount) { 0 { 0L } 1 { 32L } 10 { 104L } 50 { 424L } }
                    100000L * ($candidateBytesPerPass + $(if ($row.variant -ceq 'baseline') { 32L } else { 0L }))
                }
                { $_ -cin @('elapsed-ticks','timestamp-frequency') } {
                    if ($value -le 0) { throw "Non-positive DiscoveryWindowSnapshot timing counter: $key" }
                    $null
                }
                default { throw "Unknown DiscoveryWindowSnapshot invariant: $($row.metric)" }
            }
            if ($null -ne $expected -and $value -ne $expected) {
                throw "DiscoveryWindowSnapshot workload differs: $key, expected $expected, got $value"
            }
        }
        foreach ($run in 1..5) {
            foreach ($windowCount in @(0,1,10,50)) {
                foreach ($metric in $discoveryUnits.Keys) {
                    foreach ($variant in @('baseline','candidate')) {
                        if (!$discoveryRowKeys.ContainsKey("$variant/$run/$windowCount/$metric")) {
                            throw "Missing DiscoveryWindowSnapshot row: $variant/$run/$windowCount/$metric"
                        }
                    }
                    if ($metric -notin @('allocated-bytes','elapsed-ticks')) {
                        $firstValue = [string]$discoveryRowKeys["baseline/1/$windowCount/$metric"].value
                        if ([string]$discoveryRowKeys["baseline/$run/$windowCount/$metric"].value -cne $firstValue -or
                            [string]$discoveryRowKeys["candidate/$run/$windowCount/$metric"].value -cne $firstValue) {
                            throw "DiscoveryWindowSnapshot semantic counter differs between variants/repetitions: $run/$windowCount/$metric"
                        }
                    }
                }
                $baselineAllocation = [long]$discoveryRowKeys["baseline/$run/$windowCount/allocated-bytes"].value
                $candidateAllocation = [long]$discoveryRowKeys["candidate/$run/$windowCount/allocated-bytes"].value
                if ($baselineAllocation - $candidateAllocation -ne 3200000L) {
                    throw "DiscoveryWindowSnapshot allocation saving differs: $run/$windowCount ($baselineAllocation -> $candidateAllocation)"
                }
            }
        }
    }
    if ($Scenario -eq 'RefreshWindowSnapshot') {
        if ($rows.Count -ne 480) {
            throw "RefreshWindowSnapshot comparison requires exactly480 rows, got $($rows.Count)"
        }
        $refreshWindowSnapshotSchema = 'experiment_id|snapshot_id|scenario|configuration|window_count|metric|unit|run|value|iterations|instrument|perf_id|variant|pair|binary_sha256|benchmark_sha256'
        $refreshWindowSnapshotRowKeys = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::Ordinal)
        foreach ($row in $rows) {
            if (($row.PSObject.Properties.Name -join '|') -cne $refreshWindowSnapshotSchema) {
                throw 'RefreshWindowSnapshot row schema differs'
            }
            $scenarioMatch = [regex]::Match(
                $row.scenario,
                '^Actual TilingService\.Refresh owned pass snapshot; refresh-window-snapshot-(0|1|10|50) tracked restored windows rejected by CanResize;1000 discarded warmups then100000 complete unchanged passes$')
            if (!$scenarioMatch.Success) {
                throw "Unknown RefreshWindowSnapshot scenario: $($row.scenario)"
            }
            $windowCount = [int]$scenarioMatch.Groups[1].Value
            $expectedSnapshot = if ($row.variant -ceq 'baseline') { $BaselineSnapshotId } else { $SnapshotId }
            $expectedBinaryHash = if ($row.variant -ceq 'baseline') { $binaryRecord.Hash } else { $binaryHash }
            if ($row.metric -cnotin $refreshWindowSnapshotUnits.Keys -or $row.variant -cnotin @('baseline','candidate') -or
                [string]$row.run -cnotmatch '^[1-5]$' -or [string]$row.pair -cne [string]$row.run -or
                [string]$row.window_count -cne [string]$windowCount -or [string]$row.iterations -cne '100000' -or
                $row.unit -cne $refreshWindowSnapshotUnits[$row.metric] -or $row.perf_id -cne 'PERF-002' -or
                $row.experiment_id -cne $ExperimentId -or $row.snapshot_id -cne $expectedSnapshot -or
                $row.configuration -cne "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0" -or
                $row.instrument -cne $refreshWindowSnapshotInstrument -or
                $row.binary_sha256 -cne $expectedBinaryHash -or $row.benchmark_sha256 -cne $benchmarkHash -or
                [string]$row.value -cnotmatch '^(0|[1-9][0-9]*)$') {
                throw "RefreshWindowSnapshot row dimensions or provenance differ: $($row.variant)/$($row.run)/$windowCount/$($row.metric)"
            }
            $key = "$($row.variant)/$($row.run)/$windowCount/$($row.metric)"
            if (!$refreshWindowSnapshotRowKeys.TryAdd($key, $row)) {
                throw "Duplicate RefreshWindowSnapshot row: $key"
            }
            $value = [long]::Parse([string]$row.value, [Globalization.CultureInfo]::InvariantCulture)
            $expected = switch -CaseSensitive ($row.metric) {
                'passes' { 100000L }
                'tracked-windows' { [long]$windowCount }
                { $_ -cin @('state-reads','position-reads','resize-reads') } { 100000L * $windowCount }
                { $_ -cin @('move-reads','pin-reads','desktop-snapshot-reads','display-snapshot-reads') } { 0L }
                'allocated-bytes' {
                    $candidateBytesPerPass = switch ($windowCount) { 0 { 128L } 1 { 160L } 10 { 232L } 50 { 552L } }
                    100000L * ($candidateBytesPerPass + $(if ($row.variant -ceq 'baseline') { 32L } else { 0L }))
                }
                { $_ -cin @('elapsed-ticks','timestamp-frequency') } {
                    if ($value -le 0) { throw "Non-positive RefreshWindowSnapshot timing counter: $key" }
                    $null
                }
                default { throw "Unknown RefreshWindowSnapshot invariant: $($row.metric)" }
            }
            if ($null -ne $expected -and $value -ne $expected) {
                throw "RefreshWindowSnapshot workload differs: $key, expected $expected, got $value"
            }
        }
        $refreshWindowSnapshotTiming = foreach ($run in 1..5) {
            foreach ($windowCount in @(0,1,10,50)) {
                foreach ($metric in $refreshWindowSnapshotUnits.Keys) {
                    foreach ($variant in @('baseline','candidate')) {
                        if (!$refreshWindowSnapshotRowKeys.ContainsKey("$variant/$run/$windowCount/$metric")) {
                            throw "Missing RefreshWindowSnapshot row: $variant/$run/$windowCount/$metric"
                        }
                    }
                    if ($metric -cnotin @('allocated-bytes','elapsed-ticks')) {
                        $firstValue = [string]$refreshWindowSnapshotRowKeys["baseline/1/$windowCount/$metric"].value
                        if ([string]$refreshWindowSnapshotRowKeys["baseline/$run/$windowCount/$metric"].value -cne $firstValue -or
                            [string]$refreshWindowSnapshotRowKeys["candidate/$run/$windowCount/$metric"].value -cne $firstValue) {
                            throw "RefreshWindowSnapshot semantic counter differs between variants/repetitions: $run/$windowCount/$metric"
                        }
                    }
                }
                $baselineAllocation = [long]$refreshWindowSnapshotRowKeys["baseline/$run/$windowCount/allocated-bytes"].value
                $candidateAllocation = [long]$refreshWindowSnapshotRowKeys["candidate/$run/$windowCount/allocated-bytes"].value
                if ($baselineAllocation - $candidateAllocation -ne 3200000L) {
                    throw "RefreshWindowSnapshot allocation saving differs: $run/$windowCount ($baselineAllocation -> $candidateAllocation)"
                }
                $baselineTicks = [long]$refreshWindowSnapshotRowKeys["baseline/$run/$windowCount/elapsed-ticks"].value
                $candidateTicks = [long]$refreshWindowSnapshotRowKeys["candidate/$run/$windowCount/elapsed-ticks"].value
                $baselineFrequency = [long]$refreshWindowSnapshotRowKeys["baseline/$run/$windowCount/timestamp-frequency"].value
                $candidateFrequency = [long]$refreshWindowSnapshotRowKeys["candidate/$run/$windowCount/timestamp-frequency"].value
                $baselineNanoseconds = $baselineTicks * 1000000000.0 / $baselineFrequency / 100000.0
                $candidateNanoseconds = $candidateTicks * 1000000000.0 / $candidateFrequency / 100000.0
                if (![double]::IsFinite($baselineNanoseconds) -or ![double]::IsFinite($candidateNanoseconds) -or
                    $baselineNanoseconds -le 0 -or $candidateNanoseconds -le 0) {
                    throw "Invalid RefreshWindowSnapshot derived duration: $run/$windowCount"
                }
                [pscustomobject]@{
                    Run = $run
                    WindowCount = $windowCount
                    BaselineNanosecondsPerPass = $baselineNanoseconds
                    CandidateNanosecondsPerPass = $candidateNanoseconds
                    DeltaPercent = ($candidateNanoseconds - $baselineNanoseconds) / $baselineNanoseconds * 100.0
                    TimingImproved = $candidateNanoseconds -lt $baselineNanoseconds
                }
            }
        }
        foreach ($record in $candidateBuildRecords) {
            if ((Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $record.Path)).Hash -cne $record.SHA256) {
                throw "RefreshWindowSnapshot candidate frozen source changed during measurement: $($record.Path)"
            }
        }
        foreach ($record in $refreshWindowSnapshotBaselineBuildRecords) {
            if ((Get-FileHash -LiteralPath (Join-Path "$baselineRoot/source" $record.Path)).Hash -cne $record.SHA256) {
                throw "RefreshWindowSnapshot baseline frozen source changed during measurement: $($record.Path)"
            }
        }
        $refreshWindowSnapshotTiming | Export-Csv -NoTypeInformation "$experimentRoot/refresh-window-snapshot-timing.csv"
    }
    if ($Scenario -eq 'TransitionCompletion') {
        if ($rows.Count -ne 1080) {
            throw "TransitionCompletion comparison requires exactly1080 rows, got $($rows.Count)"
        }
        $transitionCompletionRowKeys = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::Ordinal)
        foreach ($row in $rows) {
            $scenarioMatch = [regex]::Match(
                $row.scenario,
                '^Actual Tasks\.WhenAllIgnoreCancelled completion aggregation; transition-completion-(success|cancelled|mixed)-(0|1|4|10|25|50);1000 discarded warmups then10000 awaited completions$')
            if (!$scenarioMatch.Success) {
                throw "Unknown TransitionCompletion scenario: $($row.scenario)"
            }
            $mode = $scenarioMatch.Groups[1].Value
            $taskCount = [int]$scenarioMatch.Groups[2].Value
            $expectedSnapshot = if ($row.variant -ceq 'baseline') { $BaselineSnapshotId } else { $SnapshotId }
            $expectedBinaryHash = if ($row.variant -ceq 'baseline') { $binaryRecord.Hash } else { $binaryHash }
            if ($row.metric -cnotin $transitionCompletionUnits.Keys -or $row.variant -cnotin @('baseline','candidate') -or
                [string]$row.run -cnotmatch '^[1-5]$' -or [string]$row.pair -cne [string]$row.run -or
                [string]$row.window_count -cne [string]$taskCount -or [string]$row.iterations -cne '10000' -or
                $row.unit -cne $transitionCompletionUnits[$row.metric] -or $row.perf_id -cne 'PERF-010' -or
                $row.experiment_id -cne $ExperimentId -or $row.snapshot_id -cne $expectedSnapshot -or
                $row.configuration -cne "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0" -or
                $row.instrument -cne $transitionCompletionInstrument -or
                $row.binary_sha256 -cne $expectedBinaryHash -or $row.benchmark_sha256 -cne $benchmarkHash -or
                [string]$row.value -cnotmatch '^(0|[1-9][0-9]*)$') {
                throw "TransitionCompletion row dimensions or provenance differ: $($row.variant)/$($row.run)/$mode/$taskCount/$($row.metric)"
            }
            $key = "$($row.variant)/$($row.run)/$mode/$taskCount/$($row.metric)"
            if (!$transitionCompletionRowKeys.TryAdd($key, $row)) {
                throw "Duplicate TransitionCompletion row: $key"
            }
            $value = [long]$row.value
            $expected = switch -CaseSensitive ($row.metric) {
                'completions' { 10000L }
                'input-tasks' { 10000L * $taskCount }
                'cancelled-inputs' {
                    $cancelledCount = switch -CaseSensitive ($mode) {
                        'success' { 0L }
                        'cancelled' { [long]$taskCount }
                        'mixed' { [long][Math]::Ceiling($taskCount / 2.0) }
                    }
                    10000L * $cancelledCount
                }
                'allocated-bytes' { $null }
                { $_ -cin @('elapsed-ticks','timestamp-frequency') } {
                    if ($value -le 0) { throw "Non-positive TransitionCompletion timing counter: $key" }
                    $null
                }
                default { throw "Unknown TransitionCompletion invariant: $($row.metric)" }
            }
            if ($null -ne $expected -and $value -ne $expected) {
                throw "TransitionCompletion workload differs: $key, expected $expected, got $value"
            }
        }
        $expectedFrequency = [string]$transitionCompletionRowKeys['baseline/1/success/0/timestamp-frequency'].value
        foreach ($run in 1..5) {
            foreach ($mode in $transitionCompletionModes) {
                foreach ($taskCount in $transitionCompletionCounts) {
                    foreach ($metric in $transitionCompletionUnits.Keys) {
                        foreach ($variant in @('baseline','candidate')) {
                            $key = "$variant/$run/$mode/$taskCount/$metric"
                            if (!$transitionCompletionRowKeys.ContainsKey($key)) {
                                throw "Missing TransitionCompletion row: $key"
                            }
                            if ($metric -cne 'elapsed-ticks' -and
                                [string]$transitionCompletionRowKeys[$key].value -cne [string]$transitionCompletionRowKeys["$variant/1/$mode/$taskCount/$metric"].value) {
                                throw "TransitionCompletion non-timing counter differs between repetitions: $key"
                            }
                            if ($metric -ceq 'timestamp-frequency' -and [string]$transitionCompletionRowKeys[$key].value -cne $expectedFrequency) {
                                throw "TransitionCompletion timestamp frequency differs between cases/variants/repetitions: $key"
                            }
                        }
                        if ($metric -cnotin @('allocated-bytes','elapsed-ticks') -and
                            [string]$transitionCompletionRowKeys["baseline/$run/$mode/$taskCount/$metric"].value -cne
                            [string]$transitionCompletionRowKeys["candidate/$run/$mode/$taskCount/$metric"].value) {
                            throw "TransitionCompletion semantic counter differs between variants: $run/$mode/$taskCount/$metric"
                        }
                    }
                    $baselineAllocation = [long]$transitionCompletionRowKeys["baseline/$run/$mode/$taskCount/allocated-bytes"].value
                    $candidateAllocation = [long]$transitionCompletionRowKeys["candidate/$run/$mode/$taskCount/allocated-bytes"].value
                    if ($baselineAllocation - $candidateAllocation -lt 160000L) {
                        throw "TransitionCompletion allocation saving below16 bytes/completion: $run/$mode/$taskCount ($baselineAllocation -> $candidateAllocation)"
                    }
                }
            }
        }
    }
    if ($Scenario -eq 'RuntimeStateCount') {
        if ($rows.Count -ne 600) { throw "RuntimeStateCount comparison requires exactly600 rows, got $($rows.Count)" }
        $runtimeStateCountRowKeys = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::Ordinal)
        foreach ($row in $rows) {
            $scenarioMatch = [regex]::Match($row.scenario,
                '^Actual MasterSatelliteRuntimeSession\.StateCount through coordinator and registry; runtime-state-count-(own|mixed)-(0|1|4|10|50);1000 discarded common warmups then10000 queries$')
            if (!$scenarioMatch.Success) { throw "Unknown RuntimeStateCount scenario: $($row.scenario)" }
            $mode = $scenarioMatch.Groups[1].Value
            $stateEntryCount = [int]$scenarioMatch.Groups[2].Value
            $expectedSnapshot = if ($row.variant -ceq 'baseline') { $BaselineSnapshotId } else { $SnapshotId }
            $expectedBinaryHash = if ($row.variant -ceq 'baseline') { $binaryRecord.Hash } else { $binaryHash }
            if ($row.metric -cnotin $runtimeStateCountUnits.Keys -or $row.variant -cnotin @('baseline','candidate') -or
                [string]$row.run -cnotmatch '^[1-5]$' -or [string]$row.pair -cne [string]$row.run -or
                [string]$row.window_count -cne [string]$stateEntryCount -or [string]$row.iterations -cne '10000' -or
                $row.unit -cne $runtimeStateCountUnits[$row.metric] -or $row.perf_id -cne 'PERF-022' -or
                $row.experiment_id -cne $ExperimentId -or $row.snapshot_id -cne $expectedSnapshot -or
                $row.configuration -cne "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0" -or
                $row.instrument -cne $runtimeStateCountInstrument -or
                $row.binary_sha256 -cne $expectedBinaryHash -or $row.benchmark_sha256 -cne $benchmarkHash -or
                [string]$row.value -cnotmatch '^(0|[1-9][0-9]*)$') {
                throw "RuntimeStateCount row dimensions or provenance differ: $($row.variant)/$($row.run)/$mode/$stateEntryCount/$($row.metric)"
            }
            $key = "$($row.variant)/$($row.run)/$mode/$stateEntryCount/$($row.metric)"
            if (!$runtimeStateCountRowKeys.TryAdd($key, $row)) { throw "Duplicate RuntimeStateCount row: $key" }
            $value = [long]$row.value
            $expected = switch -CaseSensitive ($row.metric) {
                'queries' { 10000L }
                'count-sum' {
                    if ($mode -ceq 'own') { 10000L * $stateEntryCount }
                    else { 10000L * [long][Math]::Ceiling($stateEntryCount / 2.0) }
                }
                'equality-reads' { if ($mode -ceq 'own') { 0L } else { 10000L * $stateEntryCount } }
                'allocated-bytes' { $null }
                { $_ -cin @('elapsed-ticks','timestamp-frequency') } {
                    if ($value -le 0) { throw "Non-positive RuntimeStateCount timing counter: $key" }
                    $null
                }
                default { throw "Unknown RuntimeStateCount invariant: $($row.metric)" }
            }
            if ($null -ne $expected -and $value -ne $expected) {
                throw "RuntimeStateCount workload differs: $key, expected $expected, got $value"
            }
        }
        $expectedFrequency = [string]$runtimeStateCountRowKeys['baseline/1/own/0/timestamp-frequency'].value
        foreach ($run in 1..5) {
            foreach ($mode in $runtimeStateCountModes) {
                foreach ($stateEntryCount in $runtimeStateCountCounts) {
                    foreach ($metric in $runtimeStateCountUnits.Keys) {
                        foreach ($variant in @('baseline','candidate')) {
                            $key = "$variant/$run/$mode/$stateEntryCount/$metric"
                            if (!$runtimeStateCountRowKeys.ContainsKey($key)) { throw "Missing RuntimeStateCount row: $key" }
                            if ($metric -cne 'elapsed-ticks' -and
                                [string]$runtimeStateCountRowKeys[$key].value -cne [string]$runtimeStateCountRowKeys["$variant/1/$mode/$stateEntryCount/$metric"].value) {
                                throw "RuntimeStateCount non-timing counter differs between repetitions: $key"
                            }
                            if ($metric -ceq 'timestamp-frequency' -and [string]$runtimeStateCountRowKeys[$key].value -cne $expectedFrequency) {
                                throw "RuntimeStateCount timestamp frequency differs between cases/variants/repetitions: $key"
                            }
                        }
                    }
                    $baselineAllocation = [long]$runtimeStateCountRowKeys["baseline/$run/$mode/$stateEntryCount/allocated-bytes"].value
                    $candidateAllocation = [long]$runtimeStateCountRowKeys["candidate/$run/$mode/$stateEntryCount/allocated-bytes"].value
                    if ($baselineAllocation -le 0 -or $candidateAllocation -ne 0) {
                        throw "RuntimeStateCount requires positive baseline allocation and zero candidate allocation: $run/$mode/$stateEntryCount ($baselineAllocation -> $candidateAllocation)"
                    }
                }
            }
        }
    }
    if ($Scenario -eq 'PanelIndex') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Panel index semantics differ: $($invariant.Name)" }
        }
        foreach ($row in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks', 'timestamp-frequency') })) {
            $childCount = [int]$row.window_count
            $expected = switch ($row.metric) {
                'lookups' { 100000 }
                'last-index-sum' { 50000 * ($childCount - 1) }
                'missing-index-sum' { 50000 * $childCount }
                default { throw "Unknown panel index invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "Panel index workload differs: $($row.variant)/$($row.scenario)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'EmbedIndex') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Embed index semantics differ: $($invariant.Name)" }
        }
        foreach ($row in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks', 'timestamp-frequency') })) {
            $childCount = [long]$row.window_count
            $expected = switch ($row.metric) {
                'embeds' { 10000L }
                'slot-index-sum' { 10000L * ($childCount - 1L) }
                'parent-links' { 20000L }
                'registrations' { 10000L * $childCount }
                'ordered-window-sum' { 10000L * $childCount * ($childCount + 1L) / 2L }
                default { throw "Unknown Embed index invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "Embed index workload differs: $($row.variant)/$($row.scenario)/$($row.metric)" }
        }
    }

    if ($Scenario -eq 'SplitPanelMeasure') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "SplitPanel measurement semantics differ: $($invariant.Name)" }
        }
        foreach ($row in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks', 'timestamp-frequency') })) {
            $expected = switch ($row.metric) {
                'measures' { 100000L }
                'backing-children' { [long]$row.window_count }
                'published-width-sum' { 700000L }
                'published-height-sum' { 900000L }
                default { throw "Unknown SplitPanel measurement invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "SplitPanel measurement workload differs: $($row.variant)/$($row.scenario)/$($row.metric)" }
        }
    }

    if ($Scenario -eq 'StackPanelMeasure') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "StackPanel measurement semantics differ: $($invariant.Name)" }
        }
        foreach ($row in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks', 'timestamp-frequency') })) {
            $hiddenChildren = $row.scenario -like '*stack-measure-hidden-*'
            $expected = switch ($row.metric) {
                'measures' { 100000L }
                'backing-children' { [long]$row.window_count }
                'published-width-sum' { if ($hiddenChildren) { 0L } else { 700000L } }
                'published-height-sum' { if ($hiddenChildren) { 0L } else { 900000L } }
                default { throw "Unknown StackPanel measurement invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "StackPanel measurement workload differs: $($row.variant)/$($row.scenario)/$($row.metric)" }
        }
    }

    if ($Scenario -eq 'PlaceholderCleanup') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Placeholder cleanup semantics differ: $($invariant.Name)" }
        }
        foreach ($row in ($rows | Where-Object { $_.metric -notin @('allocated-bytes', 'elapsed-ticks', 'timestamp-frequency') })) {
            $childCount = [long]$row.window_count
            $presentPlaceholder = $row.scenario.Contains('-present-')
            $expected = switch ($row.metric) {
                'operations' { 100000L }
                'retained-children' { $childCount }
                'parent-links' { $childCount }
                'ordered-slot-sum' { $childCount * ($childCount + 1L) * (2L * $childCount + 1L) / 6L }
                'placeholder-windows-reads' { if ($presentPlaceholder) { 200000L } else { 0L } }
                default { throw "Unknown placeholder cleanup invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "Placeholder cleanup workload differs: $($row.variant)/$($row.scenario)/$($row.metric)" }
        }
    }

    if ($Scenario -eq 'SettingsDispose') {
        if ($rows.Count -ne 300) { throw "SettingsViewModel dispose measurement must contain exactly 300 rows, got $($rows.Count)" }
        foreach ($row in $rows) {
            $caseName = $row.scenario.Split(';')[1].Trim()
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'subscription-dispose-attempts' { 100 }
                'released-subscriptions' { 100 }
                'flush-attempts' {
                    if ($caseName -eq 'settings-dispose-subscription-failure' -and $row.variant -eq 'baseline') { 0 } else { 100 }
                }
                'primary-outcomes' { 100 }
                'late-notifications' {
                    if ($caseName -in @('settings-dispose-subscription-failure','settings-dispose-logger-failure') -and $row.variant -eq 'baseline') { 100 } else { 0 }
                }
                'late-keybinding-mutations' {
                    if ($caseName -eq 'settings-dispose-subscription-failure' -and $row.variant -eq 'baseline') { 100 } else { 0 }
                }
                'repeat-dispose-errors' { 0 }
                'active-subscriptions' { 0 }
                'log-attempts' { if ($caseName -eq 'settings-dispose-logger-failure') { 100 } else { 0 } }
                default { throw "Unknown SettingsViewModel dispose invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "SettingsViewModel dispose result differs: $($row.variant)/$caseName/$($row.metric), expected $expected, got $($row.value)" }
        }
    }

    if ($Scenario -eq 'StartupWindowClose') {
        if ($rows.Count -ne 100) { throw "StartupWindow close measurement must contain exactly 100 rows, got $($rows.Count)" }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'subscription-dispose-attempts' { 100 }
                'released-subscriptions' { 100 }
                'flush-attempts' { 100 }
                'primary-outcomes' { 100 }
                'closed-callbacks' { if ($row.variant -eq 'baseline') { 0 } else { 100 } }
                'supplemental-errors' { if ($row.variant -eq 'baseline') { 0 } else { 100 } }
                'repeat-close-errors' { 0 }
                'nonzero-handles' { 0 }
                'active-subscriptions' { 0 }
                default { throw "Unknown StartupWindow close invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "StartupWindow close result differs: $($row.variant)/$($row.metric), expected $expected, got $($row.value)" }
        }
    }

    if ($Scenario -eq 'SettingsWindowClose') {
        if ($rows.Count -ne 360) { throw "SettingsWindow close measurement must contain exactly 360 rows, got $($rows.Count)" }
        foreach ($row in $rows) {
            $caseName = $row.scenario.Split(';')[1].Trim()
            if ($caseName -notin @('settings-window-close-normal','settings-window-close-viewmodel-failure','settings-window-close-combined-failure')) {
                throw "Unknown SettingsWindow close case: $caseName"
            }
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'subscription-dispose-attempts' { 100 }
                'released-subscriptions' { 100 }
                'flush-attempts' { 100 }
                'primary-outcomes' {
                    if ($caseName -eq 'settings-window-close-combined-failure' -and $row.variant -eq 'baseline') { 0 } else { 100 }
                }
                'closed-callbacks' { 100 }
                'supplemental-errors' {
                    if ($caseName -eq 'settings-window-close-combined-failure' -and $row.variant -eq 'candidate') { 100 } else { 0 }
                }
                'retained-logical-focus' {
                    if ($caseName -ne 'settings-window-close-normal' -and $row.variant -eq 'baseline') { 100 } else { 0 }
                }
                'collection-posts' {
                    if ($caseName -ne 'settings-window-close-normal' -and $row.variant -eq 'baseline') { 0 } else { 100 }
                }
                'repeat-close-errors' { 0 }
                'nonzero-handles' { 0 }
                'active-subscriptions' { 0 }
                default { throw "Unknown SettingsWindow close invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "SettingsWindow close result differs: $($row.variant)/$caseName/$($row.metric), expected $expected, got $($row.value)" }
        }
    }

    if ($Scenario -eq 'OverlayHostClose') {
        if ($rows.Count -ne 510) { throw "OverlayHost close comparison must contain exactly 510 rows, got $($rows.Count)" }
        foreach ($row in $rows) {
            $caseName = $row.scenario.Split(';')[1].Trim()
            if ($caseName -notin @('normal','cursor-failure','nonhit-close-failure')) {
                throw "Unknown OverlayHost close case: $caseName"
            }
            $baselineCursorFailure = $row.variant -eq 'baseline' -and $caseName -eq 'cursor-failure'
            $baselineNonHitFailure = $row.variant -eq 'baseline' -and $caseName -eq 'nonhit-close-failure'
            $expected = switch ($row.metric) {
                'lifetimes' { 100 }
                'invalidations' { 100 }
                'cursor-attempts' { 100 }
                'cursor-releases' { 100 }
                'refresh-attempts' { if ($baselineCursorFailure) { 0 } else { 100 } }
                'refresh-releases' { if ($baselineCursorFailure) { 0 } else { 100 } }
                'subscription-attempts' { if ($baselineCursorFailure) { 0 } else { 500 } }
                'subscription-releases' { if ($baselineCursorFailure) { 0 } else { 500 } }
                'dispatch-attempts' { if ($baselineCursorFailure) { 0 } else { 100 } }
                'nonhit-close-attempts' { if ($baselineCursorFailure) { 0 } else { 100 } }
                'nonhit-close-releases' { if ($baselineCursorFailure) { 0 } else { 100 } }
                'hit-close-attempts' { if ($baselineCursorFailure -or $baselineNonHitFailure) { 0 } else { 100 } }
                'hit-close-releases' { if ($baselineCursorFailure -or $baselineNonHitFailure) { 0 } else { 100 } }
                'primary-outcomes' { if ($caseName -eq 'normal') { 0 } else { 100 } }
                'supplemental-errors' { 0 }
                'repeat-actions' { 0 }
                'active-owners' { if ($baselineCursorFailure) { 800 } elseif ($baselineNonHitFailure) { 100 } else { 0 } }
                default { throw "Unknown OverlayHost close invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "OverlayHost close result differs: $($row.variant)/$($row.run)/$caseName/$($row.metric), expected $expected, got $($row.value)" }
        }
    }

    if ($Scenario -eq 'OverlayWindowClose') {
        if ($rows.Count -ne 390) { throw "OverlayWindow close comparison must contain exactly 390 rows, got $($rows.Count)" }
        $overlayWindowCloseRowKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($row in $rows) {
            $caseName = $row.scenario.Split(';')[1].Trim()
            if ($caseName -cnotin @('normal','base-failure','settings-failure')) {
                throw "Unknown OverlayWindow close case: $caseName"
            }
            if ($row.variant -cnotin @('baseline','candidate') -or $row.run -notin 1..5 -or $row.pair -ne $row.run) {
                throw "Unexpected OverlayWindow close variant or pair: $($row.variant)/$($row.run)/$($row.pair)"
            }
            if (!$overlayWindowCloseRowKeys.Add("$($row.variant)/$($row.run)/$caseName/$($row.metric)")) {
                throw "Duplicate OverlayWindow close row: $($row.variant)/$($row.run)/$caseName/$($row.metric)"
            }
            $baselineBaseFailure = $row.variant -eq 'baseline' -and $caseName -eq 'base-failure'
            $baselineSettingsFailure = $row.variant -eq 'baseline' -and $caseName -eq 'settings-failure'
            $expected = switch -CaseSensitive ($row.metric) {
                'lifetimes' { 100 }
                'base-attempts' { 100 }
                'settings-attempts' { if ($baselineBaseFailure) { 0 } else { 100 } }
                'settings-releases' { if ($baselineBaseFailure) { 0 } else { 100 } }
                'workarea-attempts' { if ($baselineBaseFailure -or $baselineSettingsFailure) { 0 } else { 100 } }
                'workarea-releases' { if ($baselineBaseFailure -or $baselineSettingsFailure) { 0 } else { 100 } }
                'scaling-attempts' { if ($baselineBaseFailure -or $baselineSettingsFailure) { 0 } else { 100 } }
                'scaling-releases' { if ($baselineBaseFailure -or $baselineSettingsFailure) { 0 } else { 100 } }
                'content-attempts' { if ($baselineBaseFailure -or $baselineSettingsFailure) { 0 } else { 100 } }
                'content-releases' { if ($baselineBaseFailure -or $baselineSettingsFailure) { 0 } else { 100 } }
                'primary-outcomes' { if ($caseName -eq 'normal') { 0 } else { 100 } }
                'supplemental-errors' { 0 }
                'active-owners' { if ($baselineBaseFailure) { 400 } elseif ($baselineSettingsFailure) { 300 } else { 0 } }
                default { throw "Unknown OverlayWindow close invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "OverlayWindow close result differs: $($row.variant)/$($row.run)/$caseName/$($row.metric), expected $expected, got $($row.value)" }
        }
    }

    if ($Scenario -eq 'OverlayWindowCallbacks') {
        if ($rows.Count -ne 630) { throw "OverlayWindow callback comparison must contain exactly 630 rows, got $($rows.Count)" }
        $overlayWindowCallbackRowKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($row in $rows) {
            $caseName = $row.scenario.Split(';')[1].Trim()
            $callbackCase = [regex]::Match($caseName, '^(settings|scaling|workarea)-(active|queued-before-close|captured-after-close)$')
            if (!$callbackCase.Success) {
                throw "Unknown OverlayWindow callback case: $caseName"
            }
            $callbackKind = $callbackCase.Groups[1].Value
            $callbackState = $callbackCase.Groups[2].Value
            if ($row.variant -cnotin @('baseline','candidate') -or $row.run -notin 1..5 -or $row.pair -ne $row.run) {
                throw "Unexpected OverlayWindow callback variant or pair: $($row.variant)/$($row.run)/$($row.pair)"
            }
            if (!$overlayWindowCallbackRowKeys.Add("$($row.variant)/$($row.run)/$caseName/$($row.metric)")) {
                throw "Duplicate OverlayWindow callback row: $($row.variant)/$($row.run)/$caseName/$($row.metric)"
            }
            $candidateRejectsPost = $row.variant -ceq 'candidate' -and $callbackState -ceq 'captured-after-close'
            $candidateRejectsApply = $row.variant -ceq 'candidate' -and $callbackState -cne 'active'
            $expected = switch -CaseSensitive ($row.metric) {
                'lifetimes' { 100 }
                'posts' { if ($candidateRejectsPost) { 0 } else { 100 } }
                'drained-callbacks' { if ($candidateRejectsPost) { 0 } else { 100 } }
                'transform-applies' { if ($candidateRejectsApply -or $callbackKind -ceq 'settings') { 0 } else { 100 } }
                'resource-applies' { if ($candidateRejectsApply -or $callbackKind -ceq 'workarea') { 0 } else { 100 } }
                'owner-releases' { 400 }
                'pending-callbacks' { 0 }
                default { throw "Unknown OverlayWindow callback invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "OverlayWindow callback result differs: $($row.variant)/$($row.run)/$caseName/$($row.metric), expected $expected, got $($row.value)" }
        }
    }

    if ($Scenario -eq 'TilingOverlayRendererDispose') {
        if ($rows.Count -ne 600) { throw "TilingOverlayRenderer dispose comparison must contain exactly 600 rows, got $($rows.Count)" }
        $tilingOverlayRendererDisposeRowKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($row in $rows) {
            $caseName = $row.scenario.Split(';')[1].Trim()
            if ($caseName -cnotin @('normal','overlay-failure','viewmodel-failure')) {
                throw "Unknown TilingOverlayRenderer dispose case: $caseName"
            }
            if ($row.variant -cnotin @('baseline','candidate') -or $row.run -notin 1..5 -or $row.pair -ne $row.run) {
                throw "Unexpected TilingOverlayRenderer dispose variant or pair: $($row.variant)/$($row.run)/$($row.pair)"
            }
            if (!$tilingOverlayRendererDisposeRowKeys.Add("$($row.variant)/$($row.run)/$caseName/$($row.metric)")) {
                throw "Duplicate TilingOverlayRenderer dispose row: $($row.variant)/$($row.run)/$caseName/$($row.metric)"
            }
            $baselineOverlayFailure = $row.variant -eq 'baseline' -and $caseName -eq 'overlay-failure'
            $baselineViewModelFailure = $row.variant -eq 'baseline' -and $caseName -eq 'viewmodel-failure'
            $expected = switch -CaseSensitive ($row.metric) {
                'lifetimes' { 100 }
                'capture-attempts' { 100 }
                'drag-attempts' { 100 }
                'drag-releases' { 100 }
                'overlay-attempts' { 100 }
                'overlay-releases' { 100 }
                'viewmodel-attempts' { if ($baselineOverlayFailure) { 0 } else { 100 } }
                'viewmodel-releases' { if ($baselineOverlayFailure) { 0 } else { 100 } }
                'display-attempts' { if ($baselineOverlayFailure -or $baselineViewModelFailure) { 0 } else { 100 } }
                'display-releases' { if ($baselineOverlayFailure -or $baselineViewModelFailure) { 0 } else { 100 } }
                'subscriptions-attempts' { if ($baselineOverlayFailure -or $baselineViewModelFailure) { 0 } else { 100 } }
                'subscriptions-releases' { if ($baselineOverlayFailure -or $baselineViewModelFailure) { 0 } else { 100 } }
                'invalidate-attempts' { if ($baselineOverlayFailure -or $baselineViewModelFailure) { 0 } else { 100 } }
                'invalidate-releases' { if ($baselineOverlayFailure -or $baselineViewModelFailure) { 0 } else { 100 } }
                'events-attempts' { if ($baselineOverlayFailure -or $baselineViewModelFailure) { 0 } else { 100 } }
                'events-releases' { if ($baselineOverlayFailure -or $baselineViewModelFailure) { 0 } else { 100 } }
                'primary-outcomes' { if ($caseName -eq 'normal') { 0 } else { 100 } }
                'supplemental-errors' { 0 }
                'repeat-actions' { 0 }
                'active-owners' { if ($baselineOverlayFailure) { 500 } elseif ($baselineViewModelFailure) { 400 } else { 0 } }
                default { throw "Unknown TilingOverlayRenderer dispose invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "TilingOverlayRenderer dispose result differs: $($row.variant)/$($row.run)/$caseName/$($row.metric), expected $expected, got $($row.value)" }
        }
    }

    if ($Scenario -eq 'TilingOverlayRendererResources') {
        if ($rows.Count -ne 210) { throw "TilingOverlayRenderer resource callback comparison must contain exactly 210 rows, got $($rows.Count)" }
        $tilingOverlayRendererResourceRowKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($row in $rows) {
            $caseName = $row.scenario.Split(';')[1].Trim()
            if ($caseName -cnotin @('active','queued-before-dispose','captured-after-dispose')) {
                throw "Unknown TilingOverlayRenderer resource callback case: $caseName"
            }
            if ($row.variant -cnotin @('baseline','candidate') -or $row.run -notin 1..5 -or $row.pair -ne $row.run) {
                throw "Unexpected TilingOverlayRenderer resource callback variant or pair: $($row.variant)/$($row.run)/$($row.pair)"
            }
            if (!$tilingOverlayRendererResourceRowKeys.Add("$($row.variant)/$($row.run)/$caseName/$($row.metric)")) {
                throw "Duplicate TilingOverlayRenderer resource callback row: $($row.variant)/$($row.run)/$caseName/$($row.metric)"
            }
            $candidateRejectsPost = $row.variant -eq 'candidate' -and $caseName -eq 'captured-after-dispose'
            $candidateRejectsApply = $row.variant -eq 'candidate' -and $caseName -ne 'active'
            $expected = switch -CaseSensitive ($row.metric) {
                'lifetimes' { 100 }
                'posts' { if ($candidateRejectsPost) { 0 } else { 100 } }
                'drained-callbacks' { if ($candidateRejectsPost) { 0 } else { 100 } }
                'scaling-reads' { if ($candidateRejectsApply) { 0 } else { 400 } }
                'resource-writes' { if ($candidateRejectsApply) { 0 } else { 400 } }
                'subscription-releases' { 100 }
                'pending-callbacks' { 0 }
                default { throw "Unknown TilingOverlayRenderer resource callback invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "TilingOverlayRenderer resource callback result differs: $($row.variant)/$($row.run)/$caseName/$($row.metric), expected $expected, got $($row.value)" }
        }
    }

    if ($Scenario -eq 'TilingOverlayRendererRemoval') {
        if ($rows.Count -ne 180) { throw "TilingOverlayRenderer removal comparison must contain exactly180 rows, got $($rows.Count)" }
        $tilingOverlayRendererRemovalRowKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($row in $rows) {
            $caseName = $row.scenario.Split(';')[1].Trim()
            if ($caseName -cnotin @('normal','notification-failure')) {
                throw "Unknown TilingOverlayRenderer removal case: $caseName"
            }
            if ($row.variant -cnotin @('baseline','candidate') -or $row.run -notin 1..5 -or $row.pair -ne $row.run) {
                throw "Unexpected TilingOverlayRenderer removal variant or pair: $($row.variant)/$($row.run)/$($row.pair)"
            }
            if (!$tilingOverlayRendererRemovalRowKeys.Add("$($row.variant)/$($row.run)/$caseName/$($row.metric)")) {
                throw "Duplicate TilingOverlayRenderer removal row: $($row.variant)/$($row.run)/$caseName/$($row.metric)"
            }
            $baselineRetainsOwners = $row.variant -ceq 'baseline' -and $caseName -ceq 'notification-failure'
            $expected = switch -CaseSensitive ($row.metric) {
                'cycles' { 100 }
                'removal-notifications' { 100 }
                'primary-outcomes' { if ($caseName -ceq 'normal') { 0 } else { 100 } }
                'detached-models' { 100 }
                'cursor-subscriptions-after-invalidation' { if ($baselineRetainsOwners) { 100 } else { 0 } }
                'node-references-after-invalidation' { if ($baselineRetainsOwners) { 100 } else { 0 } }
                'command-references-after-invalidation' { if ($baselineRetainsOwners) { 300 } else { 0 } }
                'models-needing-fixture-cleanup' { if ($baselineRetainsOwners) { 100 } else { 0 } }
                'cursor-subscriptions-after-fixture-cleanup' { 0 }
                default { throw "Unknown TilingOverlayRenderer removal invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "TilingOverlayRenderer removal result differs: $($row.variant)/$($row.run)/$caseName/$($row.metric), expected $expected, got $($row.value)" }
        }
    }

    if ($Scenario -eq 'TilingOverlayRendererCreation') {
        if ($rows.Count -ne 220) { throw "TilingOverlayRenderer creation comparison must contain exactly220 rows, got $($rows.Count)" }
        $tilingOverlayRendererCreationRowKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($row in $rows) {
            $caseName = $row.scenario.Split(';')[1].Trim()
            if ($caseName -cnotin @('normal','title-failure')) {
                throw "Unknown TilingOverlayRenderer creation case: $caseName"
            }
            if ($row.variant -cnotin @('baseline','candidate') -or $row.run -notin 1..5 -or $row.pair -ne $row.run) {
                throw "Unexpected TilingOverlayRenderer creation variant or pair: $($row.variant)/$($row.run)/$($row.pair)"
            }
            if (!$tilingOverlayRendererCreationRowKeys.Add("$($row.variant)/$($row.run)/$caseName/$($row.metric)")) {
                throw "Duplicate TilingOverlayRenderer creation row: $($row.variant)/$($row.run)/$caseName/$($row.metric)"
            }
            $baselineRetainsOwners = $row.variant -ceq 'baseline' -and $caseName -ceq 'title-failure'
            $expected = switch -CaseSensitive ($row.metric) {
                'cycles' { 100 }
                'title-reads' { if ($caseName -ceq 'normal') { 200 } else { 100 } }
                'created-models' { 100 }
                'published-models' { if ($caseName -ceq 'normal') { 100 } else { 0 } }
                'primary-outcomes' { if ($caseName -ceq 'normal') { 0 } else { 100 } }
                'cursor-subscriptions-after-invalidation' { if ($baselineRetainsOwners) { 100 } else { 0 } }
                'window-subscriptions-after-invalidation' { if ($baselineRetainsOwners) { 500 } else { 0 } }
                'node-references-after-invalidation' { if ($baselineRetainsOwners) { 100 } else { 0 } }
                'models-needing-fixture-cleanup' { if ($baselineRetainsOwners) { 100 } else { 0 } }
                'cursor-subscriptions-after-fixture-cleanup' { 0 }
                'window-subscriptions-after-fixture-cleanup' { 0 }
                default { throw "Unknown TilingOverlayRenderer creation invariant: $($row.metric)" }
            }
            if ([long]$row.value -ne $expected) { throw "TilingOverlayRenderer creation result differs: $($row.variant)/$($row.run)/$caseName/$($row.metric), expected $expected, got $($row.value)" }
        }
    }

    if ($Scenario -eq 'TilingWindowLifetime') {
        if ($rows.Count -ne 520) { throw "TilingWindowLifetime comparison requires exactly520 rows, got $($rows.Count)" }
        $lifetimeSchema = 'experiment_id|snapshot_id|scenario|configuration|window_count|metric|unit|run|value|iterations|instrument|perf_id|variant|pair|binary_sha256|benchmark_sha256'
        $lifetimeRowKeys = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::Ordinal)
        foreach ($row in $rows) {
            if (($row.PSObject.Properties.Name -join '|') -cne $lifetimeSchema) { throw 'TilingWindowLifetime row schema differs' }
            $caseMatch = [regex]::Match($row.scenario, '^Actual TilingWindowViewModel lifetime; (normal|removal-failure|replacement-failure|removed-reentry);100 independent managed lifetimes per case per process$')
            if (!$caseMatch.Success) { throw "Unknown TilingWindowLifetime row scenario: $($row.scenario)" }
            $caseName = $caseMatch.Groups[1].Value
            $expectedWindows = if ($caseName -ceq 'removal-failure') { '1' } else { '2' }
            if ($row.metric -cnotin $tilingWindowLifetimeUnits.Keys -or $row.variant -cnotin @('baseline','candidate') -or
                [string]$row.run -cnotmatch '^[1-5]$' -or [string]$row.pair -cne [string]$row.run -or
                [string]$row.window_count -cne $expectedWindows -or [string]$row.iterations -cne '100' -or
                $row.unit -cne $tilingWindowLifetimeUnits[$row.metric] -or $row.instrument -cne $tilingWindowLifetimeInstrument -or
                $row.configuration -cne "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0" -or
                $row.experiment_id -cne $ExperimentId -or $row.perf_id -cne 'PERF-016' -or [string]$row.value -cnotmatch '^(0|[1-9][0-9]*)$') {
                throw "TilingWindowLifetime row dimensions or metadata differ: $caseName/$($row.metric)"
            }
            $expectedSnapshot = if ($row.variant -ceq 'baseline') { $BaselineSnapshotId } else { $SnapshotId }
            $expectedBinaryHash = if ($row.variant -ceq 'baseline') { $binaryRecord.Hash } else { $binaryHash }
            if ($row.snapshot_id -cne $expectedSnapshot -or $row.binary_sha256 -cne $expectedBinaryHash -or
                $row.benchmark_sha256 -cne $benchmarkHash -or $row.binary_sha256 -cnotmatch '^[0-9A-F]{64}$' -or
                $row.benchmark_sha256 -cnotmatch '^[0-9A-F]{64}$') { throw 'TilingWindowLifetime row source/binary provenance differs' }
            $key = "$($row.variant)/$($row.run)/$caseName/$($row.metric)"
            if (!$lifetimeRowKeys.TryAdd($key,$row)) { throw "Duplicate TilingWindowLifetime row: $key" }
            $expected = Get-TilingWindowLifetimeExpectedCounter $row.variant $caseName $row.metric
            if ([string]$row.value -cne [string]$expected) { throw "TilingWindowLifetime result differs: $key, expected $expected, got $($row.value)" }
        }
        foreach ($run in 1..5) {
            foreach ($caseName in $tilingWindowLifetimeCases) {
                foreach ($metric in $tilingWindowLifetimeUnits.Keys) {
                    foreach ($variant in @('baseline','candidate')) {
                        if (!$lifetimeRowKeys.ContainsKey("$variant/$run/$caseName/$metric")) { throw "Missing TilingWindowLifetime row: $variant/$run/$caseName/$metric" }
                    }
                    if ($caseName -ceq 'normal' -and
                        [string]$lifetimeRowKeys["baseline/$run/$caseName/$metric"].value -cne [string]$lifetimeRowKeys["candidate/$run/$caseName/$metric"].value) {
                        throw "TilingWindowLifetime normal behavior differs: $run/$metric"
                    }
                }
            }
        }
    }
    if ($Scenario -eq 'TilingOverlayRendererRecovery') {
        if ($rows.Count -ne 180) { throw "TilingOverlayRenderer recovery comparison requires exactly180 rows, got $($rows.Count)" }
        $recoverySchema = 'experiment_id|snapshot_id|scenario|configuration|window_count|metric|unit|run|value|iterations|instrument|perf_id|variant|pair|binary_sha256|benchmark_sha256'
        $recoveryRowKeys = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::Ordinal)
        foreach ($row in $rows) {
            if (($row.PSObject.Properties.Name -join '|') -cne $recoverySchema) { throw 'TilingOverlayRenderer recovery row schema differs' }
            $recoveryCaseMatch = [regex]::Match($row.scenario, '^Actual TilingOverlayRenderer\.UpdateOverlay same-snapshot recovery; (normal|removal-failure);100 cycles with four initial windows and two requested survivors per case per process$')
            if (!$recoveryCaseMatch.Success) { throw "Unknown TilingOverlayRenderer recovery scenario: $($row.scenario)" }
            $caseName = $recoveryCaseMatch.Groups[1].Value
            if ($row.metric -cnotin $overlayRecoveryUnits.Keys -or $row.variant -cnotin @('baseline','candidate') -or
                [string]$row.run -cnotmatch '^[1-5]$' -or [string]$row.pair -cne [string]$row.run -or
                [string]$row.window_count -cne '4' -or [string]$row.iterations -cne '100' -or
                $row.unit -cne $overlayRecoveryUnits[$row.metric] -or $row.instrument -cne $overlayRecoveryInstrument -or
                $row.configuration -cne "Release; $testFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0" -or
                $row.experiment_id -cne $ExperimentId -or $row.perf_id -cne 'PERF-016' -or [string]$row.value -cnotmatch '^(0|[1-9][0-9]*)$') {
                throw "TilingOverlayRenderer recovery row dimensions, units or metadata differ: $caseName/$($row.metric)"
            }
            $expectedSnapshot = if ($row.variant -ceq 'baseline') { $BaselineSnapshotId } else { $SnapshotId }
            $expectedBinaryHash = if ($row.variant -ceq 'baseline') { $binaryRecord.Hash } else { $binaryHash }
            if ($row.snapshot_id -cne $expectedSnapshot -or $row.binary_sha256 -cne $expectedBinaryHash -or
                $row.benchmark_sha256 -cne $benchmarkHash -or $row.binary_sha256 -cnotmatch '^[0-9A-F]{64}$' -or
                $row.benchmark_sha256 -cnotmatch '^[0-9A-F]{64}$') { throw 'TilingOverlayRenderer recovery row source/binary provenance differs' }
            $key = "$($row.variant)/$($row.run)/$caseName/$($row.metric)"
            if (!$recoveryRowKeys.TryAdd($key,$row)) { throw "Duplicate TilingOverlayRenderer recovery row: $key" }
            $expected = Get-OverlayRecoveryExpectedCounter $row.variant $caseName $row.metric
            if ([string]$row.value -cne [string]$expected) { throw "TilingOverlayRenderer recovery result differs: $key, expected $expected, got $($row.value)" }
        }
        foreach ($run in 1..5) {
            foreach ($caseName in $overlayRecoveryCases) {
                foreach ($metric in $overlayRecoveryUnits.Keys) {
                    foreach ($variant in @('baseline','candidate')) {
                        if (!$recoveryRowKeys.ContainsKey("$variant/$run/$caseName/$metric")) { throw "Missing recovery row: $variant/$run/$caseName/$metric" }
                    }
                    if ($caseName -ceq 'normal' -and
                        $recoveryRowKeys["baseline/$run/$caseName/$metric"].value -cne $recoveryRowKeys["candidate/$run/$caseName/$metric"].value) {
                        throw "TilingOverlayRenderer recovery normal behavior differs: $run/$metric"
                    }
                }
            }
        }
    }
    if ($Scenario -in @('TilingOverlayRendererRecovery','TilingWindowLifetime','RefreshWindowSnapshot','LayoutCallbackCache','ContainsWindow','FocusRejectedAdmission','TransitionCompletion','RuntimeStateCount')) {
        $recoveryProcesses = @($overlayRecoveryProcesses | Sort-Object Start)
        if ($recoveryProcesses.Count -ne 10 -or $overlayRecoveryRunIds.Count -ne 10 -or $overlayRecoveryExecutionIds.Count -ne 10 -or
            @((Get-ChildItem -LiteralPath $experimentRoot -Filter '*.trx' -File)).Count -ne 10 -or
            ($recoveryProcesses.Run -join ',') -cne 'baseline-1,candidate-1,candidate-2,baseline-2,baseline-3,candidate-3,candidate-4,baseline-4,baseline-5,candidate-5') {
            throw 'TilingOverlayRenderer recovery requires ten distinct processes in the five-pair alternating order'
        }
        for ($index = 1; $index -lt $recoveryProcesses.Count; $index++) {
            if ($recoveryProcesses[$index-1].Finish -gt $recoveryProcesses[$index].Start) { throw 'TilingOverlayRenderer recovery process intervals overlap' }
        }
        foreach ($process in $recoveryProcesses) {
            if ((Get-FileHash -LiteralPath "$experimentRoot/$($process.Run).trx").Hash -cne $process.TrxSHA256) { throw "Recovery TRX changed after validation: $($process.Run)" }
        }
        foreach ($record in $candidateBuildRecords) {
            if ((Get-FileHash -LiteralPath (Join-Path $repositoryRoot $record.Path)).Hash -cne $record.SHA256) { throw "Recovery source changed during measurement: $($record.Path)" }
        }
        if (@(Get-ChildItem -LiteralPath $binaryRoot -Recurse -File).Count -ne $overlayWindowCallbackCandidateFiles.Count -or
            @(Get-ChildItem -LiteralPath "$experimentRoot/baseline" -Recurse -File).Count -ne $overlayWindowCallbackBaselineFiles.Count) {
            throw 'TilingOverlayRenderer recovery binary tree file count changed during measurement'
        }
        foreach ($record in $overlayWindowCallbackBinaryChecks) {
            if ((Get-FileHash -LiteralPath (Join-Path $binaryRoot $record.Path)).Hash -cne $record.CandidateSHA256 -or
                (Get-FileHash -LiteralPath (Join-Path "$experimentRoot/baseline" $record.Path)).Hash -cne $record.BaselineSHA256) {
                throw "Recovery binary input changed during measurement: $($record.Path)"
            }
        }
        $processTimesFile = if ($Scenario -eq 'FocusRejectedAdmission') { 'focus-rejected-process-times.csv' } elseif ($Scenario -eq 'ContainsWindow') { 'contains-window-process-times.csv' } elseif ($Scenario -eq 'LayoutCallbackCache') { 'layout-callback-process-times.csv' } elseif ($Scenario -eq 'TilingWindowLifetime') { 'lifetime-process-times.csv' } elseif ($Scenario -eq 'RefreshWindowSnapshot') { 'refresh-window-snapshot-process-times.csv' } elseif ($Scenario -eq 'TransitionCompletion') { 'transition-completion-process-times.csv' } elseif ($Scenario -eq 'RuntimeStateCount') { 'runtime-state-count-process-times.csv' } else { 'recovery-process-times.csv' }
        $recoveryProcesses | Export-Csv -NoTypeInformation (Join-Path $experimentRoot $processTimesFile)
    }

    if ($Scenario -eq 'WorkspaceInvariant') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('geometry-digest', 'minsize-reads', 'rounds') } | Group-Object window_count, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Workspace transaction semantics differ: $($invariant.Name)" }
        }
    }
    if ($Scenario -eq 'TransitionDispatch') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('position-writes', 'transitions', 'timestamp-frequency') } | Group-Object window_count, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Transition effects differ: $($invariant.Name)" }
        }
    }

    if ($Scenario -eq 'DiagnosticReuse') {
        foreach ($geometry in ($rows | Where-Object { $_.metric -in @('geometry-checksum', 'geometry-digest') } | Group-Object window_count, metric)) {
            if (@($geometry.Group.value | Select-Object -Unique).Count -ne 1) {
                throw "Geometry differs between DiagnosticReuse variants or repetitions for $($geometry.Name) windows"
            }
        }
    }
    if ($Scenario -in @('PreviewCache','PreviewCacheManaged')) {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('plan-digest', 'pointers', 'accepted-plans', 'timestamp-frequency') } | Group-Object window_count, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) {
                throw "Preview semantics differ between variants or repetitions: $($invariant.Name)"
            }
        }
    }
    if ($Scenario -eq 'PostMoveTimer') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('deferred-probes', 'cycles') } | Group-Object metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) {
                throw "Post-move retry semantics differ: $($invariant.Name)"
            }
        }
    }
    if ($Scenario -eq 'ThemeLocalImage') {
        foreach ($invariant in ($rows | Where-Object { $_.metric -in @('pixel-digest','pixels','reuse-reads','timestamp-frequency','retained-values-brushes','cycles') } | Group-Object scenario, metric)) {
            if (@($invariant.Group.value | Select-Object -Unique).Count -ne 1) { throw "Local image output/lifetime differs: $($invariant.Name)" }
        }
    }
    if ($Scenario -eq 'HookLifetime') {
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'installed-hooks' { 200 }
                'timer-creations' { 200 }
                'timer-cleanup-attempts' { 200 }
                'exceptional-unhook-attempts' { if ($row.variant -eq 'baseline') { 0 } else { 100 } }
                'exceptional-remaining-hooks' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'original-loop-errors' { 100 }
                'normal-unhook-attempts' { 100 }
                'normal-completions' { 100 }
                'duplicate-unhooks' { 0 }
                default { throw "Unknown hook lifetime metric: $($row.metric)" }
            }
            if ([int]$row.value -ne $expected) { throw "Hook lifetime result differs: $($row.variant)/$($row.run)/$($row.scenario)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'HookDisposal') {
        if (@($rows).Count -ne 160) { throw 'Hook disposal comparison requires 160 counter rows' }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'quit-post-attempts' { 100 }
                'observed-post-failures' { if ($row.variant -eq 'baseline') { 0 } else { 100 } }
                'completion-successes' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'late-subscriber-callbacks' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'suppressed-late-input' { 0 }
                'completed-workers' { 100 }
                'live-workers-after-join' { 0 }
                default { throw "Unknown hook disposal metric: $($row.metric)" }
            }
            if ([int]$row.value -ne $expected) { throw "Hook disposal result differs: $($row.variant)/$($row.run)/$($row.scenario)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'WindowDraggerLifetime') {
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'late-subscriptions' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'late-writes' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'normal-writes' { 100 }
                'normal-starts' { 100 }
                'normal-ends' { 100 }
                default { throw "Unknown WindowDragger lifetime metric: $($row.metric)" }
            }
            if ([int]$row.value -ne $expected) { throw "WindowDragger lifetime result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'ModifierDragLifetime') {
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'late-subscriptions' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'late-writes' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'pending-completions' { if ($row.variant -eq 'baseline') { 0 } else { 100 } }
                'normal-writes' { 100 }
                'normal-starts' { 100 }
                'normal-ends' { 100 }
                default { throw "Unknown modifier drag lifetime metric: $($row.metric)" }
            }
            if ([int]$row.value -ne $expected) { throw "Modifier drag lifetime result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
    }

    if ($Scenario -eq 'LowLevelPatternLifetime') {
        if (@($rows).Count -ne 90) { throw 'Pattern lifetime comparison requires 90 counter rows' }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'normal-notifications' { 100 }
                'queued-notifications' { 200 }
                'incorrect-queued-snapshots' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'late-notifications' { if ($row.variant -eq 'baseline') { 300 } else { 0 } }
                'stale-unrelated-suppressions' { if ($row.variant -eq 'baseline') { 200 } else { 0 } }
                'lost-owned-releases' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'retained-drain-subscriptions' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'remaining-subscriptions' { 0 }
                default { throw "Unknown pattern lifetime metric: $($row.metric)" }
            }
            if ([int]$row.value -ne $expected) { throw "Pattern lifetime result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
    }

    if ($Scenario -eq 'DelayedCommandLifetime') {
        if (@($rows).Count -ne 60) { throw 'Delayed command comparison requires 60 counter rows' }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'delay-attempts' { 200 }
                'active-actions' { 100 }
                'late-actions' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'cancellations' { 100 }
                'completed-runs' { 200 }
                default { throw "Unknown delayed command metric: $($row.metric)" }
            }
            if ([int]$row.value -ne $expected) { throw "Delayed command result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
    }

    if ($Scenario -eq 'VirtualDesktopCallbackLifetime') {
        if (@($rows).Count -ne 100) { throw 'Virtual desktop callback comparison requires 100 counter rows' }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'active-state-writes' { 100 }
                'active-feature-probes' { 100 }
                'active-name-reads' { 100 }
                'active-toasts' { 100 }
                'late-state-writes' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'late-feature-probes' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'late-name-reads' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'late-toasts' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'completed-callbacks' { 200 }
                default { throw "Unknown virtual desktop callback metric: $($row.metric)" }
            }
            if ([int]$row.value -ne $expected) { throw "Virtual desktop callback result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'VirtualDesktopRemovalLifetime') {
        if (@($rows).Count -ne 60) { throw 'Virtual desktop removal comparison requires 60 counter rows' }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'active-state-reads' { 100 }
                'active-clears' { 100 }
                'late-state-reads' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'late-clears' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'completed-callbacks' { 200 }
                default { throw "Unknown virtual desktop removal metric: $($row.metric)" }
            }
            if ([int]$row.value -ne $expected) { throw "Virtual desktop removal result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'TilingNotificationLifetime') {
        if (@($rows).Count -ne 50) { throw 'Tiling notification comparison requires 50 counter rows' }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'active-callbacks' { 100 }
                'late-callbacks' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'completed-invocations' { 200 }
                'owned-releases' { 100 }
                default { throw "Unknown tiling notification metric: $($row.metric)" }
            }
            if ([int]$row.value -ne $expected) { throw "Tiling notification result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'DirectHotkeyLifetime') {
        if (@($rows).Count -ne 70) { throw 'Direct hotkey comparison requires 70 counter rows' }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'active-executions' { 100 }
                'active-presentations' { 100 }
                'late-executions' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'late-presentations' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'completed-invocations' { 200 }
                'owned-releases' { 100 }
                default { throw "Unknown direct hotkey metric: $($row.metric)" }
            }
            if ([int]$row.value -ne $expected) { throw "Direct hotkey result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'MicaCallbackLifetime') {
        if (@($rows).Count -ne 90) { throw 'Mica callback comparison requires 90 counter rows' }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'active-posts' { 100 }
                'active-applies' { 100 }
                'late-posts' { if ($row.variant -eq 'baseline') { 100 } else { 0 } }
                'late-applies' { 0 }
                'queued-before-close-posts' { 100 }
                'queued-after-close-applies' { 0 }
                'owned-releases' { 100 }
                'completed-callbacks' { 300 }
                default { throw "Unknown Mica callback metric: $($row.metric)" }
            }
            if ([int]$row.value -ne $expected) { throw "Mica callback result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'LoadedUpdateCheckLifetime') {
        if (@($rows).Count -ne 100) { throw 'Loaded update-check comparison requires 100 counter rows' }
        foreach ($row in $rows) {
            $expected = switch ($row.metric) {
                'cycles' { 100 }
                'active-posts' { 100 }
                'active-runs' { 100 }
                'burst-posts' { if ($row.variant -eq 'baseline') { 300 } else { 100 } }
                'burst-runs' { if ($row.variant -eq 'baseline') { 300 } else { 100 } }
                'late-posts' { 0 }
                'late-runs' { 0 }
                'completed-starts' { 500 }
                'owned-releases' { 100 }
                'pending-callbacks' { 0 }
                default { throw "Unknown loaded update-check metric: $($row.metric)" }
            }
            if ([int]$row.value -ne $expected) { throw "Loaded update-check result differs: $($row.variant)/$($row.run)/$($row.metric)" }
        }
    }
    if ($Scenario -eq 'ArrangeFailureBookkeeping') {
        if ($rows.Count -ne 1920) { throw "ArrangeFailureBookkeeping comparison requires exactly 1920 rows, got $($rows.Count)" }
        $bookkeepingSchema = 'experiment_id|snapshot_id|scenario|configuration|window_count|metric|unit|run|value|iterations|instrument|perf_id|variant|pair|binary_sha256|benchmark_sha256'
        $bookkeepingRowKeys = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::Ordinal)
        foreach ($row in $rows) {
            if (($row.PSObject.Properties.Name -join '|') -cne $bookkeepingSchema) { throw 'ArrangeFailureBookkeeping row schema differs' }
            $caseMatch = [regex]::Match($row.scenario, '^Actual TilingService\.UpdateTree arrange-failure bookkeeping; (arrange-bookkeeping-(ordinary|ms)-(1|4|10)-(stable|pending));256 discarded warmups then100 successful calls;empty new-window set;pending-state verified after every call$')
            if (!$caseMatch.Success) { throw "Unknown ArrangeFailureBookkeeping scenario: $($row.scenario)" }
            $caseName = $caseMatch.Groups[1].Value
            $windowCount = [int]$caseMatch.Groups[3].Value
            $pendingCase = $caseMatch.Groups[4].Value -ceq 'pending'
            if ($caseName -cnotin $arrangeFailureBookkeepingCases -or $row.metric -cnotin $arrangeFailureBookkeepingUnits.Keys -or
                $row.variant -cnotin @('baseline','candidate') -or [string]$row.run -cnotmatch '^[1-5]$' -or
                [string]$row.pair -cne [string]$row.run -or [string]$row.window_count -cne [string]$windowCount -or
                [string]$row.iterations -cne '100' -or $row.unit -cne $arrangeFailureBookkeepingUnits[$row.metric] -or
                $row.experiment_id -cne $ExperimentId -or $row.perf_id -cne 'PERF-006' -or
                $row.configuration -cne "Release; $testFramework; x64; no debugger; DOTNET_TieredCompilation=0" -or
                $row.instrument -cne $arrangeFailureBookkeepingInstrument -or [string]$row.value -cnotmatch '^(0|[1-9][0-9]*)$') {
                throw "ArrangeFailureBookkeeping row dimensions, units or metadata differ: $caseName/$($row.metric)"
            }
            $expectedSnapshot = if ($row.variant -ceq 'baseline') { $BaselineSnapshotId } else { $SnapshotId }
            $expectedBinaryHash = if ($row.variant -ceq 'baseline') { $binaryRecord.Hash } else { $binaryHash }
            if ($row.snapshot_id -cne $expectedSnapshot -or $row.binary_sha256 -cne $expectedBinaryHash -or
                $row.benchmark_sha256 -cne $benchmarkHash -or $row.binary_sha256 -cnotmatch '^[0-9A-F]{64}$' -or
                $row.benchmark_sha256 -cnotmatch '^[0-9A-F]{64}$') { throw 'ArrangeFailureBookkeeping row source/binary provenance differs' }
            $key = "$($row.variant)/$($row.run)/$caseName/$($row.metric)"
            if (!$bookkeepingRowKeys.TryAdd($key, $row)) { throw "Duplicate ArrangeFailureBookkeeping row: $key" }
            $value = [long]::Parse([string]$row.value, [Globalization.CultureInfo]::InvariantCulture)
            $expected = switch -CaseSensitive ($row.metric) {
                { $_ -cin @('iterations','arranged-passes','geometry-checks','identity-checks','revision-checks') } { 100 }
                'minsize-reads' { 100 * $windowCount }
                'handle-reads' { if ($row.variant -ceq 'baseline' -or $pendingCase) { 100 * $windowCount } else { 0 } }
                'pending-notifications' { if ($pendingCase) { 1 } else { 0 } }
                'allocated-bytes' { if ($value -le 0) { throw "Invalid ArrangeFailureBookkeeping allocation: $key" }; $null }
                'geometry-checksum' { if ($value -gt [uint32]::MaxValue -or $value -eq 2166136261) { throw "Invalid ArrangeFailureBookkeeping checksum: $key" }; $null }
                default { 0 }
            }
            if ($null -ne $expected -and $value -ne $expected) {
                throw "ArrangeFailureBookkeeping invariant differs: $key, expected $expected, got $value"
            }
        }
        foreach ($caseName in $arrangeFailureBookkeepingCases) {
            foreach ($run in 1..5) {
                foreach ($metric in $arrangeFailureBookkeepingUnits.Keys) {
                    foreach ($variant in @('baseline','candidate')) {
                        if (!$bookkeepingRowKeys.ContainsKey("$variant/$run/$caseName/$metric")) {
                            throw "Missing ArrangeFailureBookkeeping row: $variant/$run/$caseName/$metric"
                        }
                    }
                    $baselineValue = [long]$bookkeepingRowKeys["baseline/$run/$caseName/$metric"].value
                    $candidateValue = [long]$bookkeepingRowKeys["candidate/$run/$caseName/$metric"].value
                    if ($metric -ceq 'allocated-bytes') {
                        if ($candidateValue -ge $baselineValue) { throw "ArrangeFailureBookkeeping allocations did not improve: $run/$caseName ($baselineValue -> $candidateValue)" }
                    }
                    elseif ($metric -cne 'handle-reads') {
                        $firstValue = [long]$bookkeepingRowKeys["baseline/1/$caseName/$metric"].value
                        if ($baselineValue -ne $candidateValue -or $candidateValue -ne $firstValue) {
                            throw "ArrangeFailureBookkeeping semantic counter differs between variants/repetitions: $run/$caseName/$metric"
                        }
                    }
                }
            }
        }
        foreach ($layout in @('ordinary','ms')) {
            foreach ($windowCount in @(1,4,10)) {
                $stableGeometry = $bookkeepingRowKeys["baseline/1/arrange-bookkeeping-$layout-$windowCount-stable/geometry-checksum"].value
                $pendingGeometry = $bookkeepingRowKeys["baseline/1/arrange-bookkeeping-$layout-$windowCount-pending/geometry-checksum"].value
                if ($stableGeometry -cne $pendingGeometry) { throw "ArrangeFailureBookkeeping pending state changed geometry: $layout/$windowCount" }
            }
        }
        $bookkeepingExpectedOrder = 'baseline-1,candidate-1,candidate-2,baseline-2,baseline-3,candidate-3,candidate-4,baseline-4,baseline-5,candidate-5'
        $bookkeepingProcesses = @($arrangeFailureBookkeepingProcessTimes | Sort-Object Start)
        if ($bookkeepingProcesses.Count -ne 10 -or ($bookkeepingProcesses.RunId -join ',') -cne $bookkeepingExpectedOrder) {
            throw 'ArrangeFailureBookkeeping process count or alternating order differs'
        }
        for ($index = 1; $index -lt $bookkeepingProcesses.Count; $index++) {
            if ($bookkeepingProcesses[$index].Start -lt $bookkeepingProcesses[$index - 1].Finish) { throw 'ArrangeFailureBookkeeping processes overlap' }
        }
        foreach ($record in $candidateBuildRecords) {
            if ((Get-FileHash -LiteralPath (Join-Path $repositoryRoot $record.Path)).Hash -cne $record.SHA256 -or
                (Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $record.Path)).Hash -cne $record.SHA256) {
                throw "ArrangeFailureBookkeeping candidate source changed during measurement: $($record.Path)"
            }
        }
        foreach ($record in $arrangeFailureBookkeepingBaselineBuildRecords) {
            if ((Get-FileHash -LiteralPath (Join-Path "$baselineRoot/source" $record.Path)).Hash -cne $record.SHA256) {
                throw "ArrangeFailureBookkeeping baseline source changed during measurement: $($record.Path)"
            }
        }
        foreach ($tree in @($binaryRoot,"$experimentRoot/baseline")) {
            if (@(Get-ChildItem -LiteralPath $tree -Recurse -File).Count -ne $overlayWindowCallbackBinaryChecks.Count) {
                throw 'ArrangeFailureBookkeeping binary tree file count changed during measurement'
            }
        }
        foreach ($record in $overlayWindowCallbackBinaryChecks) {
            if ((Get-FileHash -LiteralPath (Join-Path $binaryRoot $record.Path)).Hash -cne $record.CandidateSHA256 -or
                (Get-FileHash -LiteralPath (Join-Path "$experimentRoot/baseline" $record.Path)).Hash -cne $record.BaselineSHA256) {
                throw "ArrangeFailureBookkeeping binary input changed during measurement: $($record.Path)"
            }
        }
        $bookkeepingProcesses | Export-Csv -NoTypeInformation "$experimentRoot/bookkeeping-process-times.csv"
    }
    if ($Scenario -eq 'LayoutCallbackCache') {
        if ($rows.Count -ne 300) { throw 'LayoutCallbackCache requires exactly300 measurement rows' }
        $layoutKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($row in $rows) {
            $case = [regex]::Match($row.scenario, '^Actual LayoutInvalidationQueue callback scheduling; layout-callback-(1|10|50);1000 discarded warmups then10000 completed invalidation/drain cycles$')
            if (!$case.Success -or $row.metric -cnotin $layoutCallbackUnits.Keys -or
                $row.variant -cnotin @('baseline','candidate') -or [string]$row.run -cnotmatch '^[1-5]$' -or
                [string]$row.pair -cne [string]$row.run -or $row.window_count -ne 0 -or $row.iterations -ne 10000) {
                throw 'LayoutCallbackCache measurement dimensions differ'
            }
            $key = "$($row.variant)/$($row.run)/$($case.Groups[1].Value)/$($row.metric)"
            if (!$layoutKeys.Add($key)) { throw "Duplicate LayoutCallbackCache row: $key" }
            $value = [long]::Parse([string]$row.value, [Globalization.CultureInfo]::InvariantCulture)
            $expected = switch -CaseSensitive ($row.metric) {
                'invalidations' { 10000L * [long]$case.Groups[1].Value }
                { $_ -cin @('posts','applies','cycles') } { 10000L }
                'eligibility-reads' { 20000L }
                'errors' { 0L }
                'callback-instance-changes' { if ($row.variant -ceq 'baseline') { 10000L } else { 0L } }
                'allocated-bytes' { if ($value -lt 0) { throw "Negative allocation: $key" }; $null }
                { $_ -cin @('elapsed-ticks','timestamp-frequency') } {
                    if ($value -le 0) { throw "Nonpositive timing/frequency: $key" }
                    $null
                }
            }
            if ($null -ne $expected -and $value -ne $expected) { throw "LayoutCallbackCache semantics differ: $key" }
        }
        foreach ($record in $candidateBuildRecords) {
            if ((Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $record.Path)).Hash -cne $record.SHA256) {
                throw "Frozen candidate changed during measurement: $($record.Path)"
            }
        }
        foreach ($record in $layoutCallbackBaselineBuildRecords) {
            if ((Get-FileHash -LiteralPath (Join-Path "$baselineRoot/source" $record.Path)).Hash -cne $record.SHA256) {
                throw "Frozen baseline changed during measurement: $($record.Path)"
            }
        }
        # Verify-LayoutCallbackCache.ps1 independently derives allocation
        # acceptance and descriptive timing from the original raw TRX matrix.
    }
    if ($Scenario -eq 'ContainsWindow') {
        if ($rows.Count -ne 840) { throw 'ContainsWindow requires exactly 840 measurement rows' }
        $containsKeys = [Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
        $containsFrequencies = [Collections.Generic.HashSet[long]]::new()
        foreach ($row in $rows) {
            $case = [regex]::Match($row.scenario, '^Actual MasterSatelliteLayoutEngine\.ContainsWindow; contains-window-(master|first|last|absent)-(1|4|9);1000 discarded warmups then100000 calls$')
            if (!$case.Success -or $row.metric -cnotin $containsWindowUnits.Keys -or
                $row.variant -cnotin @('baseline','candidate') -or [string]$row.run -cnotmatch '^[1-5]$' -or
                [string]$row.pair -cne [string]$row.run -or $row.window_count -ne 1 + [int]$case.Groups[2].Value -or $row.iterations -ne 100000) {
                throw 'ContainsWindow measurement dimensions differ'
            }
            $caseName = "$($case.Groups[1].Value)-$($case.Groups[2].Value)"
            $key = "$($row.variant)/$($row.run)/$caseName/$($row.metric)"
            $value = [long]::Parse([string]$row.value, [Globalization.CultureInfo]::InvariantCulture)
            if (!$containsKeys.TryAdd($key, $value)) { throw "Duplicate ContainsWindow row: $key" }
            $expected = switch -CaseSensitive ($row.metric) {
                'calls' { 100000L }
                'matches' { if ($case.Groups[1].Value -ceq 'absent') { 0L } else { 100000L } }
                'equality-calls' {
                    if ($case.Groups[1].Value -ceq 'master') { 100000L }
                    elseif ($case.Groups[1].Value -ceq 'first') { 200000L }
                    else { (1L + [long]$case.Groups[2].Value) * 100000L }
                }
                'hash-calls' { 0L }
                'allocated-bytes' { if ($value -lt 0) { throw "Negative allocation: $key" }; $null }
                { $_ -cin @('elapsed-ticks','timestamp-frequency') } {
                    if ($value -le 0) { throw "Nonpositive timing/frequency: $key" }
                    if ($_ -ceq 'timestamp-frequency') { [void]$containsFrequencies.Add($value) }
                    $null
                }
            }
            if ($null -ne $expected -and $value -ne $expected) { throw "ContainsWindow semantics differ: $key" }
        }
        if ($containsFrequencies.Count -ne 1) { throw 'ContainsWindow timestamp frequency differs' }
        foreach ($caseName in @('master','first','last','absent')) {
            foreach ($satellites in @(1,4,9)) {
                foreach ($pair in 1..5) {
                    foreach ($metric in $containsWindowUnits.Keys) {
                        foreach ($variant in @('baseline','candidate')) {
                            if (!$containsKeys.ContainsKey("$variant/$pair/$caseName-$satellites/$metric")) { throw "Incomplete ContainsWindow matrix: $variant/$pair/$caseName-$satellites/$metric" }
                        }
                        if ($metric -cin @('calls','matches','equality-calls','hash-calls') -and
                            $containsKeys["baseline/$pair/$caseName-$satellites/$metric"] -ne $containsKeys["candidate/$pair/$caseName-$satellites/$metric"]) {
                            throw "Paired ContainsWindow semantics differ: $pair/$caseName-$satellites/$metric"
                        }
                    }
                    if ($containsKeys["candidate/$pair/$caseName-$satellites/allocated-bytes"] -ge $containsKeys["baseline/$pair/$caseName-$satellites/allocated-bytes"]) {
                        throw "ContainsWindow allocation did not improve: $pair/$caseName-$satellites"
                    }
                }
            }
        }
        foreach ($record in $candidateBuildRecords) {
            if ((Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $record.Path)).Hash -cne $record.SHA256) {
                throw "Frozen candidate changed during measurement: $($record.Path)"
            }
        }
        foreach ($record in $containsWindowBaselineBuildRecords) {
            if ((Get-FileHash -LiteralPath (Join-Path "$baselineRoot/source" $record.Path)).Hash -cne $record.SHA256) {
                throw "Frozen baseline changed during measurement: $($record.Path)"
            }
        }
        # Strict verification independently derives all 60 allocation pairs and
        # 240 semantic comparisons from the raw TRX. Timing remains descriptive.
    }
    if ($Scenario -eq 'FocusRejectedAdmission') {
        if ($rows.Count -ne 270) { throw 'FocusRejectedAdmission requires exactly 270 measurement rows' }
        $focusRejectedKeys = [Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
        $focusRejectedFrequencies = [Collections.Generic.HashSet[long]]::new()
        $focusRejectedSemanticMetrics = @('calls','rejected','native-calls','worker-starts','wait-calls','current-preserved')
        foreach ($row in $rows) {
            $case = [regex]::Match($row.scenario, '^Actual FocusHelper\.RequestSequence\.Enqueue; focus-rejected-(superseded|expired|stopped);1000 discarded warmups then100000 calls$')
            if (!$case.Success -or $row.metric -cnotin $focusRejectedUnits.Keys -or
                $row.variant -cnotin @('baseline','candidate') -or [string]$row.run -cnotmatch '^[1-5]$' -or
                [string]$row.pair -cne [string]$row.run -or $row.window_count -ne 0 -or $row.iterations -ne 100000 -or
                $row.perf_id -cne 'PERF-030' -or $row.unit -cne $focusRejectedUnits[$row.metric]) {
                throw 'FocusRejectedAdmission measurement dimensions differ'
            }
            $caseName = $case.Groups[1].Value
            $key = "$($row.variant)/$($row.run)/$caseName/$($row.metric)"
            $value = [long]::Parse([string]$row.value, [Globalization.CultureInfo]::InvariantCulture)
            if (!$focusRejectedKeys.TryAdd($key, $value)) { throw "Duplicate FocusRejectedAdmission row: $key" }
            $expected = switch -CaseSensitive ($row.metric) {
                { $_ -cin @('calls','rejected','current-preserved') } { 100000L }
                { $_ -cin @('native-calls','worker-starts','wait-calls') } { 0L }
                'allocated-bytes' { if ($value -lt 0) { throw "Negative allocation: $key" }; $null }
                { $_ -cin @('elapsed-ticks','timestamp-frequency') } {
                    if ($value -le 0) { throw "Nonpositive timing/frequency: $key" }
                    if ($_ -ceq 'timestamp-frequency') { [void]$focusRejectedFrequencies.Add($value) }
                    $null
                }
            }
            if ($null -ne $expected -and $value -ne $expected) { throw "FocusRejectedAdmission semantics differ: $key" }
        }
        if ($focusRejectedFrequencies.Count -ne 1) { throw 'FocusRejectedAdmission timestamp frequency differs' }
        foreach ($caseName in $focusRejectedCases) {
            foreach ($pair in 1..5) {
                foreach ($metric in $focusRejectedUnits.Keys) {
                    foreach ($variant in @('baseline','candidate')) {
                        if (!$focusRejectedKeys.ContainsKey("$variant/$pair/$caseName/$metric")) {
                            throw "Incomplete FocusRejectedAdmission matrix: $variant/$pair/$caseName/$metric"
                        }
                    }
                    if ($metric -cin $focusRejectedSemanticMetrics -and
                        $focusRejectedKeys["baseline/$pair/$caseName/$metric"] -ne $focusRejectedKeys["candidate/$pair/$caseName/$metric"]) {
                        throw "Paired FocusRejectedAdmission semantics differ: $pair/$caseName/$metric"
                    }
                }
                if ($focusRejectedKeys["candidate/$pair/$caseName/allocated-bytes"] -ge $focusRejectedKeys["baseline/$pair/$caseName/allocated-bytes"]) {
                    throw "FocusRejectedAdmission allocation did not improve: $pair/$caseName"
                }
            }
        }
        foreach ($record in $candidateBuildRecords) {
            if ((Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $record.Path)).Hash -cne $record.SHA256) {
                throw "Frozen candidate changed during measurement: $($record.Path)"
            }
        }
        foreach ($record in $focusRejectedBaselineBuildRecords) {
            if ((Get-FileHash -LiteralPath (Join-Path "$baselineRoot/source" $record.Path)).Hash -cne $record.SHA256) {
                throw "Frozen baseline changed during measurement: $($record.Path)"
            }
        }
        # Strict verification independently derives all 15 allocation pairs and
        # 90 semantic comparisons from the raw TRX. Timing remains descriptive.
    }
    $rows | Export-Csv -NoTypeInformation "$experimentRoot/measurements.csv"
    Write-Output "$Scenario; five isolated Release runs per variant; $($rows.Count) counter rows: $experimentRoot/measurements.csv"
}
finally {
    $env:DOTNET_TieredCompilation = $previousTiering
    $env:FWM_PERF_GENERIC_CASE = $previousGenericPreviewCase
}
