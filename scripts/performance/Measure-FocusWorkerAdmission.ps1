param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$CandidateSnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$BaselineSnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$ExperimentId
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

function Test-FileTree([string]$Root, [object[]]$Records, [string]$HashColumn, [bool]$CheckLengths) {
    $byPath = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in $Records) {
        Assert-RelativePath $record.Path
        Assert-Condition ($record.$HashColumn -cmatch '^[0-9A-F]{64}$' -and
            $paths.Add($record.Path) -and $byPath.TryAdd($record.Path, $record)) 'Invalid/duplicate file manifest row.'
    }
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force)
    Assert-Condition ($Records.Count -gt 0 -and $files.Count -eq $Records.Count) "File tree count differs: $Root"
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        Assert-Condition ($byPath.ContainsKey($relative) -and
            ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and
            (Get-Sha256 $file.FullName) -ceq $byPath[$relative].$HashColumn) "File hash differs: $Root/$relative"
        if ($CheckLengths) {
            Assert-Condition ([string]$byPath[$relative].Length -cmatch '^(0|[1-9][0-9]*)$' -and
                $file.Length -eq [long]$byPath[$relative].Length) "File length differs: $Root/$relative"
        }
    }
}

function Read-PreparedSnapshot([string]$Id, [string]$Role) {
    $root = Join-Path $repositoryRoot "artifacts/performance/$Id"
    $metadataPath = Join-Path $root 'snapshot.json'
    $manifestPath = Join-Path $root 'manifest.csv'
    $archiveManifestPath = Join-Path $root 'release-test-binaries.csv'
    $provenancePath = Join-Path $root 'release-test-binaries.provenance.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    $manifestHash = Get-Sha256 $manifestPath
    $metadataHash = Get-Sha256 $metadataPath
    $archiveManifestHash = Get-Sha256 $archiveManifestPath
    Assert-Condition ($metadata.SnapshotId -ceq $Id -and $metadata.ManifestSHA256 -ceq $manifestHash -and
        $provenance.Snapshot -ceq $Id -and $provenance.Role -ceq $Role -and
        $provenance.SnapshotSHA256 -ceq $metadataHash -and $provenance.SnapshotManifestSHA256 -ceq $manifestHash -and
        $provenance.ArchiveManifestSHA256 -ceq $archiveManifestHash -and $provenance.BuildSucceeded -eq $true -and
        $provenance.Configuration -ceq 'Release' -and $provenance.TargetFramework -ceq 'net10.0-windows10.0.18362.0' -and
        $provenance.SourceCopyVerified -eq $true -and $provenance.FrozenSourceVerifiedAfterBuild -eq $true) "Preparation provenance differs: $Id"
    $manifest = @(Import-Csv -LiteralPath $manifestPath)
    foreach ($record in $manifest) {
        Assert-Condition (($record.PSObject.Properties.Name -join '|') -ceq 'Path|SHA256') 'Source manifest schema differs.'
    }
    $sourceRoot = Join-Path $root 'source'
    Test-FileTree $sourceRoot $manifest 'SHA256' $false
    $archiveManifest = @(Import-Csv -LiteralPath $archiveManifestPath)
    foreach ($record in $archiveManifest) {
        Assert-Condition (($record.PSObject.Properties.Name -join '|') -ceq 'Path|Hash|Length') 'Archive manifest schema differs.'
    }
    $binaryRoot = Join-Path $root 'release-test-binaries'
    Test-FileTree $binaryRoot $archiveManifest 'Hash' $true
    $sourceHashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($record in $manifest) { $sourceHashes.Add($record.Path, $record.SHA256) }
    foreach ($label in @('ProductionSource', 'FixtureSource', 'PrepareSource', 'RunnerSource')) {
        $path = $provenance.$label
        $hashProperty = "${label}SHA256"
        Assert-Condition ($sourceHashes.ContainsKey($path) -and $sourceHashes[$path] -ceq $provenance.$hashProperty) "Prepared source provenance differs: $Id/$label"
    }
    Assert-Condition ($provenance.ProductionSource -ceq 'FancyWM/Utilities/FocusHelper.cs' -and
        $provenance.FixtureSource -ceq 'FancyWM.Tests/Utilities/FocusHelperTest.WorkerAdmission.cs' -and
        $provenance.PrepareSource -ceq 'scripts/performance/Prepare-FocusWorkerSnapshot.ps1' -and
        $provenance.RunnerSource -ceq 'scripts/performance/Measure-FocusWorkerAdmission.ps1') 'Prepared source paths differ.'
    foreach ($label in @('Commands', 'Environment', 'RestoreLog', 'BuildLog', 'RegressionTrx', 'RegressionLog')) {
        Assert-RelativePath $provenance.$label
        $hashProperty = "${label}SHA256"
        Assert-Condition ((Get-Sha256 (Join-Path $root $provenance.$label)) -ceq $provenance.$hashProperty) "Preparation evidence hash differs: $Id/$label"
    }
    $failed = if ($Role -ceq 'Baseline') { 3 } else { 0 }
    $exitCode = if ($Role -ceq 'Baseline') { 1 } else { 0 }
    Assert-Condition ($provenance.Regression.Leaves -eq 8 -and $provenance.Regression.RawResults -eq 10 -and
        $provenance.Regression.Passed -eq 8 - $failed -and $provenance.Regression.Failed -eq $failed -and
        $provenance.RegressionExitCode -eq $exitCode) 'Prepared regression outcome differs.'
    $production = @($archiveManifest | Where-Object Path -CEQ 'FancyWM.dll')
    $fixture = @($archiveManifest | Where-Object Path -CEQ 'FancyWM.Tests.dll')
    Assert-Condition ($production.Count -eq 1 -and $fixture.Count -eq 1 -and
        $production[0].Hash -ceq $provenance.FancyWMDllSHA256 -and
        $fixture[0].Hash -ceq $provenance.FancyWMTestsDllSHA256) 'Prepared assembly identities differ.'
    [pscustomobject]@{
        Id = $Id; Root = $root; Metadata = $metadata; MetadataSHA256 = $metadataHash; ManifestSHA256 = $manifestHash
        SourceRoot = $sourceRoot; Manifest = $manifest; SourceHashes = $sourceHashes
        BinaryRoot = $binaryRoot; ArchiveManifest = $archiveManifest; ArchiveManifestSHA256 = $archiveManifestHash
        Provenance = $provenance; ProvenanceSHA256 = Get-Sha256 $provenancePath
        Production = $production[0]; Fixture = $fixture[0]
    }
}

