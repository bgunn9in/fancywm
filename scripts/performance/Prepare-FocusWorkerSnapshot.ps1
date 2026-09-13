param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId,
    [Parameter(Mandatory)][ValidateSet('Baseline', 'Candidate')][string]$Role
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Assert-RelativePath([string]$Path) {
    Assert-Condition (![string]::IsNullOrWhiteSpace($Path) -and ![IO.Path]::IsPathRooted($Path) -and
        !$Path.Contains('\') -and !$Path.Contains(':') -and $Path -cnotmatch '(^|/)(\.|\.\.|)(/|$)') "Invalid relative path: $Path"
}

function Assert-FrozenSource {
    Assert-Condition ((Get-Sha256 $manifestPath) -ceq $manifestHash -and
        (Get-Sha256 $snapshotPath) -ceq $snapshotHash) 'Frozen metadata changed.'
    $files = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Force)
    Assert-Condition ($files.Count -eq $manifest.Count) 'Frozen source file count differs.'
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName).Replace('\', '/')
        Assert-Condition ($sourceHashes.ContainsKey($relative) -and
            ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and
            (Get-Sha256 $file.FullName) -ceq $sourceHashes[$relative]) "Frozen source differs: $relative"
    }
}

function Assert-Guid([string]$Value, [string]$Label) {
    $parsed = [guid]::Empty
    Assert-Condition ([guid]::TryParseExact($Value, 'D', [ref]$parsed) -and $parsed -ne [guid]::Empty) "Invalid GUID: $Label"
}

