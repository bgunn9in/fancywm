param([Parameter(Mandatory)][string]$SnapshotId)

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
$buildInputs = @($manifest | Where-Object {
    $_.Path -match '^(FancyWM/|FancyWM.Layouts/|FancyWM.Layouts.Tests/|FancyWM.ThemeEngine/|FancyWM.ThemeEngine.Tests/|FancyWM.Tests/|scripts/performance/).+\.(cs|csproj|ps1|xaml)$' -or
    $_.Path -in @('Directory.Build.props', 'version.json')
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

$productionSource = 'FancyWM/TilingService.cs'
$regressionSource = 'FancyWM.Tests/AlgorithmicLayouts/TilingServiceAlgorithmicIntegrationTest.RefreshSnapshot.cs'
$fixtureSource = 'FancyWM.Tests/AlgorithmicLayouts/TilingServiceAlgorithmicIntegrationTest.Manageability.cs'
$runnerSource = 'scripts/performance/Measure-MicaRefresh.ps1'
$archiveScriptSource = 'scripts/performance/Archive-RefreshWindowSnapshotBaseline.ps1'
foreach ($requiredSource in @($productionSource, $regressionSource, $fixtureSource, $runnerSource, $archiveScriptSource)) {
    Assert-Condition ($requiredSource -cin $manifest.Path) "Required source missing: $requiredSource"
}
Assert-Condition ((Get-Content -LiteralPath (Join-Path $repositoryRoot $productionSource) -Raw).Contains('List<IWindow> windows;')) `
    'Baseline production source no longer contains the List<IWindow> snapshot'

$validationRoot = Join-Path $snapshotRoot 'validation'
New-Item -ItemType Directory -Force -Path $validationRoot | Out-Null
$buildLog = Join-Path $validationRoot 'baseline-release-build.log'
$buildOutput = & dotnet build FancyWM.Tests/FancyWM.Tests.csproj `
    --configuration Release --no-restore --no-incremental 2>&1
$buildExitCode = $LASTEXITCODE
$buildOutput | Out-File -LiteralPath $buildLog -Encoding utf8
Assert-Condition ($buildExitCode -eq 0) "Baseline Release build failed: $buildExitCode"

$filter = 'FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.RefreshUsesStableWindowSnapshotWhenStateReadMutatesTrackedWindows|FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.RefreshPreservesMembershipProbeOrderOutsideLocks|FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.RefreshWindowSnapshotAvoidsListWrapperAllocation'
$redTrxName = 'baseline-refresh-window-snapshot-red.trx'
$redTrx = Join-Path $validationRoot $redTrxName
$redLog = Join-Path $validationRoot 'baseline-refresh-window-snapshot-red.log'
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
Assert-Condition ($redExitCode -eq 1) "Baseline allocation regression did not fail exactly as expected: $redExitCode"
Assert-Condition (Test-Path -LiteralPath $redTrx -PathType Leaf) 'Baseline red TRX missing'

[xml]$trx = Get-Content -LiteralPath $redTrx
$leafResults = @($trx.SelectNodes('//*[local-name()="UnitTestResult" and string-length(@dataRowInfo) > 0]'))
$semanticLeaves = @($leafResults | Where-Object {
    $_.testName -like 'RefreshUsesStableWindowSnapshotWhenStateReadMutatesTrackedWindows*' -or
    $_.testName -like 'RefreshPreservesMembershipProbeOrderOutsideLocks*'
})
$allocationLeaves = @($leafResults | Where-Object {
    $_.testName -like 'RefreshWindowSnapshotAvoidsListWrapperAllocation*'
})
Assert-Condition ($leafResults.Count -eq 8) "Baseline red leaf count differs: $($leafResults.Count)"
Assert-Condition ($semanticLeaves.Count -eq 4 -and @($semanticLeaves | Where-Object outcome -cne 'Passed').Count -eq 0) `
    'Baseline semantic leaves did not all pass'
Assert-Condition ($allocationLeaves.Count -eq 4 -and @($allocationLeaves | Where-Object outcome -cne 'Failed').Count -eq 0) `
    'Baseline allocation leaves did not all fail'
$redText = Get-Content -LiteralPath $redLog -Raw
foreach ($expectedBytes in @(160000L, 192000L, 264000L, 584000L)) {
    Assert-Condition ($redText.Contains("allocated $expectedBytes bytes")) `
        "Baseline red allocation evidence missing: $expectedBytes"
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
    Scope = 'PERF-002 Refresh owned window-set snapshot'
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
    BuildLog = 'validation/baseline-release-build.log'
    BuildLogSHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $buildLog).Hash
    ExpectedRedTestExitCode = 1
    RedTrx = "validation/$redTrxName"
    RedTrxSHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $redTrx).Hash
    RedLog = 'validation/baseline-refresh-window-snapshot-red.log'
    RedLogSHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $redLog).Hash
    SemanticLeavesPassed = 4
    AllocationLeavesFailed = 4
    BaselineBytesPerPass = @(160, 192, 264, 584)
    CandidateBudgetsBytesPerPass = @(128, 160, 232, 552)
    CopiedFileCount = $archiveFiles.Count
    CopiedBytes = [long](($archiveFiles | Measure-Object Length -Sum).Sum)
}
$provenancePath = Join-Path $snapshotRoot 'release-test-binaries.provenance.json'
$provenance | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $provenancePath
$provenance | ConvertTo-Json -Depth 4
