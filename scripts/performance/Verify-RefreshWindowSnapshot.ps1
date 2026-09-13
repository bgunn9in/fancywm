[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId,
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

$rootOutput = & git -C $PSScriptRoot rev-parse --show-toplevel
Assert-Condition ($LASTEXITCODE -eq 0) 'Cannot resolve the repository root.'
$repositoryRoot = [IO.Path]::GetFullPath(($rootOutput -join '').Trim())
Assert-Condition (![StringComparer]::OrdinalIgnoreCase.Equals($SnapshotId, $BaselineSnapshotId)) `
    'Candidate and baseline snapshot IDs must differ.'
$artifactRoot = Join-Path $repositoryRoot 'artifacts/performance'
$candidateRoot = Get-SafeRelativePath $artifactRoot $SnapshotId
$baselineRoot = Get-SafeRelativePath $artifactRoot $BaselineSnapshotId
$experimentRoot = Get-SafeRelativePath $candidateRoot $ExperimentId
$targetFramework = 'net10.0-windows10.0.18362.0'
$testClass = 'FancyWM.Tests.AlgorithmicLayouts.TilingServiceAlgorithmicIntegrationTest'
$testMethod = 'RefreshWindowSnapshotCounterScenario'
$testFqn = "$testClass.$testMethod"
$windowCounts = @(0, 1, 10, 50)
$passes = 100000L
$candidateBytesPerPass = @{ 0 = 128L; 1 = 160L; 10 = 232L; 50 = 552L }
$units = [ordered]@{
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
$metrics = @($units.Keys)
$semanticMetrics = @($metrics | Where-Object {
    $_ -cnotin @('allocated-bytes', 'elapsed-ticks', 'timestamp-frequency')
})

# Verify every frozen source, including dependencies and the common harness.
# Live status documents can advance after the experiment without rewriting it.
$snapshots = @{}
foreach ($entry in @(
    [pscustomobject]@{ Id = $BaselineSnapshotId; Root = $baselineRoot },
    [pscustomobject]@{ Id = $SnapshotId; Root = $candidateRoot }
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
$candidate = $snapshots[$SnapshotId]
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
Assert-Condition ($sourceDifferences.Count -eq 1 -and $sourceDifferences[0] -ceq 'FancyWM/TilingService.cs') `
    "Unexpected source delta: $($sourceDifferences -join ', ')"
$requiredSources = @{
    ProductionSource = 'FancyWM/TilingService.cs'
    RegressionSource = 'FancyWM.Tests/AlgorithmicLayouts/TilingServiceAlgorithmicIntegrationTest.RefreshSnapshot.cs'
    FixtureSource = 'FancyWM.Tests/AlgorithmicLayouts/TilingServiceAlgorithmicIntegrationTest.Manageability.cs'
    RunnerSource = 'scripts/performance/Measure-MicaRefresh.ps1'
    ArchiveScriptSource = 'scripts/performance/Archive-RefreshWindowSnapshotBaseline.ps1'
}
foreach ($path in @($requiredSources.Values) + @('scripts/performance/Verify-RefreshWindowSnapshot.ps1')) {
    Assert-Condition ($candidate.ByPath.ContainsKey($path)) "Required common source missing: $path"
}
$oldSource = [IO.File]::ReadAllText((Join-Path $baselineRoot 'source/FancyWM/TilingService.cs'))
$newSource = [IO.File]::ReadAllText((Join-Path $candidateRoot 'source/FancyWM/TilingService.cs'))
$declarations = [regex]::Matches($oldSource,
    '(?m)^(?<prefix>[ \t]*public void Refresh\(\)\r?\n[ \t]*\{\r?\n[ \t]*if \(m_disposed \|\| m_shutdownPreparation != null\) \{ return; \}\r?\n[ \t]*)List<IWindow>(?<suffix> windows;\r?$)')
Assert-Condition ($declarations.Count -eq 1) 'Baseline Refresh owned List snapshot declaration differs.'
$typeOffset = $declarations[0].Index + $declarations[0].Groups['prefix'].Length
$expectedSource = $oldSource.Substring(0, $typeOffset) + 'IWindow[]' +
    $oldSource.Substring($typeOffset + 'List<IWindow>'.Length)
Assert-Condition ($newSource -ceq $expectedSource) 'Source delta exceeds the Refresh snapshot type replacement.'

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
    $provenance.SemanticLeavesPassed -eq 4 -and $provenance.AllocationLeavesFailed -eq 4 -and
    ($provenance.BaselineBytesPerPass -join ',') -ceq '160,192,264,584' -and
    ($provenance.CandidateBudgetsBytesPerPass -join ',') -ceq '128,160,232,552') `
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
$redTrx = Read-Trx (Get-SafeRelativePath $baselineRoot $provenance.RedTrx)
$redLeaves = @($redTrx.SelectNodes('//*[local-name()="UnitTestResult" and not(*[local-name()="InnerResults"])]'))
Assert-Condition ($redLeaves.Count -eq 8) 'Baseline red TRX must contain exactly eight result leaves.'
$redNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$redPassed = 0
$redFailed = 0
foreach ($leaf in $redLeaves) {
    Assert-Condition ($redNames.Add($leaf.testName)) "Duplicate baseline red leaf: $($leaf.testName)"
    if ($leaf.testName -cmatch '^Refresh(UsesStableWindowSnapshotWhenStateReadMutatesTrackedWindows|PreservesMembershipProbeOrderOutsideLocks) \((False|True)\)$') {
        Assert-Condition ($leaf.outcome -ceq 'Passed') "Baseline semantic regression failed: $($leaf.testName)"
        $redPassed++
        continue
    }
    $allocationCase = [regex]::Match($leaf.testName,
        '^RefreshWindowSnapshotAvoidsListWrapperAllocation \((0|1|10|50),(128|160|232|552)\)$')
    Assert-Condition ($allocationCase.Success -and $leaf.outcome -ceq 'Failed') `
        "Unexpected baseline red result: $($leaf.testName)"
    $windowCount = [int]$allocationCase.Groups[1].Value
    $budget = $candidateBytesPerPass[$windowCount]
    Assert-Condition ([long]$allocationCase.Groups[2].Value -eq $budget) 'Baseline red allocation budget differs.'
    $message = Get-SingleNode $leaf './*[local-name()="Output"]/*[local-name()="ErrorInfo"]/*[local-name()="Message"]' 'red allocation failure'
    $expectedBytes = 1000L * ($budget + 32L)
    Assert-Condition ($message.InnerText -cmatch
        "^Assert\.IsTrue failed\. Refresh allocated $expectedBytes bytes for 1000 passes with $windowCount tracked windows \([^\r\n]+ B/pass\), above the $budget B/pass budget\.$") `
        "Baseline red failure is not the expected +32 B/pass: $($leaf.testName)"
    $redFailed++
}
Assert-Condition ($redPassed -eq 4 -and $redFailed -eq 4) 'Baseline red leaf outcomes differ.'

# Every path in both experiment binary trees must be checked exactly once.
$binaryChecksPath = Join-Path $experimentRoot 'refresh-window-snapshot-binary-checks.csv'
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
$counterPattern = '^PERFCOUNTER refresh-window-snapshot-(0|1|10|50) (' + ($metrics -join '|') + ') (0|[1-9][0-9]*)$'
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
    Assert-Condition ($rawLines.Count -eq 48) "Raw counter line count differs: $runName"
    foreach ($line in $rawLines) {
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
Assert-Condition ($rawCounters.Count -eq 480) 'Raw counter matrix is incomplete.'

# These strings are part of the runner protocol, not free-form acceptance claims.
$expectedConfiguration = "Release; $targetFramework; inherited isolated-process architecture; no debugger; DOTNET_TieredCompilation=0"
$expectedInstrument = 'Actual public TilingService.Refresh with its owned HashSet<IWindow> pass snapshot;field-backed IWorkspace/IWindow/IDisplay/IVirtualDesktop adapters and real Information-level Serilog logger;0/1/10/50 unique tracked restored windows lie inside the managed display and are rejected at CanResize;1000 discarded warmups then100000 complete unchanged refresh passes;all measured property/provider reads and tracked-window input counts are checked;GC.GetAllocatedBytesForCurrentThread and Stopwatch with per-process timestamp frequency;common final fixture and dependencies with only separately archived hashed baseline FancyWM.dll differing;snapshot isolation and complete membership reconciliation remain production behavior;no native window/COM calls,provider discovery,wall-clock polling latency,whole-process CPU/GPU/energy or idle-wakeup measurement;timing is recorded without a required timing win'
$measurementsPath = Join-Path $experimentRoot 'measurements.csv'
$rows = @(Import-Csv -LiteralPath $measurementsPath)
Assert-Schema $rows 'experiment_id|snapshot_id|scenario|configuration|window_count|metric|unit|run|value|iterations|instrument|perf_id|variant|pair|binary_sha256|benchmark_sha256' 'measurements.csv'
Assert-Condition ($rows.Count -eq 480) "Measurement row count differs: $($rows.Count)"
$rowKeys = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$frequencies = [Collections.Generic.HashSet[long]]::new()
foreach ($row in $rows) {
    $scenario = [regex]::Match($row.scenario,
        '^Actual TilingService\.Refresh owned pass snapshot; refresh-window-snapshot-(0|1|10|50) tracked restored windows rejected by CanResize;1000 discarded warmups then100000 complete unchanged passes$')
    Assert-Condition ($scenario.Success) "Measurement scenario differs: $($row.scenario)"
    $windowCount = [int]$scenario.Groups[1].Value
    Assert-Condition ($row.variant -cin @('baseline', 'candidate') -and
        $row.run -cmatch '^[1-5]$' -and $row.pair -ceq $row.run -and
        $row.window_count -ceq [string]$windowCount -and $row.metric -cin $metrics -and
        $row.iterations -ceq '100000' -and $row.perf_id -ceq 'PERF-002' -and
        $row.experiment_id -ceq $ExperimentId -and $row.configuration -ceq $expectedConfiguration -and
        $row.instrument -ceq $expectedInstrument -and $row.benchmark_sha256 -ceq $fixtureHash) `
        "Measurement dimensions differ: $($row.variant)/$($row.run)/$windowCount/$($row.metric)"
    $expectedSnapshot = if ($row.variant -ceq 'baseline') { $BaselineSnapshotId } else { $SnapshotId }
    Assert-Condition ($row.snapshot_id -ceq $expectedSnapshot -and
        $row.binary_sha256 -ceq $productionHashes[$row.variant] -and $row.unit -ceq $units[$row.metric]) `
        "Measurement provenance/unit differs: $($row.variant)/$($row.run)/$windowCount/$($row.metric)"
    $key = "$($row.variant)/$($row.run)/$windowCount/$($row.metric)"
    Assert-Condition ($rowKeys.TryAdd($key, $row)) "Duplicate measurement row: $key"
    $value = Read-NonnegativeInteger $row.value $key
    Assert-Condition ($rawCounters.ContainsKey($key) -and $rawCounters[$key] -eq $value) "CSV differs from raw TRX: $key"
    $expected = switch -CaseSensitive ($row.metric) {
        'allocated-bytes' {
            $bytes = $candidateBytesPerPass[$windowCount]
            if ($row.variant -ceq 'baseline') { $bytes += 32L }
            $passes * $bytes
        }
        'passes' { $passes }
        'tracked-windows' { [long]$windowCount }
        { $_ -cin @('state-reads', 'position-reads', 'resize-reads') } { $passes * $windowCount }
        { $_ -cin @('move-reads', 'pin-reads', 'desktop-snapshot-reads', 'display-snapshot-reads') } { 0L }
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
foreach ($windowCount in $windowCounts) {
    $baselineNanoseconds = @()
    $candidateNanoseconds = @()
    foreach ($pair in 1..5) {
        foreach ($metric in $metrics) {
            foreach ($variant in @('baseline', 'candidate')) {
                Assert-Condition ($rowKeys.ContainsKey("$variant/$pair/$windowCount/$metric")) `
                    "Incomplete measurement matrix: $variant/$pair/$windowCount/$metric"
            }
        }
        foreach ($metric in $semanticMetrics) {
            Assert-Condition ($rawCounters["baseline/$pair/$windowCount/$metric"] -eq
                $rawCounters["candidate/$pair/$windowCount/$metric"]) `
                "Paired semantic counter differs: $pair/$windowCount/$metric"
            $semanticComparisons++
        }
        Assert-Condition ($rawCounters["baseline/$pair/$windowCount/allocated-bytes"] -
            $rawCounters["candidate/$pair/$windowCount/allocated-bytes"] -eq 3200000L) `
            "Paired allocation delta differs: $pair/$windowCount"
        $baselineTicks = $rawCounters["baseline/$pair/$windowCount/elapsed-ticks"]
        $candidateTicks = $rawCounters["candidate/$pair/$windowCount/elapsed-ticks"]
        # Normalize each sample using its recorded frequency and measured passes.
        # Stopwatch ticks are not TimeSpan ticks; there is no fixed tick-to-ns divisor.
        $baselineNs = [double]$baselineTicks * 1e9 / [double]$frequency / [double]$passes
        $candidateNs = [double]$candidateTicks * 1e9 / [double]$frequency / [double]$passes
        $baselineNanoseconds += $baselineNs
        $candidateNanoseconds += $candidateNs
        if ($candidateTicks -lt $baselineTicks) { $timingWins++ }
        elseif ($candidateTicks -eq $baselineTicks) { $timingTies++ }
        $timingPairs += [pscustomobject]@{
            WindowCount = $windowCount; Pair = $pair
            BaselineNanosecondsPerPass = $baselineNs; CandidateNanosecondsPerPass = $candidateNs
            DeltaPercent = ($candidateNs - $baselineNs) / $baselineNs * 100.0
        }
    }
    $candidateBytes = $candidateBytesPerPass[$windowCount]
    $allocationSummary += [pscustomobject]@{
        WindowCount = $windowCount; BaselineBytesPerPass = $candidateBytes + 32L
        CandidateBytesPerPass = $candidateBytes; SavedBytesPerPass = 32L; AcceptedPairs = 5
    }
    $baselineMedian = Get-Median $baselineNanoseconds
    $candidateMedian = Get-Median $candidateNanoseconds
    $timingSummary += [pscustomobject]@{
        WindowCount = $windowCount; BaselineMedianNanosecondsPerPass = $baselineMedian
        CandidateMedianNanosecondsPerPass = $candidateMedian
        MedianDeltaPercent = 100.0 * ($candidateMedian - $baselineMedian) / $baselineMedian
    }
}

