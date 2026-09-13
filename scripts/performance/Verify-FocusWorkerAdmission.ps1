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


function Test-FocusWorkerRegressionTrx([string]$Path, [bool]$ExpectedRed, [string]$ExpectedAssemblyPath = '') {
    $expectedCases = [ordered]@{
        'RepeatedCompletedWorkerAdmissionsPreserveSemantics' = @('direct-success','alt-retry','attach-failure')
        'RepeatedCompletedWorkerAdmissionsStayWithinCallerAllocationBudget' = @('direct-success','alt-retry','attach-failure')
        'CompletedWorkerStartDelegateDoesNotRootRequestSequence' = @('')
        'FocusWorkerAdmissionCounterScenario' = @('')
    }
    $expectedClass = 'FancyWM.Tests.Utilities.FocusHelperTest'
    $regressionName = 'RepeatedCompletedWorkerAdmissionsStayWithinCallerAllocationBudget'
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
    Assert-Condition ($all.Count -eq 10 -and $leaves.Count -eq 8 -and $topResults.Count -eq 4 -and $definitions.Count -eq 4 -and $entries.Count -eq 4) "Regression raw/leaf/definition/entry dimensions differ: $Path"
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
            $aggregateOutcome = if ($ExpectedRed -and $methodName -ceq $regressionName) { 'Failed' } else { 'Passed' }
            Assert-Condition ($result.GetAttribute('resultType') -ceq 'DataDrivenTest' -and $result.outcome -ceq $aggregateOutcome -and
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
            Assert-Condition ($messages.Count -eq 1) 'Baseline allocation failure message is missing or duplicated.'
            $modeMatch = [regex]::Match($leaf.testName, '^RepeatedCompletedWorkerAdmissionsStayWithinCallerAllocationBudget \((direct-success|alt-retry|attach-failure)\)$')
            Assert-Condition ($modeMatch.Success) 'Unknown worker-admission allocation case.'
            $failurePattern = '^Assert\.IsTrue failed\. Repeated admitted worker construction exceeds its caller allocation budget: mode=' +
                [regex]::Escape($modeMatch.Groups[1].Value) + ', total=(?<allocated>[1-9][0-9]*) B, per-call=(?<perCall>[0-9]+[.,][0-9]{3}) B, budget=336 B/call\.$'
            $failureMatch = [regex]::Match($messages[0].InnerText.TrimEnd(), $failurePattern)
            Assert-Condition ($failureMatch.Success) 'Baseline failed for a reason other than worker-admission caller allocation.'
            $allocated = Read-NonnegativeInteger $failureMatch.Groups['allocated'].Value 'baseline regression allocated bytes'
            Assert-Condition ($allocated -eq 47104 -and
                $failureMatch.Groups['perCall'].Value.Replace(',', '.') -ceq '368.000') 'Expected baseline caller allocation of 47104 bytes /128 calls =368 B/call.'
            $failures++
        }
        else { Assert-Condition ($leaf.SelectNodes('./*[local-name()="Output"]/*[local-name()="ErrorInfo"]').Count -eq 0) 'Unexpected passing regression error.' }
    }
    Assert-Condition ($seen.SetEquals([string[]]@($expectedLeafKeys))) 'Regression exact method|DataRow set differs.'
    $expectedFailures = if ($ExpectedRed) { 3 } else { 0 }
    $expectedRawFailures = if ($ExpectedRed) { 4 } else { 0 }
    Assert-Condition ($failures -eq $expectedFailures -and @($all | Where-Object outcome -CNE 'Passed').Count -eq $expectedRawFailures -and
        @($document.SelectNodes('//*[local-name()="ErrorInfo"]')).Count -eq $expectedFailures) "Regression aggregate outcomes differ: $Path"
    $counters = @($document.SelectNodes('//*[local-name()="Counters"]'))
    Assert-Condition ($counters.Count -eq 1 -and $summaries[0].SelectNodes('./*[local-name()="Counters"]').Count -eq 1) 'Regression counters missing/duplicated.'
    foreach ($name in @('total','executed','passed','failed','error','timeout','aborted','inconclusive','passedButRunAborted','notRunnable','notExecuted','disconnected','warning','completed','inProgress','pending')) {
        $expected = switch ($name) {
            { $_ -cin @('total','executed') } { 10 }
            'passed' { 10 - $expectedRawFailures }
            'failed' { $expectedRawFailures }
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
$testClass = 'FancyWM.Tests.Utilities.FocusHelperTest'
$testMethod = 'FocusWorkerAdmissionCounterScenario'
$testFqn = "$testClass.$testMethod"
$cases = @('direct-success','alt-retry','attach-failure')
$calls = 500L
$units = [ordered]@{
    'allocated-bytes' = 'bytes/500 admissions'; 'elapsed-ticks' = 'ticks/500 admissions'; 'timestamp-frequency' = 'ticks/second'
    'admissions' = 'admissions/scenario'; 'true-results' = 'true-completions/scenario'; 'current-results' = 'current-token-observations/scenario'
    'worker-starts' = 'worker-starts/scenario'; 'worker-exits' = 'worker-exits/scenario'; 'background-workers' = 'background-workers/scenario'
    'join-timeouts' = 'join-timeouts/scenario'; 'wait-calls' = 'wait-adapter-calls/scenario'; 'queue-calls' = 'queue-adapter-calls/scenario'
    'thread-id-reads' = 'thread-id-reads/scenario'; 'foreground-id-reads' = 'foreground-id-reads/scenario'
    'attach-calls' = 'attach-adapter-calls/scenario'; 'detach-calls' = 'detach-adapter-calls/scenario'
    'focus-calls' = 'focus-adapter-calls/scenario'; 'alt-presses' = 'alt-adapter-calls/scenario'; 'owned-attachments' = 'owned-attachments/scenario'
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
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json -DateKind String
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
Assert-Condition ($sourceDifferences.Count -eq 1 -and $sourceDifferences[0] -ceq 'FancyWM/Utilities/FocusHelper.cs') `
    "Unexpected source delta: $($sourceDifferences -join ', ')"
$requiredSources = @{
    ProductionSource = 'FancyWM/Utilities/FocusHelper.cs'
    RegressionSource = 'FancyWM.Tests/Utilities/FocusHelperTest.WorkerAdmission.cs'
    FixtureSource = 'FancyWM.Tests/Utilities/FocusHelperTest.WorkerAdmission.cs'
    RunnerSource = 'scripts/performance/Measure-FocusWorkerAdmission.ps1'
    PrepareScriptSource = 'scripts/performance/Prepare-FocusWorkerSnapshot.ps1'
    VerifierSource = 'scripts/performance/Verify-FocusWorkerAdmission.ps1'
    AppendScriptSource = 'scripts/performance/Append-FocusWorkerMeasurements.ps1'
}
foreach ($path in @($requiredSources.Values) + @('scripts/performance/Verify-FocusWorkerAdmission.ps1')) {
    Assert-Condition ($candidate.ByPath.ContainsKey($path)) "Required common source missing: $path"
}
$oldSource = [IO.File]::ReadAllText((Join-Path $baselineRoot 'source/FancyWM/Utilities/FocusHelper.cs'))
$newSource = [IO.File]::ReadAllText((Join-Path $candidateRoot 'source/FancyWM/Utilities/FocusHelper.cs'))
$oldSource = $oldSource.Replace([string][char]13, '')
$newSource = $newSource.Replace([string][char]13, '')
$field = '            private Thread? m_worker;'
$oldConstruction = '                        start = m_worker = new Thread(RunWorker)'
$newConstruction = '                        start = m_worker = new Thread(m_workerStart ??= RunWorker)'
Assert-Condition ([regex]::Matches($oldSource, [regex]::Escape($field)).Count -eq 1 -and
    [regex]::Matches($oldSource, [regex]::Escape($oldConstruction)).Count -eq 1 -and
    !$oldSource.Contains('m_workerStart')) 'Baseline worker field/construction differs.'
$expectedNew = $oldSource.Replace($field, $field + [char]10 + '            private ThreadStart? m_workerStart;').Replace($oldConstruction, $newConstruction)
Assert-Condition ($newSource -ceq $expectedNew) 'Source delta exceeds adding the lazy per-sequence ThreadStart field and using it at the existing locked worker construction.'
$fixtureText = Get-Content -LiteralPath (Join-Path $candidateRoot 'source/FancyWM.Tests/Utilities/FocusHelperTest.WorkerAdmission.cs') -Raw
foreach ($required in @(
    'private const int WorkerAdmissionRegressionWarmupCalls = 16;',
    'private const int WorkerAdmissionRegressionMeasuredCalls = 128;',
    'private const int WorkerAdmissionCounterWarmupCalls = 32;',
    'private const int WorkerAdmissionCounterMeasuredCalls = 500;',
    'private const long WorkerAdmissionAllocationBudgetPerCall = 336;',
    'value.ToString(CultureInfo.InvariantCulture)'
)) {
    Assert-Condition ($fixtureText.Contains($required)) "Common fixture protocol differs: $required"
}
Assert-Condition ((Get-Sha256 $PSCommandPath) -ceq $candidate.ByPath['scripts/performance/Verify-FocusWorkerAdmission.ps1'].SHA256) 'Verifier differs from the common frozen protocol.'

# Independently verify both isolated builds and all archived binaries. The
# preparation script's assertions are provenance, not a substitute for these
# checks of the actual frozen source, commands, raw TRX and output bytes.
function Test-PreparedWorkerSnapshot([string]$SnapshotRoot, [string]$Id, [string]$Role, [object]$Snapshot) {
    $archiveRoot = Join-Path $SnapshotRoot 'release-test-binaries'
    $archiveManifestPath = Join-Path $SnapshotRoot 'release-test-binaries.csv'
    $archiveRecords = @(Import-Csv -LiteralPath $archiveManifestPath)
    Assert-Schema $archiveRecords 'Path|Hash|Length' "$Role archive manifest"
    $archiveByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $archivePhysicalPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $archiveBytes = 0L
    $buildRoot = Join-Path $SnapshotRoot 'build-workspace'
    $binaryRoot = Join-Path $buildRoot "FancyWM.Tests/bin/Release/$targetFramework"
    foreach ($source in $Snapshot.Records) {
        Assert-Condition ((Get-Sha256 (Get-SafeRelativePath $buildRoot $source.Path)) -ceq $source.SHA256) "Isolated $Role build source differs: $($source.Path)"
    }
    foreach ($record in $archiveRecords) {
        $path = Get-SafeRelativePath $archiveRoot $record.Path
        Assert-Condition ($record.Hash -cmatch '^[0-9A-F]{64}$' -and
            $archiveByPath.TryAdd($record.Path, $record) -and $archivePhysicalPaths.Add($path)) "Malformed/duplicate $Role archive row: $($record.Path)"
        $length = Read-NonnegativeInteger $record.Length "$Role archive/$($record.Path)/Length"
        $builtPath = Get-SafeRelativePath $binaryRoot $record.Path
        Assert-Condition ((Get-Sha256 $path) -ceq $record.Hash -and (Get-Item -LiteralPath $path).Length -eq $length -and
            (Get-Sha256 $builtPath) -ceq $record.Hash -and (Get-Item -LiteralPath $builtPath).Length -eq $length) "Archived/built $Role content differs: $($record.Path)"
        $archiveBytes += $length
    }
    $archiveFiles = @(Get-ChildItem -LiteralPath $archiveRoot -Recurse -File -Force)
    $builtFiles = @(Get-ChildItem -LiteralPath $binaryRoot -Recurse -File -Force)
    Assert-Condition ($archiveFiles.Count -eq $archiveRecords.Count -and $builtFiles.Count -eq $archiveRecords.Count) "$Role archived/built file count differs."
    foreach ($entry in @([pscustomobject]@{Root=$archiveRoot;Files=$archiveFiles},[pscustomobject]@{Root=$binaryRoot;Files=$builtFiles})) {
        foreach ($file in $entry.Files) {
            $relative = [IO.Path]::GetRelativePath($entry.Root, $file.FullName).Replace('\', '/')
            Assert-Condition ($archiveByPath.ContainsKey($relative)) "Unlisted $Role archived/built path: $relative"
        }
    }
    Assert-Condition ($archiveByPath.ContainsKey('FancyWM.dll') -and $archiveByPath.ContainsKey('FancyWM.Tests.dll')) "$Role production/fixture assembly missing."
    $provenancePath = Join-Path $SnapshotRoot 'release-test-binaries.provenance.json'
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json -DateKind String
    $expectedRed = $Role -ceq 'Baseline'
    $expectedExit = if ($expectedRed) { 1 } else { 0 }
    $roleName = $Role.ToLowerInvariant()
    Assert-Condition ($provenance.Snapshot -ceq $Id -and $provenance.Role -ceq $Role -and
        $provenance.SnapshotSHA256 -ceq $Snapshot.MetadataSHA256 -and
        $provenance.BuildSucceeded -is [bool] -and $provenance.BuildSucceeded -and
        $provenance.SourceCopyVerified -is [bool] -and $provenance.SourceCopyVerified -and
        $provenance.FrozenSourceVerifiedAfterBuild -is [bool] -and $provenance.FrozenSourceVerifiedAfterBuild -and
        $provenance.BuildWorkspace -ceq 'build-workspace' -and
        $provenance.Configuration -ceq 'Release' -and $provenance.TargetFramework -ceq $targetFramework -and
        $provenance.Architecture -ceq 'x64 vstest process; AnyCPU managed build' -and
        $provenance.RegressionExitCode -eq $expectedExit -and
        $provenance.SnapshotManifestSHA256 -ceq $Snapshot.ManifestSHA256 -and
        $provenance.ArchiveManifestSHA256 -ceq (Get-Sha256 $archiveManifestPath) -and
        $provenance.FancyWMDllSHA256 -ceq $archiveByPath['FancyWM.dll'].Hash -and
        $provenance.FancyWMTestsDllSHA256 -ceq $archiveByPath['FancyWM.Tests.dll'].Hash -and
        $provenance.CopiedFileCount -eq $archiveFiles.Count -and $provenance.CopiedBytes -eq $archiveBytes) "$Role build/archive provenance differs."
    $provenanceSources = @{
        ProductionSource = 'FancyWM/Utilities/FocusHelper.cs'
        FixtureSource = 'FancyWM.Tests/Utilities/FocusHelperTest.WorkerAdmission.cs'
        PrepareSource = 'scripts/performance/Prepare-FocusWorkerSnapshot.ps1'
        RunnerSource = 'scripts/performance/Measure-FocusWorkerAdmission.ps1'
    }
    foreach ($field in $provenanceSources.Keys) {
        Assert-Condition ($provenance.$field -ceq $provenanceSources[$field] -and
            $provenance.($field + 'SHA256') -ceq $Snapshot.ByPath[$provenanceSources[$field]].SHA256) "$Role provenance source differs: $field"
    }
    $expectedPaths = @{
        Commands = "validation/$roleName-prepare-commands.json"
        Environment = "validation/$roleName-dotnet-info.log"
        RestoreLog = "validation/$roleName-release-restore.log"
        BuildLog = "validation/$roleName-release-build.log"
        RegressionTrx = "validation/$roleName-focus-worker-regression.trx"
        RegressionLog = "validation/$roleName-focus-worker-regression.log"
    }
    foreach ($field in $expectedPaths.Keys) {
        Assert-Condition ($provenance.$field -ceq $expectedPaths[$field] -and
            (Get-Sha256 (Get-SafeRelativePath $SnapshotRoot $provenance.$field)) -ceq $provenance.($field + 'SHA256')) "$Role provenance evidence differs: $field"
    }
    $regressionMethods = @('RepeatedCompletedWorkerAdmissionsPreserveSemantics','RepeatedCompletedWorkerAdmissionsStayWithinCallerAllocationBudget','CompletedWorkerStartDelegateDoesNotRootRequestSequence','FocusWorkerAdmissionCounterScenario')
    $expectedFilter = ($regressionMethods | ForEach-Object { "FullyQualifiedName=$testClass.$_" }) -join '|'
    Assert-Condition ($provenance.RegressionFilter -ceq $expectedFilter) "$Role regression filter differs."
    $assemblyPath = Join-Path $binaryRoot 'FancyWM.Tests.dll'
    $commands = Get-Content -LiteralPath (Get-SafeRelativePath $SnapshotRoot $provenance.Commands) -Raw | ConvertFrom-Json -DateKind String
    $projectPath = Join-Path $buildRoot 'FancyWM.Tests/FancyWM.Tests.csproj'
    $expectedRestore = @('restore',$projectPath,'--disable-parallel')
    $expectedBuild = @('build',$projectPath,'--configuration','Release','--no-restore','--no-incremental')
    $expectedTest = @('vstest',$assemblyPath,'/Platform:x64',"/TestCaseFilter:$expectedFilter",
        "/Logger:trx;LogFileName=$roleName-focus-worker-regression.trx","/ResultsDirectory:$(Join-Path $SnapshotRoot 'validation')")
    Assert-Condition (($commands.PSObject.Properties.Name -join '|') -ceq 'Executable|WorkingDirectory|Restore|Build|Test|TestEnvironment' -and
        $commands.Executable -ceq 'dotnet' -and $commands.WorkingDirectory -ceq $buildRoot -and
        ($commands.Restore -join '|') -ceq ($expectedRestore -join '|') -and
        ($commands.Build -join '|') -ceq ($expectedBuild -join '|') -and
        ($commands.Test -join '|') -ceq ($expectedTest -join '|') -and
        ($commands.TestEnvironment.PSObject.Properties.Name -join '|') -ceq 'DOTNET_TieredCompilation' -and
        $commands.TestEnvironment.DOTNET_TieredCompilation -ceq '0') "$Role isolated preparation commands differ."
    $regression = Test-FocusWorkerRegressionTrx (Get-SafeRelativePath $SnapshotRoot $provenance.RegressionTrx) $expectedRed $assemblyPath
    Assert-Condition ($provenance.Regression.Leaves -eq $regression.Leaves -and
        $provenance.Regression.RawResults -eq $regression.RawResults -and
        $provenance.Regression.Passed -eq $regression.Passed -and $provenance.Regression.Failed -eq $regression.Failed -and
        $provenance.Regression.RunId -ceq $regression.RunId -and
        ($provenance.Regression.LeafNames -join '|') -ceq ($regression.LeafNames -join '|')) "$Role derived regression provenance differs."
    $started = [DateTimeOffset]::Parse($provenance.Started, [Globalization.CultureInfo]::InvariantCulture)
    $finished = [DateTimeOffset]::Parse($provenance.Finished, [Globalization.CultureInfo]::InvariantCulture)
    Assert-Condition ($started -le $regression.Start -and $regression.Finish -le $finished -and $started -lt $finished) "$Role regression escapes preparation interval."
    [pscustomobject]@{
        Root = $archiveRoot; Records = $archiveRecords; ByPath = $archiveByPath; Files = $archiveFiles.Count; Bytes = $archiveBytes
        ManifestPath = $archiveManifestPath; ManifestSHA256 = Get-Sha256 $archiveManifestPath
        Provenance = $provenance; ProvenancePath = $provenancePath; ProvenanceSHA256 = Get-Sha256 $provenancePath
        Regression = $regression
    }
}
$baselineArchive = Test-PreparedWorkerSnapshot $baselineRoot $BaselineSnapshotId 'Baseline' $baseline
$candidateArchive = Test-PreparedWorkerSnapshot $candidateRoot $CandidateSnapshotId 'Candidate' $candidate
$provenance = $baselineArchive.Provenance
$provenancePath = $baselineArchive.ProvenancePath
$archiveManifestPath = $baselineArchive.ManifestPath
$redResult = $baselineArchive.Regression
$redPassed = $redResult.Passed
$redFailed = $redResult.Failed
Assert-Condition ($redPassed -eq 5 -and $redFailed -eq 3 -and $candidateArchive.Regression.Passed -eq 8 -and $candidateArchive.Regression.Failed -eq 0) 'Prepared regression counts differ.'
$candidateRegressions = @(foreach ($configuration in @('Debug','Release')) {
    $trxPath = Join-Path $candidateRoot "validation/$configuration-focus-worker-targeted.trx"
    $logPath = [IO.Path]::ChangeExtension($trxPath, '.log')
    $result = Test-FocusWorkerRegressionTrx $trxPath $false (Join-Path $repositoryRoot "FancyWM.Tests/bin/$configuration/$targetFramework/FancyWM.Tests.dll")
    Assert-Condition (($result.LeafNames -join '|') -ceq ($redResult.LeafNames -join '|')) "Candidate regression leaf set differs: $configuration"
    [pscustomobject]@{ Configuration = $configuration; Leaves = $result.Leaves; Passed = $result.Passed; Failed = $result.Failed; TrxSHA256 = Get-Sha256 $trxPath; LogSHA256 = Get-Sha256 $logPath }
})

# Every process gets its own complete candidate dependency/fixture tree.
# The baseline production assembly is the only permitted binary replacement.
$expectedRunOrder = @('baseline-1','candidate-1','candidate-2','baseline-2','baseline-3','candidate-3','candidate-4','baseline-4','baseline-5','candidate-5')
$binaryChecksPath = Join-Path $experimentRoot 'binary-checks.csv'
$binaryChecks = @(Import-Csv -LiteralPath $binaryChecksPath)
Assert-Schema $binaryChecks 'RunId|Variant|Pair|Path|CommonCandidateSHA256|ExpectedSHA256|BeforeSHA256|AfterSHA256|Length' 'experiment binary checks'
Assert-Condition ($binaryChecks.Count -eq 10 * $candidateArchive.Files) 'Binary-check matrix dimensions differ.'
$productionHashes = @{ baseline = $baselineArchive.Provenance.FancyWMDllSHA256; candidate = $candidateArchive.Provenance.FancyWMDllSHA256 }
$fixtureHash = $candidateArchive.Provenance.FancyWMTestsDllSHA256
Assert-Condition ($productionHashes.baseline -cne $productionHashes.candidate) 'Baseline/candidate production assemblies must differ.'
$binaryKeys = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
$binaryRoots = @{}
$binaryManifestHashes = @{}
$binaryDifferenceSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($record in $binaryChecks) {
    Assert-Condition ($record.RunId -cin $expectedRunOrder -and $record.Variant -cin @('baseline','candidate') -and
        $record.Pair -cmatch '^[1-5]$' -and $record.RunId -ceq "$($record.Variant)-$($record.Pair)" -and
        $candidateArchive.ByPath.ContainsKey($record.Path) -and $binaryKeys.TryAdd("$($record.RunId)/$($record.Path)",$record)) 'Unexpected/duplicate binary-check key.'
    $common = $candidateArchive.ByPath[$record.Path]
    $expected = if ($record.Path -ceq 'FancyWM.dll') { $productionHashes[$record.Variant] } else { $common.Hash }
    $length = if ($record.Path -ceq 'FancyWM.dll' -and $record.Variant -ceq 'baseline') { $baselineArchive.ByPath['FancyWM.dll'].Length } else { $common.Length }
    $binaryRoot = Join-Path $experimentRoot "runs/$($record.RunId)/binaries"
    $path = Get-SafeRelativePath $binaryRoot $record.Path
    Assert-Condition ($record.CommonCandidateSHA256 -ceq $common.Hash -and $record.ExpectedSHA256 -ceq $expected -and
        $record.BeforeSHA256 -ceq $expected -and $record.AfterSHA256 -ceq $expected -and
        (Read-NonnegativeInteger $record.Length 'binary-check length') -eq [long]$length -and
        (Get-Sha256 $path) -ceq $expected -and (Get-Item -LiteralPath $path).Length -eq [long]$length) "Run binary differs: $($record.RunId)/$($record.Path)"
    if ($record.CommonCandidateSHA256 -cne $expected) { [void]$binaryDifferenceSet.Add($record.Path) }
}
foreach ($runName in $expectedRunOrder) {
    $binaryRoot = Join-Path $experimentRoot "runs/$runName/binaries"
    $binaryRoots[$runName] = $binaryRoot
    $manifestPath = Join-Path $experimentRoot "runs/$runName/binaries.csv"
    $manifest = @(Import-Csv -LiteralPath $manifestPath)
    Assert-Schema $manifest 'Path|Hash|Length' "$runName binary manifest"
    $files = @(Get-ChildItem -LiteralPath $binaryRoot -Recurse -File -Force)
    Assert-Condition ($manifest.Count -eq $candidateArchive.Files -and $files.Count -eq $candidateArchive.Files) "$runName binary tree dimensions differ."
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($record in $manifest) {
        Assert-Condition ($seen.Add($record.Path) -and $binaryKeys.ContainsKey("$runName/$($record.Path)")) "$runName manifest path is extra/duplicated."
        $check = $binaryKeys["$runName/$($record.Path)"]
        Assert-Condition ($record.Hash -ceq $check.ExpectedSHA256 -and $record.Length -ceq $check.Length) "$runName binary manifest differs from actual checked bytes."
    }
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($binaryRoot,$file.FullName).Replace('\','/')
        Assert-Condition ($seen.Contains($relative)) "Unlisted $runName binary file: $relative"
    }
    $binaryManifestHashes[$runName] = Get-Sha256 $manifestPath
}
$binaryDifferences = @($binaryDifferenceSet)
Assert-Condition ($binaryDifferences.Count -eq 1 -and $binaryDifferences[0] -ceq 'FancyWM.dll') 'Only FancyWM.dll may differ in common measurement binaries.'

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
$processIds = [Collections.Generic.HashSet[int]]::new()
$rawCounters = [Collections.Generic.Dictionary[string, long]]::new([StringComparer]::Ordinal)
$processes = @()
$counterPattern = '^PERFCOUNTER focus-worker-(direct-success|alt-retry|attach-failure) (' + ($metrics -join '|') + ') (0|[1-9][0-9]*)$'
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
    $assemblyPath = Join-Path $binaryRoots[$runName] 'FancyWM.Tests.dll'
    Assert-AssemblyPath $method.codeBase $assemblyPath "$runName codeBase"
    Assert-AssemblyPath $definition.storage $assemblyPath "$runName storage"
    $start = [DateTimeOffset]::Parse($times.start, [Globalization.CultureInfo]::InvariantCulture)
    $finish = [DateTimeOffset]::Parse($times.finish, [Globalization.CultureInfo]::InvariantCulture)
    $testStart = [DateTimeOffset]::Parse($result.startTime, [Globalization.CultureInfo]::InvariantCulture)
    $testFinish = [DateTimeOffset]::Parse($result.endTime, [Globalization.CultureInfo]::InvariantCulture)
    Assert-Condition ($start -lt $finish -and $start -le $testStart -and
        $testStart -lt $testFinish -and $testFinish -le $finish) "Invalid TRX process/test interval: $runName"
    $processPath = Join-Path $experimentRoot "$runName.process.json"
    $processInfo = Get-Content -LiteralPath $processPath -Raw | ConvertFrom-Json -DateKind String
    $processStart = [DateTimeOffset]::Parse($processInfo.Start,[Globalization.CultureInfo]::InvariantCulture)
    $processFinish = [DateTimeOffset]::Parse($processInfo.Finish,[Globalization.CultureInfo]::InvariantCulture)
    Assert-Condition (($processInfo.PSObject.Properties.Name -join '|') -ceq 'RunId|ProcessId|Start|Finish|ExitCode|TimedOut' -and
        $processInfo.RunId -ceq $runName -and $processInfo.ProcessId -gt 0 -and $processIds.Add([int]$processInfo.ProcessId) -and
        $processInfo.ExitCode -eq 0 -and $processInfo.TimedOut -is [bool] -and !$processInfo.TimedOut -and
        $processStart -le $start -and $finish -le $processFinish -and $processStart -lt $processFinish) "Invalid isolated process record: $runName"
    if ($processes.Count -gt 0) {
        Assert-Condition ($processes[-1].Finish -le $processStart) "A/B process order overlaps or differs at $runName."
    }
    $commandPath = Join-Path $experimentRoot "$runName.command.json"
    $command = Get-Content -LiteralPath $commandPath -Raw | ConvertFrom-Json -DateKind String
    $expectedArguments = @('vstest',$assemblyPath,'/Platform:x64',"/TestCaseFilter:FullyQualifiedName=$testFqn",
        "/Logger:trx;LogFileName=$runName.trx","/ResultsDirectory:$experimentRoot")
    Assert-Condition (($command.PSObject.Properties.Name -join '|') -ceq 'Executable|Arguments|WorkingDirectory|Environment' -and
        [IO.Path]::IsPathRooted($command.Executable) -and [IO.Path]::GetFileName($command.Executable) -ieq 'dotnet.exe' -and
        $command.WorkingDirectory -ceq $binaryRoots[$runName] -and
        ($command.Arguments -join '|') -ceq ($expectedArguments -join '|') -and
        ($command.Environment.PSObject.Properties.Name -join '|') -ceq 'DOTNET_TieredCompilation' -and
        $command.Environment.DOTNET_TieredCompilation -ceq '0') "Isolated Release x64 command differs: $runName"
    $stdout = Get-SingleNode $result './*[local-name()="Output"]/*[local-name()="StdOut"]' "$runName stdout"
    Assert-Condition (@($root.SelectNodes('//*[local-name()="StdOut"]')).Count -eq 1) "Extra TRX stdout: $runName"
    $rawLines = @($stdout.InnerText -split '\r?\n' | Where-Object { $_.Length -gt 0 })
    Assert-Condition ($rawLines.Count -eq 57) "Raw Console counter envelope differs: $runName"
    $counterLines = $rawLines
    Assert-Condition ($counterLines.Count -eq 57) "Raw counter line count differs: $runName"
    foreach ($line in $counterLines) {
        $match = [regex]::Match($line, $counterPattern)
        Assert-Condition ($match.Success) "Unexpected raw counter: $runName/$line"
        $key = "$variant/$pair/$($match.Groups[1].Value)/$($match.Groups[2].Value)"
        $value = Read-NonnegativeInteger $match.Groups[3].Value "raw/$key"
        Assert-Condition ($rawCounters.TryAdd($key, $value)) "Duplicate raw counter: $key"
    }
    $processes += [pscustomobject]@{
        Run = $runName; Variant = $variant; Pair = [int]$pair; ProcessId = [int]$processInfo.ProcessId
        RunId = $runGuid; ExecutionId = $executionGuid; TestId = $testGuid
        Start = $processStart; Finish = $processFinish; TrxStart = $start; TrxFinish = $finish
        TestStart = $testStart; TestFinish = $testFinish
        TrxSHA256 = Get-Sha256 $trxPath
        StdOutSHA256 = Get-Sha256 (Join-Path $experimentRoot "$runName.stdout.log")
        StdErrSHA256 = Get-Sha256 (Join-Path $experimentRoot "$runName.stderr.log")
        CommandSHA256 = Get-Sha256 $commandPath; ProcessSHA256 = Get-Sha256 $processPath
        BinaryManifestSHA256 = $binaryManifestHashes[$runName]
    }
}
Assert-Condition ($rawCounters.Count -eq 570) 'Raw counter matrix is incomplete.'

# These strings are part of the runner protocol, not free-form acceptance claims.
$expectedConfiguration = "Release; $targetFramework; x64; no debugger; DOTNET_TieredCompilation=0"
$expectedInstrument = 'Actual production FocusHelper.RequestSequence.Enqueue with one real completed background Thread per admission and a counted fake native adapter;32 discarded warmups then500 admissions;caller-thread GC.GetAllocatedBytesForCurrentThread;Stopwatch includes Thread.Start/Join and scheduling;common candidate fixture and dependencies, only archived FancyWM.dll differs;native focus,COM,WPF,DPI,whole-process CPU/GPU and native presentation latency NOT_MEASURED;timing descriptive without required timing win'
$measurementsPath = Join-Path $experimentRoot 'measurements.csv'
$rows = @(Import-Csv -LiteralPath $measurementsPath)
Assert-Schema $rows 'experiment_id|snapshot_id|scenario|configuration|window_count|metric|unit|run|value|iterations|instrument|perf_id|variant|pair|binary_sha256|benchmark_sha256' 'measurements.csv'
Assert-Condition ($rows.Count -eq 570) "Measurement row count differs: $($rows.Count)"
$rowKeys = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$frequencies = [Collections.Generic.HashSet[long]]::new()
foreach ($row in $rows) {
    $scenario = [regex]::Match($row.scenario,
        '^Actual FocusHelper\.RequestSequence\.Enqueue; focus-worker-(direct-success|alt-retry|attach-failure);32 discarded warmups then500 completed worker admissions$')
    Assert-Condition ($scenario.Success) "Measurement scenario differs: $($row.scenario)"
    $caseName = $scenario.Groups[1].Value
    Assert-Condition ($row.variant -cin @('baseline', 'candidate') -and
        $row.run -cmatch '^[1-5]$' -and $row.pair -ceq $row.run -and
        $row.window_count -ceq '0' -and $row.metric -cin $metrics -and
        $row.iterations -ceq '500' -and $row.perf_id -ceq 'PERF-030' -and
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
        { $_ -cin @('admissions','current-results','worker-starts','worker-exits','background-workers',
            'queue-calls','thread-id-reads','foreground-id-reads','attach-calls') } { $calls }
        { $_ -cin @('join-timeouts','wait-calls','owned-attachments') } { 0L }
        { $_ -cin @('true-results','detach-calls') } { if ($caseName -ceq 'attach-failure') { 0L } else { $calls } }
        'focus-calls' { if ($caseName -ceq 'attach-failure') { 0L } elseif ($caseName -ceq 'alt-retry') { $calls * 2L } else { $calls } }
        'alt-presses' { if ($caseName -ceq 'alt-retry') { $calls } else { 0L } }
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
        Assert-Condition ($baselineBytes -eq 184000 -and $candidateBytes -eq 152000 -and $candidateBytes -lt $baselineBytes) "Expected caller allocation improvement 368 ->304 B/call differs: $pair/$caseName"
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

# Reconstruct the process table from actual commands, process records and TRX.
$processTimesPath = Join-Path $experimentRoot 'process-times.csv'
$expectedProcessRows = @($processes | ForEach-Object {
    [pscustomobject]@{
        RunId = $_.Run; Variant = $_.Variant; Pair = $_.Pair; ProcessId = $_.ProcessId; ExitCode = 0
        Start = $_.Start.ToString('o'); Finish = $_.Finish.ToString('o')
        TrxStart = $_.TrxStart.ToString('o'); TrxFinish = $_.TrxFinish.ToString('o')
        TrxId = $_.RunId.ToString('D'); ExecutionId = $_.ExecutionId.ToString('D')
        TrxSHA256 = $_.TrxSHA256; StdOutSHA256 = $_.StdOutSHA256; StdErrSHA256 = $_.StdErrSHA256
        CommandSHA256 = $_.CommandSHA256; BinaryManifestSHA256 = $_.BinaryManifestSHA256
        FancyWMDllSHA256 = $productionHashes[$_.Variant]; FancyWMTestsDllSHA256 = $fixtureHash
    }
})
Assert-Condition ($expectedProcessRows.Count -eq 10) 'Derived process matrix must have ten rows.'
Assert-DerivedCsv $processTimesPath 'RunId|Variant|Pair|ProcessId|ExitCode|Start|Finish|TrxStart|TrxFinish|TrxId|ExecutionId|TrxSHA256|StdOutSHA256|StdErrSHA256|CommandSHA256|BinaryManifestSHA256|FancyWMDllSHA256|FancyWMTestsDllSHA256' $expectedProcessRows

Assert-Condition ($semanticMetrics.Count -eq 16 -and $semanticComparisons -eq 240 -and $allocationSummary.Count -eq 15) 'Semantic/allocation comparison dimensions differ.'
$report = [ordered]@{
    Verdict = 'ACCEPT'; Accepted = $true; VerifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Scenario = 'FocusWorkerAdmission'; PerfId = 'PERF-030'
    BaselineSnapshot = $BaselineSnapshotId; CandidateSnapshot = $CandidateSnapshotId; ExperimentId = $ExperimentId
    SourceManifestFiles = $candidate.Records.Count; SourceManifestDifferences = $sourceDifferences
    ExactSourceReplacement = 'Add private ThreadStart? m_workerStart after m_worker; construct new Thread(m_workerStart ??= RunWorker) inside the existing locked worker-null branch'
    BaselineArchiveFiles = $baselineArchive.Files; BaselineArchiveBytes = $baselineArchive.Bytes
    CandidateArchiveFiles = $candidateArchive.Files; CandidateArchiveBytes = $candidateArchive.Bytes
    BaselineSemanticTestsPassed = $redPassed; BaselineWorkerAdmissionAllocationTestsFailed = $redFailed
    BaselineRegressionLeaves = $redResult.Leaves; CandidateRegressions = $candidateRegressions
    BinaryTreeFiles = $candidateArchive.Files; BinaryCheckRows = $binaryChecks.Count; BinaryDifferences = $binaryDifferences
    BaselineProductionSHA256 = $productionHashes.baseline; CandidateProductionSHA256 = $productionHashes.candidate
    ArchivedBaselineFixtureSHA256 = $provenance.FancyWMTestsDllSHA256
    CommonFixtureSHA256 = $fixtureHash; TestFqn = $testFqn
    ProcessCount = $processes.Count; ProcessOrder = @($processes.Run); Processes = $processes
    MeasurementRows = $rows.Count; RawCounters = $rawCounters.Count
    PairedSemanticComparisons = $semanticComparisons; AllocationPairs = 15; Allocation = $allocationSummary
    SemanticMetricCount = $semanticMetrics.Count; TimestampFrequency = $frequency; FrequencyInvariantComparisons = 29; TimingFormula = 'elapsed-ticks * 1e9 / timestamp-frequency / calls'
    TimingClassification = $(if ($timingWins -eq 15) { 'all measured pairs lower' } elseif ($timingWins -eq 0 -and $timingTies -eq 0) { 'all measured pairs higher' } else { 'mixed' })
    TimingAcceptanceGate = $false; PairedTimingWins = $timingWins; PairedTimingTies = $timingTies
    PairedTimingLosses = 15 - $timingWins - $timingTies; Timing = $timingSummary; TimingPairs = $timingPairs
    MeasurementsSHA256 = Get-Sha256 $measurementsPath; BinaryChecksSHA256 = Get-Sha256 $binaryChecksPath
    ProcessTimesRows = $expectedProcessRows.Count; ProcessTimesSHA256 = Get-Sha256 $processTimesPath
    BaselineManifestSHA256 = $baseline.ManifestSHA256; CandidateManifestSHA256 = $candidate.ManifestSHA256
    BaselineSnapshotSHA256 = $baseline.MetadataSHA256; CandidateSnapshotSHA256 = $candidate.MetadataSHA256
    BaselineArchiveManifestSHA256 = Get-Sha256 $archiveManifestPath
    BaselineProvenanceSHA256 = Get-Sha256 $provenancePath
    CandidateArchiveManifestSHA256 = $candidateArchive.ManifestSHA256; CandidateProvenanceSHA256 = $candidateArchive.ProvenanceSHA256
    CandidatePreparedRegressionLeaves = $candidateArchive.Regression.Leaves; CandidatePreparedRegressionPassed = $candidateArchive.Regression.Passed
    VerifierSHA256 = Get-Sha256 $PSCommandPath
    Limitations = 'Managed caller allocation for repeated sequential admitted workers after warmup, using real background threads with synchronous join and fake native adapters. The first lazy delegate allocation, cross-thread total allocations, production contention/scheduling, native focus/input/COM/WPF/DPI, timeout or late-focus effects, WPR, whole-process CPU/GPU, presentation latency and energy remain E2E_PENDING / NOT_MEASURED. Timing includes start/join and is descriptive.'
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $candidateRoot 'validation/focus-worker-strict-verification.json'
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
