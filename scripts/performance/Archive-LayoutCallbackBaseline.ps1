param([Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId)

$ErrorActionPreference = 'Stop'

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
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

$productionSource = 'FancyWM/Utilities/LayoutInvalidationQueue.cs'
$regressionSource = 'FancyWM.Tests/Utilities/LayoutInvalidationQueueTest.CallbackCache.cs'
$fixtureSource = 'FancyWM.Tests/Utilities/LayoutInvalidationQueueTest.cs'
$runnerSource = 'scripts/performance/Measure-MicaRefresh.ps1'
$archiveScriptSource = 'scripts/performance/Archive-LayoutCallbackBaseline.ps1'
$verifierSource = 'scripts/performance/Verify-LayoutCallbackCache.ps1'
$appendScriptSource = 'scripts/performance/Append-LayoutCallbackMeasurements.ps1'
foreach ($requiredSource in @($productionSource, $regressionSource, $fixtureSource, $runnerSource, $archiveScriptSource, $verifierSource, $appendScriptSource)) {
    Assert-Condition ($requiredSource -cin $manifest.Path) "Required source missing: $requiredSource"
}
$productionText = Get-Content -LiteralPath (Join-Path $repositoryRoot $productionSource) -Raw
Assert-Condition ([regex]::Matches($productionText, '\bschedule\(Execute\);').Count -eq 1 -and $productionText -cnotmatch '\bm_execute\b') 'Baseline must contain original schedule(Execute) without m_execute.'
foreach ($relativeOutput in @('release-test-binaries','release-test-binaries.csv','release-test-binaries.provenance.json','validation/baseline-release-build.log','validation/baseline-layout-callback-red.trx','validation/baseline-layout-callback-red.log')) {
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

$testClass = 'FancyWM.Tests.Utilities.LayoutInvalidationQueueTest'
$redMethods = @('CallbackCacheReusesScheduledDelegateAcrossCompletedPasses','CallbackCacheDoesNotRootDisposedQueueAfterCompletedPass','LayoutCallbackCounterScenario')
$filter = ($redMethods | ForEach-Object { "FullyQualifiedName=$testClass.$_" }) -join '|'
$redTrxName = 'baseline-layout-callback-red.trx'
$redTrx = Join-Path $validationRoot $redTrxName
$redLog = Join-Path $validationRoot 'baseline-layout-callback-red.log'
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
Assert-Condition ($redExitCode -eq 1) "Baseline callback identity regression did not fail exactly as expected: $redExitCode"
Assert-Condition (Test-Path -LiteralPath $redTrx -PathType Leaf) 'Baseline red TRX missing'

[xml]$trx = Get-Content -LiteralPath $redTrx
$results = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]'))
$definitions = @($trx.SelectNodes('//*[local-name()="TestMethod"]'))
$counters = @($trx.SelectNodes('//*[local-name()="Counters"]'))
Assert-Condition ($results.Count -eq 3 -and $definitions.Count -eq 3 -and $counters.Count -eq 1 -and $counters[0].total -ceq '3' -and $counters[0].executed -ceq '3' -and $counters[0].passed -ceq '2' -and $counters[0].failed -ceq '1') 'Baseline red test counts differ.'
foreach ($name in @('error','timeout','aborted','inconclusive','passedButRunAborted','notRunnable','notExecuted','disconnected','warning','completed','inProgress','pending')) {
    Assert-Condition ($counters[0].GetAttribute($name) -ceq '0') "Unexpected red outcome: $name"
}
foreach ($method in $redMethods) {
    $result = @($results | Where-Object testName -ceq $method)
    $definition = @($definitions | Where-Object { $_.name -ceq $method -and $_.className -ceq $testClass })
    $expectedOutcome = if ($method -ceq $redMethods[0]) { 'Failed' } else { 'Passed' }
    Assert-Condition ($result.Count -eq 1 -and $definition.Count -eq 1 -and $result[0].outcome -ceq $expectedOutcome) "Unexpected baseline regression: $method"
}
$failure = @($results | Where-Object testName -ceq $redMethods[0])[0]
$failureMessages = @($failure.SelectNodes('./*[local-name()="Output"]/*[local-name()="ErrorInfo"]/*[local-name()="Message"]'))
Assert-Condition ($failureMessages.Count -eq 1 -and $failureMessages[0].InnerText -ceq 'Assert.IsTrue failed. A completed pass must schedule its dirty follow-up with the same delegate instance.') 'Baseline failed for a reason other than callback identity.'
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
    Scope = 'PERF-004 LayoutInvalidationQueue cached instance callback'
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
    RedLog = 'validation/baseline-layout-callback-red.log'
    RedLogSHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $redLog).Hash
    SemanticTestsPassed = 2
    CallbackIdentityTestsFailed = 1
    CopiedFileCount = $archiveFiles.Count
    CopiedBytes = [long](($archiveFiles | Measure-Object Length -Sum).Sum)
}
$provenancePath = Join-Path $snapshotRoot 'release-test-binaries.provenance.json'
$provenance | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $provenancePath
$provenance | ConvertTo-Json -Depth 4