# These are derived summaries, not independent timing evidence. In particular,
# process CSV date formatting omits sub-second precision; the exact original
# TRX intervals above remain the authority for ordering and overlap checks.
$processTimesPath = Join-Path $experimentRoot 'refresh-window-snapshot-process-times.csv'
$expectedProcessRows = @($processes | ForEach-Object {
    [pscustomobject]@{
        Run = $_.Run; TrxId = $_.RunId.ToString('D'); ExecutionId = $_.ExecutionId.ToString('D')
        Start = $_.Start; Finish = $_.Finish; TrxSHA256 = $_.TrxSHA256
    }
})
Assert-Condition ($expectedProcessRows.Count -eq 10) 'Derived process matrix must have ten rows.'
Assert-DerivedCsv $processTimesPath 'Run|TrxId|ExecutionId|Start|Finish|TrxSHA256' $expectedProcessRows

$timingCsvPath = Join-Path $experimentRoot 'refresh-window-snapshot-timing.csv'
$expectedTimingRows = @($timingPairs | Sort-Object Pair, WindowCount | ForEach-Object {
    [pscustomobject]@{
        Run = $_.Pair; WindowCount = $_.WindowCount
        BaselineNanosecondsPerPass = $_.BaselineNanosecondsPerPass
        CandidateNanosecondsPerPass = $_.CandidateNanosecondsPerPass
        DeltaPercent = $_.DeltaPercent
        TimingImproved = $_.CandidateNanosecondsPerPass -lt $_.BaselineNanosecondsPerPass
    }
})
Assert-Condition ($expectedTimingRows.Count -eq 20) 'Derived timing matrix must have twenty rows.'
Assert-DerivedCsv $timingCsvPath `
    'Run|WindowCount|BaselineNanosecondsPerPass|CandidateNanosecondsPerPass|DeltaPercent|TimingImproved' `
    $expectedTimingRows

