param([Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId)

$ErrorActionPreference = 'Stop'

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}


function Test-ContainsWindowRegressionTrx([string]$Path, [bool]$ExpectedRed, [string]$ExpectedAssemblyPath = '') {
    $expectedCases = [ordered]@{
        'ContainsWindowChecksMasterFirstAndShortCircuitsWithResidentFirstEquality' = @('')
        'ContainsWindowChecksSatellitesInOrderAndStopsAtFirstMatch' = @('0','1','2','-1')
        'ContainsWindowHandlesMissingMasterAndEmptySatellites' = @('False','True')
        'ContainsWindowPropagatesExactEqualityExceptionWithoutLaterCallbacks' = @('0','1','3')
        'ContainsWindowSeesMasterCallbackMutationBeforeSatelliteEnumeration' = @('')
        'ContainsWindowMutationThenFalseThrowsOnNextMoveNext' = @('clear','replace')
        'ContainsWindowMutationThenTrueShortCircuitsBeforeNextMoveNext' = @('clear','replace')
        'ContainsWindowPreservesRuntimeStateAndReadOnlyViewIdentity' = @('False','True')
        'ContainsWindowAvoidsCapturedPredicateAllocation' = @('')
        'ContainsWindowCounterScenario' = @('')
    }
    $expectedClass = 'FancyWM.Tests.AlgorithmicLayouts.MasterSatelliteContainsWindowTest'
    $regressionName = 'ContainsWindowAvoidsCapturedPredicateAllocation'
    $expectedLeafKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($methodName in $expectedCases.Keys) {
        foreach ($label in $expectedCases[$methodName]) {
            $testName = if ($label -ceq '') { $methodName } else { "$methodName ($label)" }
            Assert-Condition ($expectedLeafKeys.Add("$methodName|$testName")) 'Duplicate regression contract key.'
        }
    }
    function Assert-RegressionGuid([string]$Value, [string]$Label) {
        $parsed = [guid]::Empty
        Assert-Condition ([guid]::TryParseExact($Value, 'D', [ref]$parsed) -and $parsed -ne [guid]::Empty) "Invalid regression GUID: $Label"
    }
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
    Assert-Condition ($root.LocalName -ceq 'TestRun' -and $root.NamespaceURI -ceq 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010') "Invalid regression TRX: $Path"
    Assert-RegressionGuid $root.id 'TestRun.id'
    $summaries = @($root.SelectNodes('./*[local-name()="ResultSummary"]'))
    $times = @($root.SelectNodes('./*[local-name()="Times"]'))
    $expectedSummary = if ($ExpectedRed) { 'Failed' } else { 'Completed' }
    Assert-Condition ($summaries.Count -eq 1 -and $summaries[0].outcome -ceq $expectedSummary -and $times.Count -eq 1) "Regression TestRun summary/times differ: $Path"
    $start = [DateTimeOffset]::Parse($times[0].start, [Globalization.CultureInfo]::InvariantCulture)
    $finish = [DateTimeOffset]::Parse($times[0].finish, [Globalization.CultureInfo]::InvariantCulture)
    Assert-Condition ($start -lt $finish) "Invalid regression process interval: $Path"
    $all = @($document.SelectNodes('//*[local-name()="UnitTestResult"]'))
    $topResults = @($root.SelectNodes('./*[local-name()="Results"]/*[local-name()="UnitTestResult"]'))
    $leaves = @($all | Where-Object { $_.SelectNodes('./*[local-name()="InnerResults"]/*[local-name()="UnitTestResult"]').Count -eq 0 })
    $definitions = @($root.SelectNodes('./*[local-name()="TestDefinitions"]/*[local-name()="UnitTest"]'))
    $entries = @($root.SelectNodes('./*[local-name()="TestEntries"]/*[local-name()="TestEntry"]'))
    Assert-Condition ($all.Count -eq 25 -and $leaves.Count -eq 19 -and $topResults.Count -eq 10 -and $definitions.Count -eq 10 -and $entries.Count -eq 10) "Regression raw/leaf/definition/entry dimensions differ: $Path"
    $methodsById = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $definitionsById = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $distinctMethods = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $assembly = $ExpectedAssemblyPath
    foreach ($definition in $definitions) {
        $methods = @($definition.SelectNodes('./*[local-name()="TestMethod"]'))
        $executions = @($definition.SelectNodes('./*[local-name()="Execution"]'))
        Assert-RegressionGuid $definition.id 'definition.testId'
        Assert-Condition ($methods.Count -eq 1 -and $executions.Count -eq 1) 'Regression definition method/execution dimensions differ.'
        $method = $methods[0]
        Assert-RegressionGuid $executions[0].id 'definition.executionId'
        Assert-Condition ($method.className -ceq $expectedClass -and $method.name -cin $expectedCases.Keys -and
            $definition.name -ceq $method.name -and $method.adapterTypeName -ceq 'executor://mstestadapter/v2' -and
            $distinctMethods.Add($method.name) -and $methodsById.TryAdd($definition.id, $method.name) -and $definitionsById.TryAdd($definition.id, $definition)) "Unexpected/duplicate regression definition: $Path"
        foreach ($assemblyPath in @($method.codeBase, $definition.storage)) {
            Assert-Condition (![string]::IsNullOrWhiteSpace($assemblyPath) -and [IO.Path]::IsPathRooted($assemblyPath)) 'Regression assembly path must be absolute.'
            if (!$assembly) { $assembly = [IO.Path]::GetFullPath($assemblyPath) }
            Assert-Condition ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($assemblyPath), [IO.Path]::GetFullPath($assembly))) "Regression assembly identity differs: $assemblyPath"
        }
    }
    $entriesById = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($entry in $entries) {
        Assert-RegressionGuid $entry.testId 'entry.testId'
        Assert-RegressionGuid $entry.executionId 'entry.executionId'
        Assert-RegressionGuid $entry.testListId 'entry.testListId'
        Assert-Condition ($entriesById.TryAdd($entry.testId, $entry) -and $methodsById.ContainsKey($entry.testId)) 'Regression test entry identity differs.'
    }
    $seenTopIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($result in $topResults) {
        Assert-Condition ($methodsById.ContainsKey($result.testId) -and $seenTopIds.Add($result.testId) -and $entriesById.ContainsKey($result.testId)) 'Regression top-level test identity differs.'
        $methodName = $methodsById[$result.testId]
        $definition = $definitionsById[$result.testId]
        $entry = $entriesById[$result.testId]
        Assert-Condition ($result.testName -ceq $methodName -and $definition.Execution.id -ceq $result.executionId -and
            $entry.executionId -ceq $result.executionId -and $entry.testListId -ceq $result.testListId -and
            !$result.HasAttribute('parentExecutionId')) "Regression definition/result/entry relationship differs: $methodName"
        $children = @($result.SelectNodes('./*[local-name()="InnerResults"]/*[local-name()="UnitTestResult"]'))
        $dataDriven = $expectedCases[$methodName].Count -gt 1
        if ($dataDriven) {
            Assert-Condition ($result.GetAttribute('resultType') -ceq 'DataDrivenTest' -and $result.outcome -ceq 'Passed' -and
                $children.Count -eq $expectedCases[$methodName].Count) "Regression aggregate shape differs: $methodName"
            foreach ($child in $children) {
                Assert-Condition ($child.GetAttribute('resultType') -ceq 'DataDrivenDataRow' -and
                    $child.testId -ceq $result.testId -and $child.parentExecutionId -ceq $result.executionId -and
                    $child.testListId -ceq $result.testListId -and $child.SelectNodes('./*[local-name()="InnerResults"]').Count -eq 0) "Regression DataRow relationship differs: $methodName"
            }
        }
        else {
            Assert-Condition ($children.Count -eq 0 -and !$result.HasAttribute('resultType') -and
                $result.SelectNodes('./*[local-name()="InnerResults"]').Count -eq 0) "Unexpected regression aggregate: $methodName"
        }
    }
    $seenExecutions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($result in $all) {
        Assert-RegressionGuid $result.executionId 'result.executionId'
        Assert-RegressionGuid $result.testId 'result.testId'
        Assert-RegressionGuid $result.testListId 'result.testListId'
        Assert-Condition ($seenExecutions.Add($result.executionId) -and $result.relativeResultsDirectory -ceq $result.executionId) 'Regression result execution identity differs.'
        $testStart = [DateTimeOffset]::Parse($result.startTime, [Globalization.CultureInfo]::InvariantCulture)
        $testFinish = [DateTimeOffset]::Parse($result.endTime, [Globalization.CultureInfo]::InvariantCulture)
        Assert-Condition ($start -le $testStart -and $testStart -le $testFinish -and $testFinish -le $finish) 'Regression result interval escapes TestRun.'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $failures = 0
    foreach ($leaf in $leaves) {
        Assert-Condition ($methodsById.ContainsKey($leaf.testId)) "Regression leaf has no definition: $($leaf.testName)"
        $methodName = $methodsById[$leaf.testId]
        $key = "$methodName|$($leaf.testName)"
        Assert-Condition ($expectedLeafKeys.Contains($key) -and $seen.Add($key)) "Unexpected/duplicate regression DataRow: $key"
        $mustFail = $ExpectedRed -and $methodName -ceq $regressionName
        $expectedOutcome = if ($mustFail) { 'Failed' } else { 'Passed' }
        Assert-Condition ($leaf.outcome -ceq $expectedOutcome) "Unexpected regression outcome: $($leaf.testName)"
        if ($mustFail) {
            $messages = @($leaf.SelectNodes('./*[local-name()="Output"]/*[local-name()="ErrorInfo"]/*[local-name()="Message"]'))
            Assert-Condition ($messages.Count -eq 1 -and $messages[0].InnerText.StartsWith('Assert.IsTrue failed. Captured predicate allocation remains:', [StringComparison]::Ordinal)) 'Baseline failed for a reason other than captured predicate allocation.'
            $failures++
        }
        else { Assert-Condition ($leaf.SelectNodes('./*[local-name()="Output"]/*[local-name()="ErrorInfo"]').Count -eq 0) 'Unexpected passing regression error.' }
    }
    Assert-Condition ($seen.SetEquals($expectedLeafKeys)) 'Regression exact method|DataRow set differs.'
    $expectedFailures = if ($ExpectedRed) { 1 } else { 0 }
    Assert-Condition ($failures -eq $expectedFailures -and @($all | Where-Object outcome -CNE 'Passed').Count -eq $expectedFailures -and
        @($document.SelectNodes('//*[local-name()="ErrorInfo"]')).Count -eq $expectedFailures) "Regression aggregate outcomes differ: $Path"
    $counters = @($document.SelectNodes('//*[local-name()="Counters"]'))
    Assert-Condition ($counters.Count -eq 1 -and $summaries[0].SelectNodes('./*[local-name()="Counters"]').Count -eq 1) 'Regression counters missing/duplicated.'
    foreach ($name in @('total','executed','passed','failed','error','timeout','aborted','inconclusive','passedButRunAborted','notRunnable','notExecuted','disconnected','warning','completed','inProgress','pending')) {
        $expected = switch ($name) {
            { $_ -cin @('total','executed') } { 25 }
            'passed' { 25 - $expectedFailures }
            'failed' { $expectedFailures }
            default { 0 }
        }
        Assert-Condition ($counters[0].GetAttribute($name) -ceq [string]$expected) "Regression TRX counter differs: $name"
    }
    [pscustomobject]@{ Leaves = $leaves.Count; RawResults = $all.Count; Passed = $leaves.Count - $failures; Failed = $failures; LeafNames = @($seen | Sort-Object); Methods = @($expectedCases.Keys); RunId = $root.id; Start = $start; Finish = $finish; Assembly = $assembly }
}