function Assert-Guid([string]$Value, [string]$Label) {
    $parsed = [guid]::Empty
    Assert-Condition ([guid]::TryParseExact($Value, 'D', [ref]$parsed) -and $parsed -ne [guid]::Empty) "Invalid GUID: $Label"
}

function Read-CounterTrx([string]$Path, [string]$AssemblyPath) {
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
        $root.NamespaceURI -ceq 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010') 'Invalid counter TRX root.'
    Assert-Guid $root.id 'TestRun.id'
    $summaries = @($root.SelectNodes('./*[local-name()="ResultSummary"]'))
    $times = @($root.SelectNodes('./*[local-name()="Times"]'))
    $results = @($root.SelectNodes('.//*[local-name()="UnitTestResult"]'))
    $definitions = @($root.SelectNodes('./*[local-name()="TestDefinitions"]/*[local-name()="UnitTest"]'))
    $entries = @($root.SelectNodes('./*[local-name()="TestEntries"]/*[local-name()="TestEntry"]'))
    $counters = @($root.SelectNodes('.//*[local-name()="Counters"]'))
    $outputs = @($root.SelectNodes('.//*[local-name()="StdOut"]'))
    Assert-Condition ($summaries.Count -eq 1 -and $summaries[0].outcome -ceq 'Completed' -and
        $times.Count -eq 1 -and $results.Count -eq 1 -and $definitions.Count -eq 1 -and $entries.Count -eq 1 -and
        $counters.Count -eq 1 -and $outputs.Count -eq 1 -and
        $root.SelectNodes('.//*[local-name()="ErrorInfo"]').Count -eq 0 -and
        $root.SelectNodes('./*[local-name()="Results"]/*[local-name()="UnitTestResult"]').Count -eq 1 -and
        $summaries[0].SelectNodes('./*[local-name()="Counters"]').Count -eq 1) 'Counter TRX dimensions/outcomes differ.'
    foreach ($name in @('total','executed','passed','failed','error','timeout','aborted','inconclusive','passedButRunAborted','notRunnable','notExecuted','disconnected','warning','completed','inProgress','pending')) {
        $expected = if ($name -cin @('total','executed','passed')) { '1' } else { '0' }
        Assert-Condition ($counters[0].GetAttribute($name) -ceq $expected) "Counter TRX result count differs: $name"
    }
    $result = $results[0]
    $definition = $definitions[0]
    $entry = $entries[0]
    $methods = @($definition.SelectNodes('./*[local-name()="TestMethod"]'))
    $executions = @($definition.SelectNodes('./*[local-name()="Execution"]'))
    Assert-Condition ($methods.Count -eq 1 -and $executions.Count -eq 1) 'Counter definition method/execution count differs.'
    $method = $methods[0]
    $testName = 'FocusWorkerAdmissionCounterScenario'
    Assert-Guid $result.testId 'result.testId'
    Assert-Guid $result.executionId 'result.executionId'
    Assert-Guid $result.testListId 'result.testListId'
    Assert-Condition ($result.testName -ceq $testName -and $result.outcome -ceq 'Passed' -and
        !$result.HasAttribute('resultType') -and !$result.HasAttribute('parentExecutionId') -and
        $result.SelectNodes('./*[local-name()="InnerResults"]').Count -eq 0 -and
        $result.relativeResultsDirectory -ceq $result.executionId -and
        $definition.name -ceq $testName -and $definition.id -ceq $result.testId -and
        $executions[0].id -ceq $result.executionId -and $entry.testId -ceq $result.testId -and
        $entry.executionId -ceq $result.executionId -and $entry.testListId -ceq $result.testListId -and
        $method.name -ceq $testName -and $method.className -ceq 'FancyWM.Tests.Utilities.FocusHelperTest' -and
        $method.adapterTypeName -ceq 'executor://mstestadapter/v2' -and
        $result.SelectNodes('./*[local-name()="Output"]/*[local-name()="StdOut"]').Count -eq 1) 'Counter TRX identity/relationships differ.'
    foreach ($actual in @($method.codeBase, $definition.storage)) {
        Assert-Condition (![string]::IsNullOrWhiteSpace($actual) -and [IO.Path]::IsPathRooted($actual) -and
            [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($actual), $AssemblyPath)) 'Counter TRX assembly identity differs.'
    }
    $start = [DateTimeOffset]::Parse($times[0].start, [Globalization.CultureInfo]::InvariantCulture)
    $finish = [DateTimeOffset]::Parse($times[0].finish, [Globalization.CultureInfo]::InvariantCulture)
    $testStart = [DateTimeOffset]::Parse($result.startTime, [Globalization.CultureInfo]::InvariantCulture)
    $testFinish = [DateTimeOffset]::Parse($result.endTime, [Globalization.CultureInfo]::InvariantCulture)
    Assert-Condition ($start -lt $finish -and $start -le $testStart -and $testStart -le $testFinish -and $testFinish -le $finish) 'Counter TRX interval differs.'
    [pscustomobject]@{ RunId = $root.id; ExecutionId = $result.executionId; Start = $start; Finish = $finish; StdOut = $outputs[0].InnerText }
}