$report = [ordered]@{
    Verdict = 'ACCEPT'; Accepted = $true; VerifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Scenario = 'RefreshWindowSnapshot'; PerfId = 'PERF-002'
    BaselineSnapshot = $BaselineSnapshotId; CandidateSnapshot = $SnapshotId; ExperimentId = $ExperimentId
    SourceManifestFiles = $candidate.Records.Count; SourceManifestDifferences = $sourceDifferences
    ExactSourceReplacement = 'TilingService.Refresh owned List<IWindow> -> IWindow[]'
    BaselineArchiveFiles = $archiveFiles.Count; BaselineArchiveBytes = $archiveBytes
    BaselineSemanticLeavesPassed = $redPassed; BaselineAllocationLeavesFailed = $redFailed
    BinaryTreeFiles = $binaryChecks.Count; BinaryDifferences = $binaryDifferences
    BaselineProductionSHA256 = $productionHashes.baseline; CandidateProductionSHA256 = $productionHashes.candidate
    ArchivedBaselineFixtureSHA256 = $provenance.FancyWMTestsDllSHA256
    CommonFixtureSHA256 = $fixtureHash; TestFqn = $testFqn
    ProcessCount = $processes.Count; ProcessOrder = @($processes.Run); Processes = $processes
    MeasurementRows = $rows.Count; RawCounters = $rawCounters.Count
    PairedSemanticComparisons = $semanticComparisons; AllocationPairs = 20; Allocation = $allocationSummary
    TimestampFrequency = $frequency; TimingFormula = 'elapsed-ticks * 1e9 / timestamp-frequency / passes'
    TimingAcceptanceGate = $false; PairedTimingWins = $timingWins; PairedTimingTies = $timingTies
    PairedTimingLosses = 20 - $timingWins - $timingTies; Timing = $timingSummary; TimingPairs = $timingPairs
    MeasurementsSHA256 = Get-Sha256 $measurementsPath; BinaryChecksSHA256 = Get-Sha256 $binaryChecksPath
    ProcessTimesRows = $expectedProcessRows.Count; ProcessTimesSHA256 = Get-Sha256 $processTimesPath
    TimingCsvRows = $expectedTimingRows.Count; TimingCsvSHA256 = Get-Sha256 $timingCsvPath
    BaselineManifestSHA256 = $baseline.ManifestSHA256; CandidateManifestSHA256 = $candidate.ManifestSHA256
    BaselineSnapshotSHA256 = $baseline.MetadataSHA256; CandidateSnapshotSHA256 = $candidate.MetadataSHA256
    BaselineArchiveManifestSHA256 = Get-Sha256 $archiveManifestPath
    BaselineProvenanceSHA256 = Get-Sha256 $provenancePath; VerifierSHA256 = Get-Sha256 $PSCommandPath
    Limitations = 'Managed actual Refresh pass with field-backed adapters and disabled verbose diagnostics. Timing is descriptive. Native window/COM/WPF/DPI behavior, polling latency, WPR, whole-process CPU/GPU and energy remain E2E_PENDING / NOT_MEASURED.'
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $candidateRoot 'validation/refresh-window-snapshot-strict-verification.json'
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