$repositoryRoot = (git rev-parse --show-toplevel).Trim()
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$snapshotPath = Join-Path $snapshotRoot 'snapshot.json'
$manifestPath = Join-Path $snapshotRoot 'manifest.csv'
Assert-Condition (Test-Path -LiteralPath $snapshotPath -PathType Leaf) `
    "Snapshot metadata missing: $SnapshotId"
Assert-Condition (Test-Path -LiteralPath $manifestPath -PathType Leaf) `
    "Snapshot manifest missing: $SnapshotId"

$snapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
$manifestHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash
Assert-Condition ($snapshot.SnapshotId -ceq $SnapshotId) 'Snapshot ID differs'
Assert-Condition ($snapshot.ManifestSHA256 -ceq $manifestHash) 'Snapshot manifest hash differs'

$manifest = @(Import-Csv -LiteralPath $manifestPath)
$sourcePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($record in $manifest) {
    Assert-Condition (($record.PSObject.Properties.Name -join '|') -ceq 'Path|SHA256' -and $record.SHA256 -cmatch '^[0-9A-F]{64}$' -and $sourcePaths.Add($record.Path) -and ![IO.Path]::IsPathRooted($record.Path) -and !$record.Path.Contains('\') -and !$record.Path.Contains(':') -and $record.Path -cnotmatch '(^|/)(\.|\.\.|)(/|$)') "Malformed/duplicate source row: $($record.Path)"
    Assert-Condition ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path "$snapshotRoot/source" $record.Path)).Hash -ceq $record.SHA256) "Frozen source differs: $($record.Path)"
}
$buildInputs = @($manifest | Where-Object {
    $_.Path -match '\.(cs|csproj|xaml|props|targets|sln|slnx|ps1)$' -or
    $_.Path -match '(^|/)(global\.json|NuGet\.config)$' -or $_.Path -ceq 'version.json'
})
foreach ($record in $buildInputs) {
    $currentPath = Join-Path $repositoryRoot $record.Path
    $frozenPath = Join-Path (Join-Path $snapshotRoot 'source') $record.Path
    Assert-Condition (Test-Path -LiteralPath $currentPath -PathType Leaf) `
        "Current build input missing: $($record.Path)"
    Assert-Condition (Test-Path -LiteralPath $frozenPath -PathType Leaf) `
        "Frozen build input missing: $($record.Path)"
    Assert-Condition ((Get-FileHash -Algorithm SHA256 -LiteralPath $currentPath).Hash -ceq $record.SHA256) `
        "Current build input differs from snapshot: $($record.Path)"
    Assert-Condition ((Get-FileHash -Algorithm SHA256 -LiteralPath $frozenPath).Hash -ceq $record.SHA256) `
        "Frozen build input differs from manifest: $($record.Path)"
}