function Test-WorkerRegressionTrx([string]$Path, [bool]$ExpectedRed, [string]$AssemblyPath) {
    $cases = [ordered]@{
        RepeatedCompletedWorkerAdmissionsPreserveSemantics = @('direct-success', 'alt-retry', 'attach-failure')
        RepeatedCompletedWorkerAdmissionsStayWithinCallerAllocationBudget = @('direct-success', 'alt-retry', 'attach-failure')
        CompletedWorkerStartDelegateDoesNotRootRequestSequence = @('')
        FocusWorkerAdmissionCounterScenario = @('')
    }
    $allocationMethod = 'RepeatedCompletedWorkerAdmissionsStayWithinCallerAllocationBudget'
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally { $reader.Dispose() }
    $root = $document.DocumentElement
    Assert-Condition ($root.LocalName -ceq 'TestRun' -and
        $root.NamespaceURI -ceq 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010') 'Invalid regression TRX root.'
    Assert-Guid $root.id 'TestRun.id'
    $summaries = @($root.SelectNodes('./*[local-name()="ResultSummary"]'))
    $times = @($root.SelectNodes('./*[local-name()="Times"]'))
    $summaryOutcome = if ($ExpectedRed) { 'Failed' } else { 'Completed' }
    Assert-Condition ($summaries.Count -eq 1 -and $summaries[0].outcome -ceq $summaryOutcome -and $times.Count -eq 1) 'Regression summary/times differ.'
    $start = [DateTimeOffset]::Parse($times[0].start, [Globalization.CultureInfo]::InvariantCulture)
    $finish = [DateTimeOffset]::Parse($times[0].finish, [Globalization.CultureInfo]::InvariantCulture)
    Assert-Condition ($start -lt $finish) 'Invalid regression interval.'
    $all = @($root.SelectNodes('.//*[local-name()="UnitTestResult"]'))
    $top = @($root.SelectNodes('./*[local-name()="Results"]/*[local-name()="UnitTestResult"]'))
    $leaves = @($all | Where-Object { $_.SelectNodes('./*[local-name()="InnerResults"]/*[local-name()="UnitTestResult"]').Count -eq 0 })
    $definitions = @($root.SelectNodes('./*[local-name()="TestDefinitions"]/*[local-name()="UnitTest"]'))
    $entries = @($root.SelectNodes('./*[local-name()="TestEntries"]/*[local-name()="TestEntry"]'))
    Assert-Condition ($all.Count -eq 10 -and $leaves.Count -eq 8 -and $top.Count -eq 4 -and
        $definitions.Count -eq 4 -and $entries.Count -eq 4) 'Regression raw/leaf/definition/entry counts differ.'
    $byId = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $methodNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($definition in $definitions) {
        Assert-Guid $definition.id 'definition.id'
        $methods = @($definition.SelectNodes('./*[local-name()="TestMethod"]'))
        $executions = @($definition.SelectNodes('./*[local-name()="Execution"]'))
        Assert-Condition ($methods.Count -eq 1 -and $executions.Count -eq 1) 'Regression definition shape differs.'
        $method = $methods[0]
        Assert-Guid $executions[0].id 'definition.executionId'
        Assert-Condition ($method.className -ceq 'FancyWM.Tests.Utilities.FocusHelperTest' -and
            $method.name -cin $cases.Keys -and $definition.name -ceq $method.name -and
            $method.adapterTypeName -ceq 'executor://mstestadapter/v2' -and
            $methodNames.Add($method.name) -and $byId.TryAdd($definition.id, $definition)) 'Unexpected/duplicate regression definition.'
        foreach ($actualPath in @($method.codeBase, $definition.storage)) {
            Assert-Condition (![string]::IsNullOrWhiteSpace($actualPath) -and [IO.Path]::IsPathRooted($actualPath) -and
                [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($actualPath), $AssemblyPath)) 'Regression assembly identity differs.'
        }
    }
    $entriesById = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($entry in $entries) {
        Assert-Guid $entry.testId 'entry.testId'
        Assert-Guid $entry.executionId 'entry.executionId'
        Assert-Guid $entry.testListId 'entry.testListId'
        Assert-Condition ($byId.ContainsKey($entry.testId) -and $entriesById.TryAdd($entry.testId, $entry)) 'Unexpected/duplicate regression entry.'
    }
    $seenTop = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($result in $top) {
        Assert-Condition ($byId.ContainsKey($result.testId) -and $seenTop.Add($result.testId)) 'Regression top-level identity differs.'
        $definition = $byId[$result.testId]
        $method = $definition.TestMethod.name
        $entry = $entriesById[$result.testId]
        Assert-Condition ($result.testName -ceq $method -and $definition.Execution.id -ceq $result.executionId -and
            $entry.executionId -ceq $result.executionId -and $entry.testListId -ceq $result.testListId -and
            !$result.HasAttribute('parentExecutionId')) 'Regression definition/result/entry relationship differs.'
        $children = @($result.SelectNodes('./*[local-name()="InnerResults"]/*[local-name()="UnitTestResult"]'))
        if ($cases[$method].Count -gt 1) {
            $expected = if ($ExpectedRed -and $method -ceq $allocationMethod) { 'Failed' } else { 'Passed' }
            Assert-Condition ($result.GetAttribute('resultType') -ceq 'DataDrivenTest' -and
                $result.outcome -ceq $expected -and $children.Count -eq 3) 'Regression DataTest aggregate differs.'
            foreach ($child in $children) {
                Assert-Condition ($child.GetAttribute('resultType') -ceq 'DataDrivenDataRow' -and
                    $child.testId -ceq $result.testId -and $child.parentExecutionId -ceq $result.executionId -and
                    $child.testListId -ceq $result.testListId -and $child.SelectNodes('./*[local-name()="InnerResults"]').Count -eq 0) 'Regression DataRow relationship differs.'
            }
        }
        else {
            Assert-Condition ($children.Count -eq 0 -and !$result.HasAttribute('resultType') -and
                $result.SelectNodes('./*[local-name()="InnerResults"]').Count -eq 0) 'Unexpected regression aggregate.'
        }
    }
    $executionIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($result in $all) {
        Assert-Guid $result.executionId 'result.executionId'
        Assert-Guid $result.testId 'result.testId'
        Assert-Guid $result.testListId 'result.testListId'
        Assert-Condition ($executionIds.Add($result.executionId) -and $result.relativeResultsDirectory -ceq $result.executionId) 'Regression execution identity differs.'
        $testStart = [DateTimeOffset]::Parse($result.startTime, [Globalization.CultureInfo]::InvariantCulture)
        $testFinish = [DateTimeOffset]::Parse($result.endTime, [Globalization.CultureInfo]::InvariantCulture)
        Assert-Condition ($start -le $testStart -and $testStart -le $testFinish -and $testFinish -le $finish) 'Regression result escapes TestRun interval.'
    }
    $expectedKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($method in $cases.Keys) {
        foreach ($case in $cases[$method]) {
            $name = if ($case -ceq '') { $method } else { "$method ($case)" }
            [void]$expectedKeys.Add("$method|$name")
        }
    }
    $seenKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $failures = [Collections.Generic.List[object]]::new()
    foreach ($leaf in $leaves) {
        Assert-Condition ($byId.ContainsKey($leaf.testId)) 'Regression leaf has no definition.'
        $method = $byId[$leaf.testId].TestMethod.name
        $key = "$method|$($leaf.testName)"
        Assert-Condition ($expectedKeys.Contains($key) -and $seenKeys.Add($key)) "Unexpected/duplicate regression leaf: $key"
        $mustFail = $ExpectedRed -and $method -ceq $allocationMethod
        $outcome = if ($mustFail) { 'Failed' } else { 'Passed' }
        Assert-Condition ($leaf.outcome -ceq $outcome) "Regression outcome differs: $key"
        if ($mustFail) {
            $messages = @($leaf.SelectNodes('./*[local-name()="Output"]/*[local-name()="ErrorInfo"]/*[local-name()="Message"]'))
            Assert-Condition ($messages.Count -eq 1) 'Allocation failure message missing/duplicated.'
            $case = [regex]::Match($leaf.testName, '\((direct-success|alt-retry|attach-failure)\)$').Groups[1].Value
            $pattern = '^Assert\.IsTrue failed\. Repeated admitted worker construction exceeds its caller allocation budget: mode=' +
                [regex]::Escape($case) + ', total=(?<bytes>[1-9][0-9]*) B, per-call=(?<perCall>[0-9]+[.,][0-9]{3}) B, budget=336 B/call\.$'
            $match = [regex]::Match($messages[0].InnerText.TrimEnd(), $pattern)
            Assert-Condition ($match.Success) 'Baseline failed for a reason other than worker caller allocation.'
            $bytes = [long]::Parse($match.Groups['bytes'].Value, [Globalization.CultureInfo]::InvariantCulture)
            $perCall = ([double]$bytes / 128).ToString('F3', [Globalization.CultureInfo]::InvariantCulture)
            Assert-Condition ($bytes -gt 336L * 128 -and $match.Groups['perCall'].Value.Replace(',', '.') -ceq $perCall) 'Allocation failure values differ.'
            $failures.Add([pscustomobject]@{ Case = $case; AllocatedBytes = $bytes; BytesPerAdmission = $perCall })
        }
        else { Assert-Condition ($leaf.SelectNodes('./*[local-name()="Output"]/*[local-name()="ErrorInfo"]').Count -eq 0) 'Passing regression contains an error.' }
    }
    Assert-Condition ($seenKeys.SetEquals([string[]]@($expectedKeys))) 'Exact regression leaf set differs.'
    $expectedFailures = if ($ExpectedRed) { 3 } else { 0 }
    $rawFailures = if ($ExpectedRed) { 4 } else { 0 }
    Assert-Condition ($failures.Count -eq $expectedFailures -and
        @($all | Where-Object outcome -CNE 'Passed').Count -eq $rawFailures -and
        $root.SelectNodes('.//*[local-name()="ErrorInfo"]').Count -eq $expectedFailures) 'Regression failure count differs.'
    $counters = @($root.SelectNodes('.//*[local-name()="Counters"]'))
    Assert-Condition ($counters.Count -eq 1 -and $summaries[0].SelectNodes('./*[local-name()="Counters"]').Count -eq 1) 'Regression counters missing/duplicated.'
    foreach ($name in @('total','executed','passed','failed','error','timeout','aborted','inconclusive','passedButRunAborted','notRunnable','notExecuted','disconnected','warning','completed','inProgress','pending')) {
        $expected = switch ($name) {
            { $_ -cin @('total','executed') } { 10 }
            'passed' { 10 - $rawFailures }
            'failed' { $rawFailures }
            default { 0 }
        }
        Assert-Condition ($counters[0].GetAttribute($name) -ceq [string]$expected) "Regression TRX counter differs: $name"
    }
    [pscustomobject]@{
        Leaves = 8; RawResults = 10; Passed = 8 - $expectedFailures; Failed = $expectedFailures
        Methods = @($cases.Keys); LeafNames = @($seenKeys | Sort-Object); AllocationFailures = @($failures)
        RunId = $root.id; Start = $start.ToString('o'); Finish = $finish.ToString('o'); Assembly = $AssemblyPath
    }
}

$rootOutput = @(& git -C $PSScriptRoot rev-parse --show-toplevel)
Assert-Condition ($LASTEXITCODE -eq 0) 'Cannot resolve repository root.'
$repositoryRoot = [IO.Path]::GetFullPath(($rootOutput -join '').Trim())
Assert-Condition ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath((Get-Location).Path), $repositoryRoot)) 'Run from the repository root.'
Assert-Condition ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [Runtime.InteropServices.Architecture]::X64) 'Use x64 PowerShell and dotnet.'
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$sourceRoot = Join-Path $snapshotRoot 'source'
$snapshotPath = Join-Path $snapshotRoot 'snapshot.json'
$manifestPath = Join-Path $snapshotRoot 'manifest.csv'
$snapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
$snapshotHash = Get-Sha256 $snapshotPath
$manifestHash = Get-Sha256 $manifestPath
Assert-Condition ($snapshot.SnapshotId -ceq $SnapshotId -and $snapshot.ManifestSHA256 -ceq $manifestHash) 'Snapshot identity/manifest hash differs.'
$head = (@(& git -C $repositoryRoot rev-parse HEAD) -join '').Trim()
Assert-Condition ($LASTEXITCODE -eq 0 -and $head -ceq $snapshot.Commit) 'Snapshot/current HEAD differs.'
$tree = (@(& git -C $repositoryRoot rev-parse 'HEAD^{tree}') -join '').Trim()
Assert-Condition ($LASTEXITCODE -eq 0 -and $tree -ceq $snapshot.Tree) 'Snapshot/current tree differs.'
$submodules = @(& git -C $repositoryRoot submodule status --recursive)
Assert-Condition ($LASTEXITCODE -eq 0 -and @($submodules | Where-Object { $_ -cmatch '^[+-U]' }).Count -eq 0 -and
    ($submodules -join '|') -ceq ($snapshot.Submodules -join '|')) 'Snapshot/current submodules differ.'