function Get-ExpectedCounter([string]$Case, [string]$Metric) {
    switch -CaseSensitive ($Metric) {
        { $_ -cin @('admissions','current-results','worker-starts','worker-exits','background-workers','queue-calls','thread-id-reads','foreground-id-reads','attach-calls') } { 500L }
        { $_ -cin @('true-results','detach-calls') } { if ($Case -ceq 'attach-failure') { 0L } else { 500L } }
        'focus-calls' { if ($Case -ceq 'direct-success') { 500L } elseif ($Case -ceq 'alt-retry') { 1000L } else { 0L } }
        'alt-presses' { if ($Case -ceq 'alt-retry') { 500L } else { 0L } }
        { $_ -cin @('join-timeouts','wait-calls','owned-attachments') } { 0L }
        default { $null }
    }
}

function Get-Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    Assert-Condition ($sorted.Count -eq 5) 'Expected five values for descriptive median.'
    $sorted[2]
}

$rootOutput = @(& git -C $PSScriptRoot rev-parse --show-toplevel)
Assert-Condition ($LASTEXITCODE -eq 0) 'Cannot resolve repository root.'
$repositoryRoot = [IO.Path]::GetFullPath(($rootOutput -join '').Trim())
Assert-Condition ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath((Get-Location).Path), $repositoryRoot)) 'Run from the repository root.'
Assert-Condition ($CandidateSnapshotId -cne $BaselineSnapshotId -and
    [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [Runtime.InteropServices.Architecture]::X64) 'Distinct snapshots and x64 PowerShell are required.'
$candidate = Read-PreparedSnapshot $CandidateSnapshotId 'Candidate'
$baseline = Read-PreparedSnapshot $BaselineSnapshotId 'Baseline'
Assert-Condition ($candidate.Metadata.Commit -ceq $baseline.Metadata.Commit -and $candidate.Metadata.Tree -ceq $baseline.Metadata.Tree -and
    ($candidate.Metadata.Submodules -join '|') -ceq ($baseline.Metadata.Submodules -join '|') -and
    $candidate.Manifest.Count -eq $baseline.Manifest.Count) 'Snapshot git/source identities differ.'
$differences = [Collections.Generic.List[string]]::new()
foreach ($record in $candidate.Manifest) {
    Assert-Condition ($baseline.SourceHashes.ContainsKey($record.Path)) "Baseline source path missing: $($record.Path)"
    if ($record.SHA256 -cne $baseline.SourceHashes[$record.Path]) { $differences.Add($record.Path) }
}
Assert-Condition ($differences.Count -eq 1 -and $differences[0] -ceq 'FancyWM/Utilities/FocusHelper.cs') 'Frozen manifests must differ only in FocusHelper.cs.'
Assert-Condition ($candidate.Production.Hash -cne $baseline.Production.Hash -and
    (Get-Sha256 $PSCommandPath) -ceq $candidate.SourceHashes['scripts/performance/Measure-FocusWorkerAdmission.ps1']) 'Production binaries must differ and executing runner must match the frozen source.'
$experimentRoot = Join-Path $candidate.Root $ExperimentId
Assert-Condition (!(Test-Path -LiteralPath $experimentRoot)) "Immutable experiment already exists: $experimentRoot"
New-Item -ItemType Directory -Path $experimentRoot | Out-Null
$units = [ordered]@{
    'allocated-bytes' = 'bytes/500 admissions'; 'elapsed-ticks' = 'ticks/500 admissions'; 'timestamp-frequency' = 'ticks/second'
    'admissions' = 'admissions/scenario'; 'true-results' = 'true-completions/scenario'; 'current-results' = 'current-token-observations/scenario'
    'worker-starts' = 'worker-starts/scenario'; 'worker-exits' = 'worker-exits/scenario'; 'background-workers' = 'background-workers/scenario'
    'join-timeouts' = 'join-timeouts/scenario'; 'wait-calls' = 'wait-adapter-calls/scenario'; 'queue-calls' = 'queue-adapter-calls/scenario'
    'thread-id-reads' = 'thread-id-reads/scenario'; 'foreground-id-reads' = 'foreground-id-reads/scenario'
    'attach-calls' = 'attach-adapter-calls/scenario'; 'detach-calls' = 'detach-adapter-calls/scenario'
    'focus-calls' = 'focus-adapter-calls/scenario'; 'alt-presses' = 'alt-adapter-calls/scenario'; 'owned-attachments' = 'owned-attachments/scenario'
}
$cases = @('direct-success', 'alt-retry', 'attach-failure')
$instrument = 'Actual production FocusHelper.RequestSequence.Enqueue with one real completed background Thread per admission and a counted fake native adapter;32 discarded warmups then500 admissions;caller-thread GC.GetAllocatedBytesForCurrentThread;Stopwatch includes Thread.Start/Join and scheduling;common candidate fixture and dependencies, only archived FancyWM.dll differs;native focus,COM,WPF,DPI,whole-process CPU/GPU and native presentation latency NOT_MEASURED;timing descriptive without required timing win'
$rows = [Collections.Generic.List[object]]::new()
$raw = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[object]]::new()
$binaryChecks = [Collections.Generic.List[object]]::new()
$runManifests = [Collections.Generic.List[object]]::new()
$values = [Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
$trxRunIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$executionIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$frequencies = [Collections.Generic.HashSet[long]]::new()
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
$dotnetInfoPath = Join-Path $experimentRoot 'dotnet-info.log'
& $dotnet --info *> $dotnetInfoPath
Assert-Condition ($LASTEXITCODE -eq 0) 'dotnet --info failed.'
$filter = 'FullyQualifiedName=FancyWM.Tests.Utilities.FocusHelperTest.FocusWorkerAdmissionCounterScenario'
$counterPattern = '^PERFCOUNTER focus-worker-(direct-success|alt-retry|attach-failure) (' + ($units.Keys -join '|') + ') (0|[1-9][0-9]*)$'
foreach ($pair in 1..5) {
    $variants = if ($pair % 2) { @('baseline', 'candidate') } else { @('candidate', 'baseline') }
    foreach ($variant in $variants) {
        $runId = "$variant-$pair"
        $selected = if ($variant -ceq 'baseline') { $baseline } else { $candidate }
        $runBinaryRoot = Join-Path $experimentRoot "runs/$runId/binaries"
        New-Item -ItemType Directory -Path $runBinaryRoot | Out-Null
        $runManifest = @(foreach ($record in $candidate.ArchiveManifest) {
            # Every dependency and the benchmark come from the same candidate output.
            $inputRoot = if ($record.Path -ceq 'FancyWM.dll') { $selected.BinaryRoot } else { $candidate.BinaryRoot }
            $expectedHash = if ($record.Path -ceq 'FancyWM.dll') { $selected.Production.Hash } else { $record.Hash }
            $expectedLength = if ($record.Path -ceq 'FancyWM.dll') { $selected.Production.Length } else { $record.Length }
            $destination = Join-Path $runBinaryRoot $record.Path
            New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
            Copy-Item -LiteralPath (Join-Path $inputRoot $record.Path) -Destination $destination
            [pscustomobject]@{ Path = $record.Path; Hash = $expectedHash; Length = $expectedLength }
        })
        Test-FileTree $runBinaryRoot $runManifest 'Hash' $true
        $runManifestPath = Join-Path $experimentRoot "runs/$runId/binaries.csv"
        $runManifest | Export-Csv -NoTypeInformation -LiteralPath $runManifestPath
        $runManifests.Add([pscustomobject]@{ Root = $runBinaryRoot; Records = $runManifest; Path = "runs/$runId/binaries.csv"; SHA256 = Get-Sha256 $runManifestPath })
        $assembly = [IO.Path]::GetFullPath((Join-Path $runBinaryRoot 'FancyWM.Tests.dll'))
        $trxPath = Join-Path $experimentRoot "$runId.trx"
        $stdoutPath = Join-Path $experimentRoot "$runId.stdout.log"
        $stderrPath = Join-Path $experimentRoot "$runId.stderr.log"
        $commandPath = Join-Path $experimentRoot "$runId.command.json"
        $arguments = @('vstest', $assembly, '/Platform:x64', "/TestCaseFilter:$filter", "/Logger:trx;LogFileName=$runId.trx", "/ResultsDirectory:$experimentRoot")
        [ordered]@{ Executable = $dotnet; Arguments = $arguments; WorkingDirectory = $runBinaryRoot; Environment = @{ DOTNET_TieredCompilation = '0' } } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $commandPath
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $dotnet
        $startInfo.WorkingDirectory = $runBinaryRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.Environment['DOTNET_TieredCompilation'] = '0'
        foreach ($argument in $arguments) { $startInfo.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        $processStarted = [DateTimeOffset]::UtcNow
        try {
            Assert-Condition ($process.Start()) "Failed to start counter process: $runId"
            $processId = $process.Id
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            $timedOut = !$process.WaitForExit(300000)
            if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
            [IO.File]::WriteAllText($stdoutPath, $stdout, [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText($stderrPath, $stderr, [Text.UTF8Encoding]::new($false))
            $exitCode = $process.ExitCode
            $processFinished = [DateTimeOffset]::UtcNow
        }
        finally { $process.Dispose() }
        [ordered]@{ RunId = $runId; ProcessId = $processId; Start = $processStarted.ToString('o'); Finish = $processFinished.ToString('o'); ExitCode = $exitCode; TimedOut = $timedOut } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $experimentRoot "$runId.process.json")
        Assert-Condition (!$timedOut -and $exitCode -eq 0) "Counter process failed: $runId (exit $exitCode, timeout $timedOut)"
        $trx = Read-CounterTrx $trxPath $assembly
        Assert-Condition ($trxRunIds.Add($trx.RunId) -and $executionIds.Add($trx.ExecutionId) -and
            $processStarted -le $trx.Start -and $trx.Finish -le $processFinished) 'Counter process identity/interval differs.'
        Test-FileTree $runBinaryRoot $runManifest 'Hash' $true
        foreach ($record in $runManifest) {
            $binaryChecks.Add([pscustomobject]@{ RunId = $runId; Variant = $variant; Pair = $pair; Path = $record.Path; CommonCandidateSHA256 = @($candidate.ArchiveManifest | Where-Object Path -CEQ $record.Path)[0].Hash; ExpectedSHA256 = $record.Hash; BeforeSHA256 = $record.Hash; AfterSHA256 = Get-Sha256 (Join-Path $runBinaryRoot $record.Path); Length = $record.Length })
        }
        $lines = @($trx.StdOut -split '\r?\n' | Where-Object { $_ -cne '' })
        Assert-Condition ($lines.Count -eq 57) "Expected exactly 57 counter lines: $runId"
        $runKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($line in $lines) {
            $match = [regex]::Match($line, $counterPattern)
            Assert-Condition ($match.Success) "Malformed/unexpected counter line: $runId/$line"
            $case = $match.Groups[1].Value
            $metric = $match.Groups[2].Value
            $value = [long]::Parse($match.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
            $key = "$variant/$pair/$case/$metric"
            Assert-Condition ($runKeys.Add("$case/$metric") -and $values.TryAdd($key, $value)) "Duplicate counter: $key"
            $expected = Get-ExpectedCounter $case $metric
            if ($null -ne $expected) { Assert-Condition ($value -eq $expected) "Counter semantics differ: $key (expected $expected, got $value)" }
            elseif ($metric -cin @('elapsed-ticks','timestamp-frequency')) { Assert-Condition ($value -gt 0) "Nonpositive timing/frequency: $key" }
            if ($metric -ceq 'timestamp-frequency') { [void]$frequencies.Add($value) }
            $raw.Add([pscustomobject]@{ RunId = $runId; Variant = $variant; Pair = $pair; Scenario = "focus-worker-$case"; Metric = $metric; Value = $value; TrxSHA256 = Get-Sha256 $trxPath; Line = $line })
            $rows.Add([pscustomobject][ordered]@{
                experiment_id = $ExperimentId; snapshot_id = $selected.Id
                scenario = "Actual FocusHelper.RequestSequence.Enqueue; focus-worker-$case;32 discarded warmups then500 completed worker admissions"
                configuration = 'Release; net10.0-windows10.0.18362.0; x64; no debugger; DOTNET_TieredCompilation=0'
                window_count = 0; metric = $metric; unit = $units[$metric]; run = $pair
                value = $value.ToString([Globalization.CultureInfo]::InvariantCulture); iterations = 500
                instrument = $instrument; perf_id = 'PERF-030'; variant = $variant; pair = $pair
                binary_sha256 = $selected.Production.Hash; benchmark_sha256 = $candidate.Fixture.Hash
            })
        }
        foreach ($case in $cases) {
            foreach ($metric in $units.Keys) { Assert-Condition ($runKeys.Contains("$case/$metric")) "Missing counter: $runId/$case/$metric" }
        }
        $processes.Add([pscustomobject]@{
            RunId = $runId; Variant = $variant; Pair = $pair; ProcessId = $processId; ExitCode = $exitCode
            Start = $processStarted.ToString('o'); Finish = $processFinished.ToString('o')
            TrxStart = $trx.Start.ToString('o'); TrxFinish = $trx.Finish.ToString('o'); TrxId = $trx.RunId; ExecutionId = $trx.ExecutionId
            TrxSHA256 = Get-Sha256 $trxPath; StdOutSHA256 = Get-Sha256 $stdoutPath; StdErrSHA256 = Get-Sha256 $stderrPath
            CommandSHA256 = Get-Sha256 $commandPath; BinaryManifestSHA256 = Get-Sha256 $runManifestPath
            FancyWMDllSHA256 = $selected.Production.Hash; FancyWMTestsDllSHA256 = $candidate.Fixture.Hash
        })
        Write-Output "$runId passed: 57 counters; isolated x64 process $processId"
    }
}
Assert-Condition ($rows.Count -eq 570 -and $raw.Count -eq 570 -and $frequencies.Count -eq 1) 'Measurement matrix/frequency differs.'
$expectedOrder = 'baseline-1,candidate-1,candidate-2,baseline-2,baseline-3,candidate-3,candidate-4,baseline-4,baseline-5,candidate-5'
Assert-Condition ($processes.Count -eq 10 -and ($processes.RunId -join ',') -ceq $expectedOrder) 'Alternating process order differs.'
for ($index = 1; $index -lt $processes.Count; $index++) {
    Assert-Condition ([DateTimeOffset]::Parse($processes[$index].Start) -ge [DateTimeOffset]::Parse($processes[$index - 1].Finish)) 'Measurement processes overlap.'
}
$paired = [Collections.Generic.List[object]]::new()
$timingWins = 0
$timingLosses = 0
$timingTies = 0
$semanticComparisons = 0
foreach ($case in $cases) {
    foreach ($pair in 1..5) {
        foreach ($metric in $units.Keys) {
            $before = $values["baseline/$pair/$case/$metric"]
            $after = $values["candidate/$pair/$case/$metric"]
            if ($metric -ceq 'allocated-bytes') { Assert-Condition ($after -lt $before) "Allocation did not improve: $pair/$case ($before -> $after)" }
            elseif ($metric -cne 'elapsed-ticks') {
                Assert-Condition ($before -eq $after -and $before -eq $values["baseline/1/$case/$metric"]) "Semantic/frequency counter differs across pairs: $pair/$case/$metric"
                if ($metric -cne 'timestamp-frequency') { $semanticComparisons++ }
            }
        }
        $frequency = $values["baseline/$pair/$case/timestamp-frequency"]
        $baselineNs = [double]$values["baseline/$pair/$case/elapsed-ticks"] * 1e9 / $frequency / 500
        $candidateNs = [double]$values["candidate/$pair/$case/elapsed-ticks"] * 1e9 / $frequency / 500
        $timing = if ($candidateNs -lt $baselineNs) { $timingWins++; 'win' } elseif ($candidateNs -gt $baselineNs) { $timingLosses++; 'loss' } else { $timingTies++; 'tie' }
        $paired.Add([pscustomobject]@{ Case = $case; Pair = $pair; BaselineBytesPerAdmission = [double]$values["baseline/$pair/$case/allocated-bytes"] / 500; CandidateBytesPerAdmission = [double]$values["candidate/$pair/$case/allocated-bytes"] / 500; BaselineNsPerAdmission = $baselineNs; CandidateNsPerAdmission = $candidateNs; Timing = $timing })
    }
}
Assert-Condition ($semanticComparisons -eq 240 -and $paired.Count -eq 15) 'Paired comparison counts differ.'
foreach ($prepared in @($baseline, $candidate)) {
    Assert-Condition ((Get-Sha256 (Join-Path $prepared.Root 'snapshot.json')) -ceq $prepared.MetadataSHA256 -and
        (Get-Sha256 (Join-Path $prepared.Root 'manifest.csv')) -ceq $prepared.ManifestSHA256 -and
        (Get-Sha256 (Join-Path $prepared.Root 'release-test-binaries.csv')) -ceq $prepared.ArchiveManifestSHA256 -and
        (Get-Sha256 (Join-Path $prepared.Root 'release-test-binaries.provenance.json')) -ceq $prepared.ProvenanceSHA256) 'Prepared snapshot metadata changed during measurement.'
    Test-FileTree $prepared.SourceRoot $prepared.Manifest 'SHA256' $false
    Test-FileTree $prepared.BinaryRoot $prepared.ArchiveManifest 'Hash' $true
}
foreach ($runManifest in $runManifests) {
    Test-FileTree $runManifest.Root $runManifest.Records 'Hash' $true
    Assert-Condition ((Get-Sha256 (Join-Path $experimentRoot $runManifest.Path)) -ceq $runManifest.SHA256) 'Run binary manifest changed.'
}
$rows | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $experimentRoot 'measurements.csv')
$raw | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $experimentRoot 'raw-counters.csv')
$binaryChecks | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $experimentRoot 'binary-checks.csv')
$processes | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $experimentRoot 'process-times.csv')
$paired | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $experimentRoot 'paired-results.csv')
$caseSummaries = @(foreach ($case in $cases) {
    $selectedPairs = @($paired | Where-Object Case -CEQ $case)
    [pscustomobject]@{ Case = $case; BaselineMedianBytesPerAdmission = Get-Median ([double[]]$selectedPairs.BaselineBytesPerAdmission); CandidateMedianBytesPerAdmission = Get-Median ([double[]]$selectedPairs.CandidateBytesPerAdmission); BaselineMedianNsPerAdmission = Get-Median ([double[]]$selectedPairs.BaselineNsPerAdmission); CandidateMedianNsPerAdmission = Get-Median ([double[]]$selectedPairs.CandidateNsPerAdmission) }
})
$evidence = @(foreach ($path in @('measurements.csv','raw-counters.csv','binary-checks.csv','process-times.csv','paired-results.csv','dotnet-info.log')) {
    [pscustomobject]@{ Path = $path; SHA256 = Get-Sha256 (Join-Path $experimentRoot $path) }
})
$provenance = [ordered]@{
    ExperimentId = $ExperimentId; PerfId = 'PERF-030'; Decision = 'MEASUREMENT_CHECKS_PASS'
    BaselineSnapshotId = $BaselineSnapshotId; CandidateSnapshotId = $CandidateSnapshotId
    BaselineManifestSHA256 = $baseline.ManifestSHA256; CandidateManifestSHA256 = $candidate.ManifestSHA256
    BaselineArchiveProvenanceSHA256 = $baseline.ProvenanceSHA256; CandidateArchiveProvenanceSHA256 = $candidate.ProvenanceSHA256
    BaselineFancyWMDllSHA256 = $baseline.Production.Hash; CandidateFancyWMDllSHA256 = $candidate.Production.Hash
    CommonFancyWMTestsDllSHA256 = $candidate.Fixture.Hash
    FixtureSourceSHA256 = $candidate.SourceHashes['FancyWM.Tests/Utilities/FocusHelperTest.WorkerAdmission.cs']
    RunnerSourceSHA256 = Get-Sha256 $PSCommandPath; SourceDifferences = @($differences); OnlyFancyWMDllDiffersInRunBinaries = $true
    WarmupAdmissions = 32; MeasuredAdmissions = 500; Cases = $cases; Metrics = @($units.Keys)
    ProcessCount = 10; AlternatingOrder = $expectedOrder; ProcessIntervalsDoNotOverlap = $true
    MeasurementRows = $rows.Count; AllocationPairsImproved = 15; SemanticComparisonsMatched = $semanticComparisons
    TimingWins = $timingWins; TimingLosses = $timingLosses; TimingTies = $timingTies
    TimingClassification = if ($timingWins -gt 0 -and $timingLosses -gt 0) { 'mixed' } elseif ($timingLosses -gt 0) { 'losses; descriptive' } elseif ($timingWins -gt 0) { 'wins; descriptive' } else { 'ties; descriptive' }
    TimingScope = 'Descriptive managed fixture elapsed time includes real background Thread.Start/Join and OS scheduling; no required timing win.'
    AllocationScope = 'Caller-thread allocations only; worker allocations and whole-process allocation NOT_MEASURED.'
    NotMeasured = @('native focus APIs','COM','WPF','DPI','whole-process CPU','GPU','native presentation latency')
    CaseSummaries = $caseSummaries; Evidence = $evidence; FrozenSourcesAndArchivesVerifiedAfterMeasurement = $true
}
$provenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $experimentRoot 'measurement-provenance.json')
$provenance | ConvertTo-Json -Depth 8