$productionSource = 'FancyWM/AlgorithmicLayouts/MasterSatelliteLayoutEngine.cs'
$regressionSource = 'FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteContainsWindowTest.cs'
$fixtureSource = 'FancyWM/AlgorithmicLayouts/MasterSatelliteRuntimeState.cs'
$runnerSource = 'scripts/performance/Measure-MicaRefresh.ps1'
$archiveScriptSource = 'scripts/performance/Archive-ContainsWindowBaseline.ps1'
$verifierSource = 'scripts/performance/Verify-ContainsWindowScan.ps1'
$appendScriptSource = 'scripts/performance/Append-ContainsWindowMeasurements.ps1'
foreach ($requiredSource in @($productionSource, $regressionSource, $fixtureSource, $runnerSource, $archiveScriptSource, $verifierSource, $appendScriptSource)) {
    Assert-Condition ($requiredSource -cin $manifest.Path) "Required source missing: $requiredSource"
}
$productionText = Get-Content -LiteralPath (Join-Path $repositoryRoot $productionSource) -Raw
$oldMethod = @'
        private static bool ContainsWindow(MasterSatelliteRuntimeState state, IWindow window)
        {
            return WindowEquals(state.Master, window)
                || state.Satellites.Any(satellite => WindowEquals(satellite, window));
        }