$manifest = @(Import-Csv -LiteralPath $manifestPath)
$sourceHashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
$caseInsensitivePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($record in $manifest) {
    Assert-RelativePath $record.Path
    Assert-Condition (($record.PSObject.Properties.Name -join '|') -ceq 'Path|SHA256' -and
        $record.SHA256 -cmatch '^[0-9A-F]{64}$' -and $caseInsensitivePaths.Add($record.Path) -and
        $sourceHashes.TryAdd($record.Path, $record.SHA256)) 'Malformed/duplicate source manifest row.'
}
Assert-FrozenSource
$productionSource = 'FancyWM/Utilities/FocusHelper.cs'
$fixtureSource = 'FancyWM.Tests/Utilities/FocusHelperTest.WorkerAdmission.cs'
$prepareSource = 'scripts/performance/Prepare-FocusWorkerSnapshot.ps1'
$runnerSource = 'scripts/performance/Measure-FocusWorkerAdmission.ps1'
foreach ($required in @($productionSource, $fixtureSource, $prepareSource, $runnerSource)) {
    Assert-Condition ($sourceHashes.ContainsKey($required)) "Required frozen input missing: $required"
}
Assert-Condition ((Get-Sha256 $PSCommandPath) -ceq $sourceHashes[$prepareSource]) 'Executing preparation script differs from frozen input.'
$production = Get-Content -LiteralPath (Join-Path $sourceRoot $productionSource) -Raw
if ($Role -ceq 'Baseline') {
    Assert-Condition ([regex]::Matches($production, 'new Thread\(RunWorker\)').Count -eq 1 -and
        !$production.Contains('m_workerStart')) 'Baseline must retain uncached worker-start delegate conversion.'
}
else {
    Assert-Condition ([regex]::Matches($production, 'private ThreadStart\? m_workerStart;').Count -eq 1 -and
        [regex]::Matches($production, 'new Thread\(m_workerStart \?\?= RunWorker\)').Count -eq 1 -and
        !$production.Contains('new Thread(RunWorker)')) 'Candidate worker-start delegate cache shape differs.'
}
$roleName = $Role.ToLowerInvariant()
$buildRoot = Join-Path $snapshotRoot 'build-workspace'
$archiveRoot = Join-Path $snapshotRoot 'release-test-binaries'
$archiveManifestPath = Join-Path $snapshotRoot 'release-test-binaries.csv'
$provenancePath = Join-Path $snapshotRoot 'release-test-binaries.provenance.json'
$validationRoot = Join-Path $snapshotRoot 'validation'
$restoreLog = Join-Path $validationRoot "$roleName-release-restore.log"
$buildLog = Join-Path $validationRoot "$roleName-release-build.log"
$testLog = Join-Path $validationRoot "$roleName-focus-worker-regression.log"
$trxName = "$roleName-focus-worker-regression.trx"
$testTrx = Join-Path $validationRoot $trxName
$commandPath = Join-Path $validationRoot "$roleName-prepare-commands.json"
$environmentPath = Join-Path $validationRoot "$roleName-dotnet-info.log"
foreach ($output in @($buildRoot, $archiveRoot, $archiveManifestPath, $provenancePath, $restoreLog, $buildLog, $testLog, $testTrx, $commandPath, $environmentPath)) {
    Assert-Condition (!(Test-Path -LiteralPath $output)) "Immutable preparation output already exists: $output"
}
New-Item -ItemType Directory -Path $buildRoot | Out-Null
New-Item -ItemType Directory -Force -Path $validationRoot | Out-Null
foreach ($record in $manifest) {
    $destination = Join-Path $buildRoot $record.Path
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot $record.Path) -Destination $destination
    Assert-Condition ((Get-Sha256 $destination) -ceq $record.SHA256) "Isolated source copy differs: $($record.Path)"
}
$project = Join-Path $buildRoot 'FancyWM.Tests/FancyWM.Tests.csproj'
$framework = 'net10.0-windows10.0.18362.0'
$binaryRoot = Join-Path $buildRoot "FancyWM.Tests/bin/Release/$framework"
$testAssembly = [IO.Path]::GetFullPath((Join-Path $binaryRoot 'FancyWM.Tests.dll'))
$methods = @('RepeatedCompletedWorkerAdmissionsPreserveSemantics', 'RepeatedCompletedWorkerAdmissionsStayWithinCallerAllocationBudget', 'CompletedWorkerStartDelegateDoesNotRootRequestSequence', 'FocusWorkerAdmissionCounterScenario')
$filter = ($methods | ForEach-Object { "FullyQualifiedName=FancyWM.Tests.Utilities.FocusHelperTest.$_" }) -join '|'
$restoreArguments = @('restore', $project, '--disable-parallel')
$buildArguments = @('build', $project, '--configuration', 'Release', '--no-restore', '--no-incremental')
$testArguments = @('vstest', $testAssembly, '/Platform:x64', "/TestCaseFilter:$filter", "/Logger:trx;LogFileName=$trxName", "/ResultsDirectory:$validationRoot")
[ordered]@{ Executable = 'dotnet'; WorkingDirectory = $buildRoot; Restore = $restoreArguments; Build = $buildArguments; Test = $testArguments; TestEnvironment = @{ DOTNET_TieredCompilation = '0' } } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $commandPath
$dotnetInfo = & dotnet --info 2>&1
$dotnetInfoExit = $LASTEXITCODE
$dotnetInfo | Out-File -LiteralPath $environmentPath -Encoding utf8
Assert-Condition ($dotnetInfoExit -eq 0) 'dotnet --info failed.'
$started = [DateTimeOffset]::UtcNow
Push-Location -LiteralPath $buildRoot
try {
    & dotnet @restoreArguments *> $restoreLog
    Assert-Condition ($LASTEXITCODE -eq 0) "Isolated Release restore failed: $restoreLog"
    & dotnet @buildArguments *> $buildLog
    Assert-Condition ($LASTEXITCODE -eq 0) "Isolated Release build failed: $buildLog"
    $previousTiering = $env:DOTNET_TieredCompilation
    try {
        $env:DOTNET_TieredCompilation = '0'
        & dotnet @testArguments *> $testLog
        $testExit = $LASTEXITCODE
    }
    finally { $env:DOTNET_TieredCompilation = $previousTiering }
}
finally { Pop-Location }
$expectedExit = if ($Role -ceq 'Baseline') { 1 } else { 0 }
Assert-Condition ($testExit -eq $expectedExit) "Worker regression exit differs: expected $expectedExit, got $testExit"
$regression = Test-WorkerRegressionTrx $testTrx ($Role -ceq 'Baseline') $testAssembly
Assert-FrozenSource
foreach ($record in $manifest) {
    Assert-Condition ((Get-Sha256 (Join-Path $buildRoot $record.Path)) -ceq $record.SHA256) "Isolated source changed during build: $($record.Path)"
}
New-Item -ItemType Directory -Path $archiveRoot | Out-Null
$archiveManifest = @(foreach ($file in @(Get-ChildItem -LiteralPath $binaryRoot -Recurse -File -Force | Sort-Object FullName)) {
    $relative = [IO.Path]::GetRelativePath($binaryRoot, $file.FullName).Replace('\', '/')
    $destination = Join-Path $archiveRoot $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    $hash = Get-Sha256 $file.FullName
    Copy-Item -LiteralPath $file.FullName -Destination $destination
    Assert-Condition ((Get-Sha256 $destination) -ceq $hash) "Archived binary differs: $relative"
    [pscustomobject]@{ Path = $relative; Hash = $hash; Length = $file.Length }
})
Assert-Condition ($archiveManifest.Count -gt 0 -and
    @($archiveManifest | Where-Object Path -CEQ 'FancyWM.dll').Count -eq 1 -and
    @($archiveManifest | Where-Object Path -CEQ 'FancyWM.Tests.dll').Count -eq 1) 'Required Release binaries missing.'
$archiveManifest | Export-Csv -NoTypeInformation -LiteralPath $archiveManifestPath
$provenance = [ordered]@{
    Snapshot = $SnapshotId; Role = $Role; SnapshotSHA256 = $snapshotHash; SnapshotManifestSHA256 = $manifestHash
    ArchiveManifestSHA256 = Get-Sha256 $archiveManifestPath
    BuildSucceeded = $true; Configuration = 'Release'; TargetFramework = $framework; Architecture = 'x64 vstest process; AnyCPU managed build'
    Scope = 'PERF-030 RequestSequence.Enqueue cached worker-start delegate; caller allocation only'
    BuildWorkspace = 'build-workspace'; SourceCopyVerified = $true; FrozenSourceVerifiedAfterBuild = $true
    FancyWMDllSHA256 = @($archiveManifest | Where-Object Path -CEQ 'FancyWM.dll')[0].Hash
    FancyWMTestsDllSHA256 = @($archiveManifest | Where-Object Path -CEQ 'FancyWM.Tests.dll')[0].Hash
    ProductionSource = $productionSource; ProductionSourceSHA256 = $sourceHashes[$productionSource]
    FixtureSource = $fixtureSource; FixtureSourceSHA256 = $sourceHashes[$fixtureSource]
    PrepareSource = $prepareSource; PrepareSourceSHA256 = $sourceHashes[$prepareSource]
    RunnerSource = $runnerSource; RunnerSourceSHA256 = $sourceHashes[$runnerSource]
    Commands = "validation/$roleName-prepare-commands.json"; CommandsSHA256 = Get-Sha256 $commandPath
    Environment = "validation/$roleName-dotnet-info.log"; EnvironmentSHA256 = Get-Sha256 $environmentPath
    RestoreLog = "validation/$roleName-release-restore.log"; RestoreLogSHA256 = Get-Sha256 $restoreLog
    BuildLog = "validation/$roleName-release-build.log"; BuildLogSHA256 = Get-Sha256 $buildLog
    RegressionFilter = $filter; RegressionExitCode = $testExit
    RegressionTrx = "validation/$trxName"; RegressionTrxSHA256 = Get-Sha256 $testTrx
    RegressionLog = "validation/$roleName-focus-worker-regression.log"; RegressionLogSHA256 = Get-Sha256 $testLog
    Regression = $regression; Started = $started.ToString('o'); Finished = [DateTimeOffset]::UtcNow.ToString('o')
    CopiedFileCount = $archiveManifest.Count; CopiedBytes = [long](($archiveManifest | Measure-Object Length -Sum).Sum)
}
$provenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $provenancePath
$provenance | ConvertTo-Json -Depth 8
