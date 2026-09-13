[CmdletBinding()]
param(
    [Parameter(Mandatory)][Alias('SnapshotId')][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$CandidateSnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$BaselineSnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$ExperimentId,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Get-Sha256([string]$Path) {
    Assert-Condition (Test-Path -LiteralPath $Path -PathType Leaf) "Required file missing: $Path"
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Get-SafeRelativePath([string]$Root, [string]$Relative) {
    Assert-Condition (![string]::IsNullOrWhiteSpace($Relative) -and
        ![IO.Path]::IsPathRooted($Relative) -and !$Relative.Contains('\') -and
        !$Relative.Contains(':') -and $Relative -cnotmatch '(^|/)(\.|\.\.|)(/|$)') `
        "Noncanonical relative path: $Relative"
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $fullPath = [IO.Path]::GetFullPath((Join-Path $fullRoot $Relative))
    Assert-Condition ($fullPath.StartsWith(
        $fullRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) "Path escapes its root: $Relative"
    $fullPath
}

function Assert-Schema([object[]]$Records, [string]$Schema, [string]$Label) {
    Assert-Condition ($Records.Count -gt 0) "Empty CSV: $Label"
    foreach ($record in $Records) {
        Assert-Condition (($record.PSObject.Properties.Name -join '|') -ceq $Schema) `
            "CSV schema differs: $Label"
    }
}

function Read-NonnegativeInteger([string]$Value, [string]$Label) {
    $parsed = 0L
    Assert-Condition ($Value -cmatch '^(0|[1-9][0-9]*)$' -and
        [long]::TryParse($Value, [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) `
        "Invalid nonnegative Int64: $Label = $Value"
    $parsed
}

function Read-Trx([string]$Path) {
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
    Assert-Condition ($document.DocumentElement.LocalName -ceq 'TestRun' -and
        $document.DocumentElement.NamespaceURI -ceq
            'http://microsoft.com/schemas/VisualStudio/TeamTest/2010') "Invalid TRX root: $Path"
    return ,$document
}

function Get-SingleNode([Xml.XmlNode]$Parent, [string]$XPath, [string]$Label) {
    $nodes = @($Parent.SelectNodes($XPath))
    Assert-Condition ($nodes.Count -eq 1) "Expected exactly one $Label; got $($nodes.Count)"
    return ,$nodes[0]
}

function Read-Guid([string]$Value, [string]$Label) {
    $parsed = [guid]::Empty
    Assert-Condition ([guid]::TryParseExact($Value, 'D', [ref]$parsed) -and
        $parsed -ne [guid]::Empty) "Invalid GUID: $Label"
    $parsed
}

function Assert-AssemblyPath([string]$Actual, [string]$Expected, [string]$Label) {
    Assert-Condition (![string]::IsNullOrWhiteSpace($Actual) -and
        [IO.Path]::IsPathRooted($Actual)) "Missing absolute assembly path: $Label"
    Assert-Condition ([StringComparer]::OrdinalIgnoreCase.Equals(
        [IO.Path]::GetFullPath($Actual), [IO.Path]::GetFullPath($Expected))) `
        "TRX assembly path differs: $Label ($Actual)"
}

function Get-Median([double[]]$Values) {
    Assert-Condition ($Values.Count -eq 5) 'This protocol requires five samples per median.'
    $ordered = @($Values | Sort-Object)
    [double]$ordered[2]
}

function Assert-DerivedCsv([string]$Path, [string]$Schema, [object[]]$ExpectedRows) {
    $actual = @(Import-Csv -LiteralPath $Path)
    Assert-Schema $actual $Schema $Path
    # Export-Csv uses the normal PowerShell serialization of DateTimeOffset,
    # Double and Boolean values. Reproduce that serialization in memory, then
    # compare every field exactly, including canonical row order.
    $expected = @($ExpectedRows | ConvertTo-Csv -NoTypeInformation | ConvertFrom-Csv)
    Assert-Schema $expected $Schema "expected $Path"
    Assert-Condition ($actual.Count -eq $expected.Count) "Derived CSV row count differs: $Path"
    $columns = $Schema.Split('|')
    for ($index = 0; $index -lt $expected.Count; $index++) {
        foreach ($column in $columns) {
            Assert-Condition ($actual[$index].$column -ceq $expected[$index].$column) `
                "Derived CSV value/order differs: $Path row $($index + 1) column $column"
        }
    }
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

$rootOutput = & git -C $PSScriptRoot rev-parse --show-toplevel
Assert-Condition ($LASTEXITCODE -eq 0) 'Cannot resolve the repository root.'
$repositoryRoot = [IO.Path]::GetFullPath(($rootOutput -join '').Trim())
Assert-Condition (![StringComparer]::OrdinalIgnoreCase.Equals($CandidateSnapshotId, $BaselineSnapshotId)) `
    'Candidate and baseline snapshot IDs must differ.'
$artifactRoot = Join-Path $repositoryRoot 'artifacts/performance'
$candidateRoot = Get-SafeRelativePath $artifactRoot $CandidateSnapshotId
$baselineRoot = Get-SafeRelativePath $artifactRoot $BaselineSnapshotId
$experimentRoot = Get-SafeRelativePath $candidateRoot $ExperimentId
$targetFramework = 'net10.0-windows10.0.18362.0'
$testClass = 'FancyWM.Tests.AlgorithmicLayouts.MasterSatelliteContainsWindowTest'
$testMethod = 'ContainsWindowCounterScenario'
$testFqn = "$testClass.$testMethod"
$cases = @(foreach ($mode in @('master','first','last','absent')) { foreach ($satellites in @(1,4,9)) { "$mode-$satellites" } })
$calls = 100000L
$units = [ordered]@{
'allocated-bytes' = 'bytes/100000 calls'
'elapsed-ticks' = 'ticks/100000 calls'
'timestamp-frequency' = 'ticks/second'
'calls' = 'calls/scenario'
'matches' = 'matches/scenario'
'equality-calls' = 'equality-calls/scenario'
'hash-calls' = 'hash-calls/scenario'
}
$metrics = @($units.Keys)
$semanticMetrics = @($metrics | Where-Object {
    $_ -cnotin @('allocated-bytes', 'elapsed-ticks', 'timestamp-frequency')
})

# Verify every frozen source, including dependencies and the common harness.
# Live status documents can advance after the experiment without rewriting it.
$snapshots = @{}
foreach ($entry in @(
    [pscustomobject]@{ Id = $BaselineSnapshotId; Root = $baselineRoot },
    [pscustomobject]@{ Id = $CandidateSnapshotId; Root = $candidateRoot }
)) {
    $manifestPath = Join-Path $entry.Root 'manifest.csv'
    $metadataPath = Join-Path $entry.Root 'snapshot.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $manifestHash = Get-Sha256 $manifestPath
    Assert-Condition ($metadata.SnapshotId -ceq $entry.Id -and
        $metadata.ManifestSHA256 -ceq $manifestHash) "Snapshot identity/hash differs: $($entry.Id)"
    $records = @(Import-Csv -LiteralPath $manifestPath)
    Assert-Schema $records 'Path|SHA256' "$($entry.Id)/manifest.csv"
    $byPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $physicalPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $sourceRoot = Join-Path $entry.Root 'source'
    foreach ($record in $records) {
        $sourcePath = Get-SafeRelativePath $sourceRoot $record.Path
        Assert-Condition ($record.SHA256 -cmatch '^[0-9A-F]{64}$' -and
            $byPath.TryAdd($record.Path, $record) -and $physicalPaths.Add($sourcePath)) `
            "Malformed/duplicate source manifest row: $($entry.Id)/$($record.Path)"
        Assert-Condition ((Get-Sha256 $sourcePath) -ceq $record.SHA256) `
            "Frozen source hash differs: $($entry.Id)/$($record.Path)"
    }
    $sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Force)
    Assert-Condition ($sourceFiles.Count -eq $records.Count) "Unlisted frozen source files: $($entry.Id)"
    foreach ($file in $sourceFiles) {
        $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName).Replace('\', '/')
        Assert-Condition ($byPath.ContainsKey($relative)) "Unlisted source path: $($entry.Id)/$relative"
    }
    $snapshots[$entry.Id] = [pscustomobject]@{
        Metadata = $metadata; Records = $records; ByPath = $byPath
        ManifestSHA256 = $manifestHash; MetadataSHA256 = Get-Sha256 $metadataPath
    }
}
$baseline = $snapshots[$BaselineSnapshotId]
$candidate = $snapshots[$CandidateSnapshotId]
Assert-Condition ($baseline.Records.Count -eq $candidate.Records.Count -and
    $baseline.Metadata.Commit -ceq $candidate.Metadata.Commit -and
    $baseline.Metadata.Tree -ceq $candidate.Metadata.Tree -and
    ($baseline.Metadata.Submodules -join "`n") -ceq ($candidate.Metadata.Submodules -join "`n")) `
    'Baseline/candidate snapshot input sets or repository identities differ.'
$sourceDifferences = @()
foreach ($record in $candidate.Records) {
    Assert-Condition ($baseline.ByPath.ContainsKey($record.Path)) "Baseline source missing: $($record.Path)"
    if ($baseline.ByPath[$record.Path].SHA256 -cne $record.SHA256) { $sourceDifferences += $record.Path }
}
Assert-Condition ($sourceDifferences.Count -eq 1 -and $sourceDifferences[0] -ceq 'FancyWM/AlgorithmicLayouts/MasterSatelliteLayoutEngine.cs') `
    "Unexpected source delta: $($sourceDifferences -join ', ')"
$requiredSources = @{
    ProductionSource = 'FancyWM/AlgorithmicLayouts/MasterSatelliteLayoutEngine.cs'
    RegressionSource = 'FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteContainsWindowTest.cs'
    FixtureSource = 'FancyWM/AlgorithmicLayouts/MasterSatelliteRuntimeState.cs'
    RunnerSource = 'scripts/performance/Measure-MicaRefresh.ps1'
    ArchiveScriptSource = 'scripts/performance/Archive-ContainsWindowBaseline.ps1'
    VerifierSource = 'scripts/performance/Verify-ContainsWindowScan.ps1'
    AppendScriptSource = 'scripts/performance/Append-ContainsWindowMeasurements.ps1'
}
foreach ($path in @($requiredSources.Values) + @('scripts/performance/Verify-ContainsWindowScan.ps1')) {
    Assert-Condition ($candidate.ByPath.ContainsKey($path)) "Required common source missing: $path"
}
$oldSource = [IO.File]::ReadAllText((Join-Path $baselineRoot 'source/FancyWM/AlgorithmicLayouts/MasterSatelliteLayoutEngine.cs'))
$newSource = [IO.File]::ReadAllText((Join-Path $candidateRoot 'source/FancyWM/AlgorithmicLayouts/MasterSatelliteLayoutEngine.cs'))
$methodPattern = '(?ms)^        private static bool ContainsWindow\(MasterSatelliteRuntimeState state, IWindow window\)\r?\n.*?(?=^        private static int IndexOfWindow)'
$oldMethods = [regex]::Matches($oldSource, $methodPattern)
$newMethods = [regex]::Matches($newSource, $methodPattern)
Assert-Condition ($oldMethods.Count -eq 1 -and $newMethods.Count -eq 1) 'ContainsWindow method boundary differs.'
$expectedOld = @'
        private static bool ContainsWindow(MasterSatelliteRuntimeState state, IWindow window)
        {
            return WindowEquals(state.Master, window)
                || state.Satellites.Any(satellite => WindowEquals(satellite, window));
        }
'@
$expectedNew = @'
        private static bool ContainsWindow(MasterSatelliteRuntimeState state, IWindow window)
        {
            if (WindowEquals(state.Master, window))
            {
                return true;
            }
            foreach (var satellite in state.Satellites)
            {
                if (WindowEquals(satellite, window))
                {
                    return true;
                }
            }
            return false;
        }
'@
Assert-Condition (($oldMethods[0].Value.Trim() -replace '\s+', ' ') -ceq ($expectedOld.Trim() -replace '\s+', ' ')) 'Baseline captured predicate source differs.'
Assert-Condition (($newMethods[0].Value.Trim() -replace '\s+', ' ') -ceq ($expectedNew.Trim() -replace '\s+', ' ')) 'Candidate ordered enumeration source differs.'
$reconstructedBaseline = $newSource.Substring(0, $newMethods[0].Index) + $oldMethods[0].Value + $newSource.Substring($newMethods[0].Index + $newMethods[0].Length)
Assert-Condition ($reconstructedBaseline.Replace([string][char]13, '') -ceq $oldSource.Replace([string][char]13, '')) 'Source delta exceeds ContainsWindow captured predicate removal.'
$fixtureText = Get-Content -LiteralPath (Join-Path $candidateRoot 'source/FancyWM.Tests/AlgorithmicLayouts/MasterSatelliteContainsWindowTest.cs') -Raw
Assert-Condition ($fixtureText.Contains('private const int WarmupCalls = 1000;') -and $fixtureText.Contains('private const int MeasuredCalls = 100000;') -and
    $fixtureText.Contains('value.ToString(CultureInfo.InvariantCulture)')) 'Common fixture iteration/counter protocol differs.'
Assert-Condition ((Get-Sha256 $PSCommandPath) -ceq $candidate.ByPath['scripts/performance/Verify-ContainsWindowScan.ps1'].SHA256) 'Verifier differs from the common frozen protocol.'

# Check the complete archived baseline tree and its build/red-test provenance.
$archiveRoot = Join-Path $baselineRoot 'release-test-binaries'
$archiveManifestPath = Join-Path $baselineRoot 'release-test-binaries.csv'
$archiveRecords = @(Import-Csv -LiteralPath $archiveManifestPath)
Assert-Schema $archiveRecords 'Path|Hash|Length' 'baseline archive manifest'
$archiveByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$archivePhysicalPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$archiveBytes = 0L
foreach ($record in $archiveRecords) {
    $path = Get-SafeRelativePath $archiveRoot $record.Path
    Assert-Condition ($record.Hash -cmatch '^[0-9A-F]{64}$' -and
        $archiveByPath.TryAdd($record.Path, $record) -and $archivePhysicalPaths.Add($path)) `
        "Malformed/duplicate archive row: $($record.Path)"
    $length = Read-NonnegativeInteger $record.Length "archive/$($record.Path)/Length"
    Assert-Condition ((Get-Sha256 $path) -ceq $record.Hash -and
        (Get-Item -LiteralPath $path).Length -eq $length) "Archive content differs: $($record.Path)"
    $archiveBytes += $length
}
$archiveFiles = @(Get-ChildItem -LiteralPath $archiveRoot -Recurse -File -Force)
Assert-Condition ($archiveFiles.Count -eq $archiveRecords.Count) 'Archive file count differs.'
foreach ($file in $archiveFiles) {
    $relative = [IO.Path]::GetRelativePath($archiveRoot, $file.FullName).Replace('\', '/')
    Assert-Condition ($archiveByPath.ContainsKey($relative)) "Unlisted archive path: $relative"
}
Assert-Condition ($archiveByPath.ContainsKey('FancyWM.dll') -and
    $archiveByPath.ContainsKey('FancyWM.Tests.dll')) 'Baseline production/fixture assembly missing.'
$provenancePath = Join-Path $baselineRoot 'release-test-binaries.provenance.json'
$provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
Assert-Condition ($provenance.Snapshot -ceq $BaselineSnapshotId -and
    $provenance.BuildSucceeded -is [bool] -and $provenance.BuildSucceeded -and
    $provenance.Configuration -ceq 'Release' -and $provenance.TargetFramework -ceq $targetFramework -and
    $provenance.BuildCommand -ceq 'dotnet build FancyWM.Tests/FancyWM.Tests.csproj --configuration Release --no-restore --no-incremental' -and
    $provenance.ExpectedRedTestExitCode -eq 1 -and
    $provenance.SnapshotManifestSHA256 -ceq $baseline.ManifestSHA256 -and
    $provenance.ArchiveManifestSHA256 -ceq (Get-Sha256 $archiveManifestPath) -and
    $provenance.FancyWMDllSHA256 -ceq $archiveByPath['FancyWM.dll'].Hash -and
    $provenance.FancyWMTestsDllSHA256 -ceq $archiveByPath['FancyWM.Tests.dll'].Hash -and
    $provenance.CopiedFileCount -eq $archiveFiles.Count -and $provenance.CopiedBytes -eq $archiveBytes -and
    $provenance.SemanticTestsPassed -eq 18 -and $provenance.CapturedPredicateAllocationTestsFailed -eq 1) `
    'Baseline build/archive provenance differs.'
foreach ($field in $requiredSources.Keys) {
    Assert-Condition ($provenance.$field -ceq "source/$($requiredSources[$field])" -and
        $provenance."${field}SHA256" -ceq $baseline.ByPath[$requiredSources[$field]].SHA256) `
        "Baseline provenance source differs: $field"
}
foreach ($field in @('BuildLog', 'RedLog', 'RedTrx')) {
    $path = Get-SafeRelativePath $baselineRoot $provenance.$field
    Assert-Condition ((Get-Sha256 $path) -ceq $provenance."${field}SHA256") `
        "Baseline provenance evidence differs: $field"
}
$redMethods = @('ContainsWindowChecksMasterFirstAndShortCircuitsWithResidentFirstEquality','ContainsWindowChecksSatellitesInOrderAndStopsAtFirstMatch','ContainsWindowHandlesMissingMasterAndEmptySatellites','ContainsWindowPropagatesExactEqualityExceptionWithoutLaterCallbacks','ContainsWindowSeesMasterCallbackMutationBeforeSatelliteEnumeration','ContainsWindowMutationThenFalseThrowsOnNextMoveNext','ContainsWindowMutationThenTrueShortCircuitsBeforeNextMoveNext','ContainsWindowPreservesRuntimeStateAndReadOnlyViewIdentity','ContainsWindowAvoidsCapturedPredicateAllocation','ContainsWindowCounterScenario')
$expectedFilter = ($redMethods | ForEach-Object { "FullyQualifiedName=$testClass.$_" }) -join '|'
Assert-Condition ($provenance.RedFilter -ceq $expectedFilter) 'Baseline red filter differs.'
$redResult = Test-ContainsWindowRegressionTrx (Get-SafeRelativePath $baselineRoot $provenance.RedTrx) $true
$redPassed = $redResult.Passed
$redFailed = $redResult.Failed
$candidateRegressions = @(foreach ($configuration in @('Debug','Release')) {
    $trxPath = Join-Path $candidateRoot "validation/$configuration-contains-window-targeted.trx"
    $logPath = [IO.Path]::ChangeExtension($trxPath, '.log')
    $result = Test-ContainsWindowRegressionTrx $trxPath $false
    Assert-Condition (($result.LeafNames -join '|') -ceq ($redResult.LeafNames -join '|')) "Candidate regression leaf set differs: $configuration"
    [pscustomobject]@{ Configuration = $configuration; Leaves = $result.Leaves; Passed = $result.Passed; Failed = $result.Failed; TrxSHA256 = Get-Sha256 $trxPath; LogSHA256 = Get-Sha256 $logPath }
})

# Every path in both experiment binary trees must be checked exactly once.
$binaryChecksPath = Join-Path $experimentRoot 'contains-window-binary-checks.csv'
$binaryChecks = @(Import-Csv -LiteralPath $binaryChecksPath)
Assert-Schema $binaryChecks 'Path|CandidateSHA256|BaselineSHA256' 'experiment binary checks'
$binaryRoots = @{ baseline = Join-Path $experimentRoot 'baseline'; candidate = Join-Path $experimentRoot 'binaries' }
$binaryByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$binaryPhysicalPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$binaryDifferences = @()
foreach ($record in $binaryChecks) {
    $candidatePath = Get-SafeRelativePath $binaryRoots.candidate $record.Path
    $baselinePath = Get-SafeRelativePath $binaryRoots.baseline $record.Path
    Assert-Condition ($record.CandidateSHA256 -cmatch '^[0-9A-F]{64}$' -and
        $record.BaselineSHA256 -cmatch '^[0-9A-F]{64}$' -and
        $binaryByPath.TryAdd($record.Path, $record) -and $binaryPhysicalPaths.Add($candidatePath)) `
        "Malformed/duplicate binary-check row: $($record.Path)"
    Assert-Condition ((Get-Sha256 $candidatePath) -ceq $record.CandidateSHA256 -and
        (Get-Sha256 $baselinePath) -ceq $record.BaselineSHA256) "Experiment binary differs: $($record.Path)"
    if ($record.CandidateSHA256 -cne $record.BaselineSHA256) { $binaryDifferences += $record.Path }
}
foreach ($variant in @('baseline', 'candidate')) {
    $files = @(Get-ChildItem -LiteralPath $binaryRoots[$variant] -Recurse -File -Force)
    Assert-Condition ($files.Count -eq $binaryChecks.Count) "Experiment binary file count differs: $variant"
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($binaryRoots[$variant], $file.FullName).Replace('\', '/')
        Assert-Condition ($binaryByPath.ContainsKey($relative)) "Unlisted experiment binary: $variant/$relative"
    }
}
Assert-Condition ($binaryDifferences.Count -eq 1 -and $binaryDifferences[0] -ceq 'FancyWM.dll' -and
    $binaryByPath.ContainsKey('FancyWM.Tests.dll')) "Unexpected binary delta: $($binaryDifferences -join ', ')"
$productionHashes = @{
    baseline = $binaryByPath['FancyWM.dll'].BaselineSHA256
    candidate = $binaryByPath['FancyWM.dll'].CandidateSHA256
}
$fixtureHash = $binaryByPath['FancyWM.Tests.dll'].CandidateSHA256
Assert-Condition ($productionHashes.baseline -ceq $provenance.FancyWMDllSHA256) `
    'Experiment baseline production differs from the archived assembly.'
# The runner recompiles the common final fixture once, then gives those exact
# bytes to both variants. Its archive-time build hash is recorded separately;
# it need not equal the rebuilt common fixture hash.

# Parse the ten original process records and their complete raw counter matrix.
$expectedRunOrder = @(
    'baseline-1', 'candidate-1', 'candidate-2', 'baseline-2', 'baseline-3',
    'candidate-3', 'candidate-4', 'baseline-4', 'baseline-5', 'candidate-5'
)
$trxFiles = @(Get-ChildItem -LiteralPath $experimentRoot -Filter '*.trx' -File)
Assert-Condition ($trxFiles.Count -eq 10 -and
    @($trxFiles | Where-Object BaseName -cnotin $expectedRunOrder).Count -eq 0) 'Experiment TRX file set differs.'
$runIds = [Collections.Generic.HashSet[guid]]::new()
$executionIds = [Collections.Generic.HashSet[guid]]::new()
$testIdsByVariant = @{}
$rawCounters = [Collections.Generic.Dictionary[string, long]]::new([StringComparer]::Ordinal)
$processes = @()
$counterPattern = '^PERFCOUNTER contains-window-((?:master|first|last|absent)-(?:1|4|9)) (' + ($metrics -join '|') + ') (0|[1-9][0-9]*)$'
foreach ($runName in $expectedRunOrder) {
    $variant, $pair = $runName.Split('-')
    $trxPath = Join-Path $experimentRoot "$runName.trx"
    $trx = Read-Trx $trxPath
    $root = $trx.DocumentElement
    $times = Get-SingleNode $root './*[local-name()="Times"]' "$runName Times"
    $result = Get-SingleNode $root './*[local-name()="Results"]/*[local-name()="UnitTestResult"]' "$runName result"
    $definition = Get-SingleNode $root './*[local-name()="TestDefinitions"]/*[local-name()="UnitTest"]' "$runName definition"
    $method = Get-SingleNode $definition './*[local-name()="TestMethod"]' "$runName method"
    $execution = Get-SingleNode $definition './*[local-name()="Execution"]' "$runName execution"
    $testEntry = Get-SingleNode $root './*[local-name()="TestEntries"]/*[local-name()="TestEntry"]' "$runName test entry"
    $summary = Get-SingleNode $root './*[local-name()="ResultSummary"]' "$runName summary"
    $counters = Get-SingleNode $summary './*[local-name()="Counters"]' "$runName result counters"
    Assert-Condition (@($root.SelectNodes('//*[local-name()="UnitTestResult"]')).Count -eq 1 -and
        @($root.SelectNodes('//*[local-name()="TestMethod"]')).Count -eq 1 -and
        @($root.SelectNodes('//*[local-name()="ErrorInfo"]')).Count -eq 0) "$runName contains extra/error test records."
    $runGuid = Read-Guid $root.id "$runName run ID"
    $executionGuid = Read-Guid $result.executionId "$runName execution ID"
    $testGuid = Read-Guid $result.testId "$runName test ID"
    Assert-Condition ($runIds.Add($runGuid) -and $executionIds.Add($executionGuid)) "Duplicate process identity: $runName"
    Assert-Condition ($result.testName -ceq $testMethod -and $definition.name -ceq $testMethod -and
        $method.className -ceq $testClass -and $method.name -ceq $testMethod -and
        $method.adapterTypeName -ceq 'executor://mstestadapter/v2' -and
        $result.outcome -ceq 'Passed' -and $summary.outcome -ceq 'Completed' -and
        $definition.id -ceq $result.testId -and $execution.id -ceq $result.executionId -and
        $testEntry.testId -ceq $result.testId -and $testEntry.executionId -ceq $result.executionId -and
        $testEntry.testListId -ceq $result.testListId -and
        $result.relativeResultsDirectory -ceq $result.executionId) "TRX FQN/outcome/identity differs: $runName"
    foreach ($name in @('total', 'executed', 'passed')) {
        Assert-Condition ($counters.GetAttribute($name) -ceq '1') "TRX $name counter differs: $runName"
    }
    foreach ($name in @('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted',
        'notRunnable', 'notExecuted', 'disconnected', 'warning', 'completed', 'inProgress', 'pending')) {
        Assert-Condition ($counters.GetAttribute($name) -ceq '0') "TRX $name counter differs: $runName"
    }
    if ($testIdsByVariant.ContainsKey($variant)) {
        Assert-Condition ($testIdsByVariant[$variant] -eq $testGuid) "TRX test identity changed within $variant."
    }
    else { $testIdsByVariant[$variant] = $testGuid }
    $assemblyPath = Join-Path $binaryRoots[$variant] 'FancyWM.Tests.dll'
    Assert-AssemblyPath $method.codeBase $assemblyPath "$runName codeBase"
    Assert-AssemblyPath $definition.storage $assemblyPath "$runName storage"
    $start = [DateTimeOffset]::Parse($times.start, [Globalization.CultureInfo]::InvariantCulture)
    $finish = [DateTimeOffset]::Parse($times.finish, [Globalization.CultureInfo]::InvariantCulture)
    $testStart = [DateTimeOffset]::Parse($result.startTime, [Globalization.CultureInfo]::InvariantCulture)
    $testFinish = [DateTimeOffset]::Parse($result.endTime, [Globalization.CultureInfo]::InvariantCulture)
    Assert-Condition ($start -lt $finish -and $start -le $testStart -and
        $testStart -lt $testFinish -and $testFinish -le $finish) "Invalid TRX process/test interval: $runName"
    if ($processes.Count -gt 0) {
        Assert-Condition ($processes[-1].Finish -le $start) "A/B process order overlaps or differs at $runName."
    }
    $stdout = Get-SingleNode $result './*[local-name()="Output"]/*[local-name()="StdOut"]' "$runName stdout"
    Assert-Condition (@($root.SelectNodes('//*[local-name()="StdOut"]')).Count -eq 1) "Extra TRX stdout: $runName"
    $rawLines = @($stdout.InnerText -split '\r?\n' | Where-Object { $_.Length -gt 0 })
    Assert-Condition ($rawLines.Count -eq 85 -and $rawLines[0] -ceq 'TestContext Messages:') `
        "Raw counter envelope differs: $runName"
    $counterLines = @($rawLines | Select-Object -Skip 1)
    Assert-Condition ($counterLines.Count -eq 84) "Raw counter line count differs: $runName"
    foreach ($line in $counterLines) {
        $match = [regex]::Match($line, $counterPattern)
        Assert-Condition ($match.Success) "Unexpected raw counter: $runName/$line"
        $key = "$variant/$pair/$($match.Groups[1].Value)/$($match.Groups[2].Value)"
        $value = Read-NonnegativeInteger $match.Groups[3].Value "raw/$key"
        Assert-Condition ($rawCounters.TryAdd($key, $value)) "Duplicate raw counter: $key"
    }
    $logPath = Join-Path $experimentRoot "$runName.log"
    $processes += [pscustomobject]@{
        Run = $runName; RunId = $runGuid; ExecutionId = $executionGuid; TestId = $testGuid
        Start = $start; Finish = $finish; TestStart = $testStart; TestFinish = $testFinish
        TrxSHA256 = Get-Sha256 $trxPath; LogSHA256 = Get-Sha256 $logPath
    }
}
Assert-Condition ($rawCounters.Count -eq 840) 'Raw counter matrix is incomplete.'

# These strings are part of the runner protocol, not free-form acceptance claims.
$expectedConfiguration = "Release; $targetFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
$expectedInstrument = 'Actual production MasterSatelliteLayoutEngine.ContainsWindow through a bound private-method delegate;field-backed IWindow adapters and owned runtime state;master/first/last/absent queries with1/4/9 satellites;1000 discarded warmups then100000 calls;GC.GetAllocatedBytesForCurrentThread and Stopwatch with per-process timestamp frequency;exact matches,ordered equality calls and zero hash calls checked;common final fixture and dependencies with only separately archived hashed baseline FancyWM.dll differing;no transaction or clone reuse,native window/COM/WPF/DPI calls,whole-process CPU/GPU,frame cadence or presentation latency measurement;timing is descriptive without a required timing win'
$measurementsPath = Join-Path $experimentRoot 'measurements.csv'
$rows = @(Import-Csv -LiteralPath $measurementsPath)
Assert-Schema $rows 'experiment_id|snapshot_id|scenario|configuration|window_count|metric|unit|run|value|iterations|instrument|perf_id|variant|pair|binary_sha256|benchmark_sha256' 'measurements.csv'
Assert-Condition ($rows.Count -eq 840) "Measurement row count differs: $($rows.Count)"
$rowKeys = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$frequencies = [Collections.Generic.HashSet[long]]::new()
foreach ($row in $rows) {
    $scenario = [regex]::Match($row.scenario,
        '^Actual MasterSatelliteLayoutEngine\.ContainsWindow; contains-window-((master|first|last|absent)-(1|4|9));1000 discarded warmups then100000 calls$')
    Assert-Condition ($scenario.Success) "Measurement scenario differs: $($row.scenario)"
    $caseName = $scenario.Groups[1].Value
    $mode = $scenario.Groups[2].Value
    $satellites = [int]$scenario.Groups[3].Value
    Assert-Condition ($row.variant -cin @('baseline', 'candidate') -and
        $row.run -cmatch '^[1-5]$' -and $row.pair -ceq $row.run -and
        $row.window_count -ceq [string](1 + $satellites) -and $row.metric -cin $metrics -and
        $row.iterations -ceq '100000' -and $row.perf_id -ceq 'PERF-008' -and
        $row.experiment_id -ceq $ExperimentId -and $row.configuration -ceq $expectedConfiguration -and
        $row.instrument -ceq $expectedInstrument -and $row.benchmark_sha256 -ceq $fixtureHash) `
        "Measurement dimensions differ: $($row.variant)/$($row.run)/$caseName/$($row.metric)"
    $expectedSnapshot = if ($row.variant -ceq 'baseline') { $BaselineSnapshotId } else { $CandidateSnapshotId }
    Assert-Condition ($row.snapshot_id -ceq $expectedSnapshot -and
        $row.binary_sha256 -ceq $productionHashes[$row.variant] -and $row.unit -ceq $units[$row.metric]) `
        "Measurement provenance/unit differs: $($row.variant)/$($row.run)/$caseName/$($row.metric)"
    $key = "$($row.variant)/$($row.run)/$caseName/$($row.metric)"
    Assert-Condition ($rowKeys.TryAdd($key, $row)) "Duplicate measurement row: $key"
    $value = Read-NonnegativeInteger $row.value $key
    Assert-Condition ($rawCounters.ContainsKey($key) -and $rawCounters[$key] -eq $value) "CSV differs from raw TRX: $key"
    $expected = switch -CaseSensitive ($row.metric) {
        'allocated-bytes' { $null }
        'calls' { $calls }
        'matches' { if ($mode -ceq 'absent') { 0L } else { $calls } }
        'equality-calls' {
            if ($mode -ceq 'master') { $calls }
            elseif ($mode -ceq 'first') { 2L * $calls }
            else { (1L + $satellites) * $calls }
        }
        'hash-calls' { 0L }
        { $_ -cin @('elapsed-ticks', 'timestamp-frequency') } {
            Assert-Condition ($value -gt 0) "Nonpositive timing/frequency: $key"
            if ($_ -ceq 'timestamp-frequency') { [void]$frequencies.Add($value) }
            $null
        }
        default { throw "Unexpected metric: $($row.metric)" }
    }
    if ($null -ne $expected) {
        Assert-Condition ($value -eq $expected) "Measurement value differs: $key ($value != $expected)"
    }
}
Assert-Condition ($frequencies.Count -eq 1) 'Stopwatch frequency differs across scenarios, variants or repetitions.'
$frequency = @($frequencies)[0]
$allocationSummary = @()
$timingSummary = @()
$timingPairs = @()
$semanticComparisons = 0
$timingWins = 0
$timingTies = 0
foreach ($caseName in $cases) {
    $baselineNanoseconds = @()
    $candidateNanoseconds = @()
    foreach ($pair in 1..5) {
        foreach ($metric in $metrics) {
            foreach ($variant in @('baseline', 'candidate')) {
                Assert-Condition ($rowKeys.ContainsKey("$variant/$pair/$caseName/$metric")) `
                    "Incomplete measurement matrix: $variant/$pair/$caseName/$metric"
            }
        }
        foreach ($metric in $semanticMetrics) {
            Assert-Condition ($rawCounters["baseline/$pair/$caseName/$metric"] -eq
                $rawCounters["candidate/$pair/$caseName/$metric"]) `
                "Paired semantic counter differs: $pair/$caseName/$metric"
            $semanticComparisons++
        }
        $baselineBytes = $rawCounters["baseline/$pair/$caseName/allocated-bytes"]
        $candidateBytes = $rawCounters["candidate/$pair/$caseName/allocated-bytes"]
        Assert-Condition ($candidateBytes -lt $baselineBytes) "Candidate allocation did not improve: $pair/$caseName"
        $allocationSummary += [pscustomobject]@{
            Case = $caseName; Pair = $pair
            BaselineBytes = $baselineBytes; CandidateBytes = $candidateBytes
            SavedBytes = $baselineBytes - $candidateBytes
            BaselineBytesPerCall = [double]$baselineBytes / $calls
            CandidateBytesPerCall = [double]$candidateBytes / $calls
            SavedBytesPerCall = [double]($baselineBytes - $candidateBytes) / $calls
        }
        $baselineTicks = $rawCounters["baseline/$pair/$caseName/elapsed-ticks"]
        $candidateTicks = $rawCounters["candidate/$pair/$caseName/elapsed-ticks"]
        # Normalize each sample using its recorded frequency and measured passes.
        # Stopwatch ticks are not TimeSpan ticks; there is no fixed tick-to-ns divisor.
        $baselineNs = [double]$baselineTicks * 1e9 / [double]$frequency / [double]$calls
        $candidateNs = [double]$candidateTicks * 1e9 / [double]$frequency / [double]$calls
        $baselineNanoseconds += $baselineNs
        $candidateNanoseconds += $candidateNs
        if ($candidateTicks -lt $baselineTicks) { $timingWins++ }
        elseif ($candidateTicks -eq $baselineTicks) { $timingTies++ }
        $timingPairs += [pscustomobject]@{
            Case = $caseName; Pair = $pair
            BaselineNanosecondsPerCall = $baselineNs; CandidateNanosecondsPerCall = $candidateNs
            DeltaPercent = ($candidateNs - $baselineNs) / $baselineNs * 100.0
        }
    }
    $baselineMedian = Get-Median $baselineNanoseconds
    $candidateMedian = Get-Median $candidateNanoseconds
    $timingSummary += [pscustomobject]@{
        Case = $caseName; BaselineMedianNanosecondsPerCall = $baselineMedian
        CandidateMedianNanosecondsPerCall = $candidateMedian
        MedianDeltaPercent = 100.0 * ($candidateMedian - $baselineMedian) / $baselineMedian
    }
}

# These are derived summaries, not independent timing evidence. In particular,
# process CSV date formatting omits sub-second precision; the exact original
# TRX intervals above remain the authority for ordering and overlap checks.
$processTimesPath = Join-Path $experimentRoot 'contains-window-process-times.csv'
$expectedProcessRows = @($processes | ForEach-Object {
    [pscustomobject]@{
        Run = $_.Run; TrxId = $_.RunId.ToString('D'); ExecutionId = $_.ExecutionId.ToString('D')
        Start = $_.Start; Finish = $_.Finish; TrxSHA256 = $_.TrxSHA256
    }
})
Assert-Condition ($expectedProcessRows.Count -eq 10) 'Derived process matrix must have ten rows.'
Assert-DerivedCsv $processTimesPath 'Run|TrxId|ExecutionId|Start|Finish|TrxSHA256' $expectedProcessRows

$report = [ordered]@{
    Verdict = 'ACCEPT'; Accepted = $true; VerifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Scenario = 'ContainsWindow'; PerfId = 'PERF-008'
    BaselineSnapshot = $BaselineSnapshotId; CandidateSnapshot = $CandidateSnapshotId; ExperimentId = $ExperimentId
    SourceManifestFiles = $candidate.Records.Count; SourceManifestDifferences = $sourceDifferences
    ExactSourceReplacement = 'ContainsWindow captured predicate replaced by the same resident-first short-circuit enumeration'
    BaselineArchiveFiles = $archiveFiles.Count; BaselineArchiveBytes = $archiveBytes
    BaselineSemanticTestsPassed = $redPassed; BaselineCapturedPredicateAllocationTestsFailed = $redFailed
    BaselineRegressionLeaves = $redResult.Leaves; CandidateRegressions = $candidateRegressions
    BinaryTreeFiles = $binaryChecks.Count; BinaryDifferences = $binaryDifferences
    BaselineProductionSHA256 = $productionHashes.baseline; CandidateProductionSHA256 = $productionHashes.candidate
    ArchivedBaselineFixtureSHA256 = $provenance.FancyWMTestsDllSHA256
    CommonFixtureSHA256 = $fixtureHash; TestFqn = $testFqn
    ProcessCount = $processes.Count; ProcessOrder = @($processes.Run); Processes = $processes
    MeasurementRows = $rows.Count; RawCounters = $rawCounters.Count
    PairedSemanticComparisons = $semanticComparisons; AllocationPairs = 60; Allocation = $allocationSummary
    TimestampFrequency = $frequency; TimingFormula = 'elapsed-ticks * 1e9 / timestamp-frequency / calls'
    TimingClassification = $(if ($timingWins -eq 60) { 'all measured pairs lower' } elseif ($timingWins -eq 0 -and $timingTies -eq 0) { 'all measured pairs higher' } else { 'mixed' })
    TimingAcceptanceGate = $false; PairedTimingWins = $timingWins; PairedTimingTies = $timingTies
    PairedTimingLosses = 60 - $timingWins - $timingTies; Timing = $timingSummary; TimingPairs = $timingPairs
    MeasurementsSHA256 = Get-Sha256 $measurementsPath; BinaryChecksSHA256 = Get-Sha256 $binaryChecksPath
    ProcessTimesRows = $expectedProcessRows.Count; ProcessTimesSHA256 = Get-Sha256 $processTimesPath
    BaselineManifestSHA256 = $baseline.ManifestSHA256; CandidateManifestSHA256 = $candidate.ManifestSHA256
    BaselineSnapshotSHA256 = $baseline.MetadataSHA256; CandidateSnapshotSHA256 = $candidate.MetadataSHA256
    BaselineArchiveManifestSHA256 = Get-Sha256 $archiveManifestPath
    BaselineProvenanceSHA256 = Get-Sha256 $provenancePath; VerifierSHA256 = Get-Sha256 $PSCommandPath
    Limitations = 'Managed ContainsWindow membership calls with ordered resident equality and the same read-only collection enumeration. No broad transaction/clone reuse. Timing is descriptive. Native COM/WPF/DPI behavior, presentation latency, WPR, whole-process CPU/GPU and energy remain E2E_PENDING / NOT_MEASURED.'
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $candidateRoot 'validation/contains-window-strict-verification.json'
}
elseif (![IO.Path]::IsPathRooted($ReportPath)) { $ReportPath = Join-Path $repositoryRoot $ReportPath }
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
Assert-Condition (!(Test-Path -LiteralPath $ReportPath)) "Report already exists; choose a new ReportPath: $ReportPath"
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $ReportPath))
$json = $report | ConvertTo-Json -Depth 8
$stream = [IO.File]::Open($ReportPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    try { $writer.WriteLine($json) }
    finally { $writer.Dispose() }
}
finally { $stream.Dispose() }
$json