'@
Assert-Condition ($productionText.Replace([string][char]13, '').Contains($oldMethod)) 'Baseline must contain the original ContainsWindow captured predicate.'
foreach ($relativeOutput in @('release-test-binaries','release-test-binaries.csv','release-test-binaries.provenance.json','validation/baseline-release-build.log','validation/baseline-contains-window-red.trx','validation/baseline-contains-window-red.log')) {
    Assert-Condition (!(Test-Path -LiteralPath (Join-Path $snapshotRoot $relativeOutput))) "Immutable output already exists: $relativeOutput"
}

$validationRoot = Join-Path $snapshotRoot 'validation'
New-Item -ItemType Directory -Force -Path $validationRoot | Out-Null
$buildLog = Join-Path $validationRoot 'baseline-release-build.log'
$buildOutput = & dotnet build FancyWM.Tests/FancyWM.Tests.csproj `
    --configuration Release --no-restore --no-incremental 2>&1
$buildExitCode = $LASTEXITCODE
$buildOutput | Out-File -LiteralPath $buildLog -Encoding utf8
Assert-Condition ($buildExitCode -eq 0) "Baseline Release build failed: $buildExitCode"

$testClass = 'FancyWM.Tests.AlgorithmicLayouts.MasterSatelliteContainsWindowTest'
$redMethods = @('ContainsWindowChecksMasterFirstAndShortCircuitsWithResidentFirstEquality','ContainsWindowChecksSatellitesInOrderAndStopsAtFirstMatch','ContainsWindowHandlesMissingMasterAndEmptySatellites','ContainsWindowPropagatesExactEqualityExceptionWithoutLaterCallbacks','ContainsWindowSeesMasterCallbackMutationBeforeSatelliteEnumeration','ContainsWindowMutationThenFalseThrowsOnNextMoveNext','ContainsWindowMutationThenTrueShortCircuitsBeforeNextMoveNext','ContainsWindowPreservesRuntimeStateAndReadOnlyViewIdentity','ContainsWindowAvoidsCapturedPredicateAllocation','ContainsWindowCounterScenario')
$filter = ($redMethods | ForEach-Object { "FullyQualifiedName=$testClass.$_" }) -join '|'
$redTrxName = 'baseline-contains-window-red.trx'
$redTrx = Join-Path $validationRoot $redTrxName
$redLog = Join-Path $validationRoot 'baseline-contains-window-red.log'
$previousTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    $testOutput = & dotnet test FancyWM.Tests/FancyWM.Tests.csproj `
        --configuration Release --no-build --no-restore `
        --filter $filter `
        --logger "trx;LogFileName=$redTrxName" `
        --results-directory $validationRoot 2>&1
    $redExitCode = $LASTEXITCODE
    $testOutput | Out-File -LiteralPath $redLog -Encoding utf8
}
finally {
    $env:DOTNET_TieredCompilation = $previousTiering
}
Assert-Condition ($redExitCode -eq 1) "Baseline captured predicate allocation regression did not fail exactly as expected: $redExitCode"
Assert-Condition (Test-Path -LiteralPath $redTrx -PathType Leaf) 'Baseline red TRX missing'

$redResult = Test-ContainsWindowRegressionTrx $redTrx $true
foreach ($record in $buildInputs) {
    Assert-Condition ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $repositoryRoot $record.Path)).Hash -ceq $record.SHA256 -and (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path "$snapshotRoot/source" $record.Path)).Hash -ceq $record.SHA256) "Current/frozen build input changed: $($record.Path)"
}

$binarySourceRoot = Join-Path $repositoryRoot 'FancyWM.Tests/bin/Release/net10.0-windows10.0.18362.0'
$archiveRoot = Join-Path $snapshotRoot 'release-test-binaries'
Assert-Condition (!(Test-Path -LiteralPath $archiveRoot)) 'Baseline binary archive already exists'
New-Item -ItemType Directory -Path $archiveRoot | Out-Null
Copy-Item -Path (Join-Path $binarySourceRoot '*') -Destination $archiveRoot -Recurse

$archiveFiles = @(Get-ChildItem -LiteralPath $archiveRoot -Recurse -File | Sort-Object FullName)
$archiveManifestPath = Join-Path $snapshotRoot 'release-test-binaries.csv'
$archiveManifest = @($archiveFiles | ForEach-Object {
    [pscustomobject]@{
        Path = [IO.Path]::GetRelativePath($archiveRoot, $_.FullName).Replace('\', '/')
        Hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
        Length = $_.Length
    }
})
$archiveManifest | Export-Csv -NoTypeInformation -LiteralPath $archiveManifestPath

$productionRecord = @($archiveManifest | Where-Object Path -ceq 'FancyWM.dll')
$benchmarkRecord = @($archiveManifest | Where-Object Path -ceq 'FancyWM.Tests.dll')
Assert-Condition ($productionRecord.Count -eq 1) 'Archived FancyWM.dll count differs'
Assert-Condition ($benchmarkRecord.Count -eq 1) 'Archived FancyWM.Tests.dll count differs'

function Source-Hash([string]$Path) {
    return @($manifest | Where-Object Path -ceq $Path)[0].SHA256
}

$provenance = [ordered]@{
    Snapshot = $SnapshotId
    SnapshotManifestSHA256 = $manifestHash
    ArchiveManifestSHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $archiveManifestPath).Hash
    BuildSucceeded = $true
    BuildCommand = 'dotnet build FancyWM.Tests/FancyWM.Tests.csproj --configuration Release --no-restore --no-incremental'
    Configuration = 'Release'
    TargetFramework = 'net10.0-windows10.0.18362.0'
    Architecture = 'inherited dotnet host'
    Scope = 'PERF-008 MasterSatelliteLayoutEngine.ContainsWindow ordered scan without captured predicate'
    FancyWMDllSHA256 = $productionRecord[0].Hash
    FancyWMTestsDllSHA256 = $benchmarkRecord[0].Hash
    ProductionSource = "source/$productionSource"
    ProductionSourceSHA256 = Source-Hash $productionSource
    RegressionSource = "source/$regressionSource"
    RegressionSourceSHA256 = Source-Hash $regressionSource
    FixtureSource = "source/$fixtureSource"
    FixtureSourceSHA256 = Source-Hash $fixtureSource
    RunnerSource = "source/$runnerSource"
    RunnerSourceSHA256 = Source-Hash $runnerSource
    ArchiveScriptSource = "source/$archiveScriptSource"
    ArchiveScriptSourceSHA256 = Source-Hash $archiveScriptSource
    VerifierSource = "source/$verifierSource"
    VerifierSourceSHA256 = Source-Hash $verifierSource
    AppendScriptSource = "source/$appendScriptSource"
    AppendScriptSourceSHA256 = Source-Hash $appendScriptSource
    RedFilter = $filter
    BuildLog = 'validation/baseline-release-build.log'
    BuildLogSHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $buildLog).Hash
    ExpectedRedTestExitCode = 1
    RedTrx = "validation/$redTrxName"
    RedTrxSHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $redTrx).Hash
    RedLog = 'validation/baseline-contains-window-red.log'
    RedLogSHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $redLog).Hash
    SemanticTestsPassed = $redResult.Passed
    CapturedPredicateAllocationTestsFailed = 1
    CopiedFileCount = $archiveFiles.Count
    CopiedBytes = [long](($archiveFiles | Measure-Object Length -Sum).Sum)
}
$provenancePath = Join-Path $snapshotRoot 'release-test-binaries.provenance.json'
$provenance | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $provenancePath
$provenance | ConvertTo-Json -Depth 4
