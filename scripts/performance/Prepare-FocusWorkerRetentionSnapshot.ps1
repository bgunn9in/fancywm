param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }
function Load-Trx([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try { $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null; $document.Load($reader); return $document }
    finally { $reader.Dispose() }
}

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Require ($LASTEXITCODE -eq 0 -and [StringComparer]::OrdinalIgnoreCase.Equals(
    [IO.Path]::GetFullPath((Get-Location).Path), [IO.Path]::GetFullPath($repositoryRoot))) 'Run from repository root.'
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$sourceRoot = Join-Path $snapshotRoot 'source'
$manifestPath = Join-Path $snapshotRoot 'manifest.csv'
$snapshotPath = Join-Path $snapshotRoot 'snapshot.json'
Require ((Test-Path $manifestPath) -and (Test-Path $snapshotPath)) 'Create the immutable source snapshot first.'
$manifest = @(Import-Csv $manifestPath)
$snapshot = Get-Content -Raw $snapshotPath | ConvertFrom-Json -DateKind String
Require ($snapshot.SnapshotId -ceq $SnapshotId -and $snapshot.ManifestSHA256 -ceq (Hash $manifestPath)) 'Snapshot identity differs.'
$sourceHashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
foreach ($record in $manifest) {
    Require ($sourceHashes.TryAdd($record.Path, $record.SHA256) -and (Hash (Join-Path $sourceRoot $record.Path)) -ceq $record.SHA256) "Frozen source differs: $($record.Path)"
}
$required = @(
    'FancyWM/Utilities/FocusHelper.cs',
    'FancyWM.Tests/Utilities/FocusHelperTest.WorkerRetention.cs',
    'scripts/performance/Prepare-FocusWorkerRetentionSnapshot.ps1',
    'scripts/performance/Measure-FocusWorkerRetention.ps1',
    'scripts/performance/Verify-FocusWorkerRetention.ps1',
    'scripts/performance/Append-FocusWorkerRetentionMeasurements.ps1',
    'scripts/performance/Verify-FocusWorkerRetentionDelivery.ps1',
    'docs/performance/OPTIMIZATION_MEASUREMENTS.csv'
)
foreach ($path in $required) { Require ($sourceHashes.ContainsKey($path)) "Required frozen input missing: $path" }
Require ((Hash $PSCommandPath) -ceq $sourceHashes['scripts/performance/Prepare-FocusWorkerRetentionSnapshot.ps1']) 'Executing prepare script differs from frozen source.'
Require ($sourceHashes['docs/performance/OPTIMIZATION_MEASUREMENTS.csv'] -ceq '4EB8974ADA760ADB195853E1C2CABBE4C9F687BF13681D88BD226E3F890F837A') 'Frozen ledger is not the accepted concurrent-admission prefix.'
$previousFocus = Join-Path $repositoryRoot 'artifacts/performance/FWM-FOCUS-CONCURRENT-ADMISSION-20260909-C2/source/FancyWM/Utilities/FocusHelper.cs'
Require ($sourceHashes['FancyWM/Utilities/FocusHelper.cs'] -ceq (Hash $previousFocus)) 'FocusHelper production source differs from accepted C2.'

$buildRoot = Join-Path $snapshotRoot 'build-workspace'
$archiveRoot = Join-Path $snapshotRoot 'release-test-binaries'
$archiveManifestPath = Join-Path $snapshotRoot 'release-test-binaries.csv'
$provenancePath = Join-Path $snapshotRoot 'release-test-binaries.provenance.json'
$validationRoot = Join-Path $snapshotRoot 'validation'
foreach ($path in @($buildRoot,$archiveRoot,$archiveManifestPath,$provenancePath,$validationRoot)) { Require (!(Test-Path $path)) "Immutable prepare output already exists: $path" }
New-Item -ItemType Directory -Path $buildRoot | Out-Null
New-Item -ItemType Directory -Path $validationRoot | Out-Null
foreach ($record in $manifest) {
    $destination = Join-Path $buildRoot $record.Path
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot $record.Path) -Destination $destination
}
$project = Join-Path $buildRoot 'FancyWM.Tests/FancyWM.Tests.csproj'
$restoreLog = Join-Path $validationRoot 'prepare-release-restore.log'
$buildLog = Join-Path $validationRoot 'prepare-release-build.log'
$testLog = Join-Path $validationRoot 'prepare-release-regression.log'
$trxName = 'prepare-release-regression.trx'
$trxPath = Join-Path $validationRoot $trxName
$dotnetInfo = Join-Path $validationRoot 'prepare-dotnet-info.log'
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
& $dotnet --info *> $dotnetInfo
Require ($LASTEXITCODE -eq 0) 'dotnet --info failed.'
& $dotnet restore $project --runtime win-x64 --force --no-cache *> $restoreLog
Require ($LASTEXITCODE -eq 0) 'Isolated Release restore failed.'
& $dotnet build $project --configuration Release --runtime win-x64 --no-restore --no-incremental *> $buildLog
Require ($LASTEXITCODE -eq 0) 'Isolated Release build failed.'
$methods = @('CompletedWorkerLifetimesReleaseSequenceOwnership','OverlappedWorkerLifetimesReleaseSequenceOwnership','FocusWorkerRetentionCounterScenario')
$filter = ($methods | ForEach-Object { "FullyQualifiedName=FancyWM.Tests.Utilities.FocusHelperTest.$_" }) -join '|'
& $dotnet test $project --configuration Release --runtime win-x64 --no-restore --no-build --filter $filter --logger "trx;LogFileName=$trxName" --results-directory $validationRoot *> $testLog
Require ($LASTEXITCODE -eq 0 -and (Test-Path $trxPath)) 'Isolated retention regression failed.'
$document = Load-Trx $trxPath
$leaves = @($document.SelectNodes('.//*[local-name()="UnitTestResult"]') | Where-Object { $_.SelectNodes('./*[local-name()="InnerResults"]/*[local-name()="UnitTestResult"]').Count -eq 0 })
Require ($leaves.Count -eq 3 -and @($leaves | Where-Object outcome -CNE 'Passed').Count -eq 0 -and (@($leaves.testName | Sort-Object) -join '|') -ceq (@($methods | Sort-Object) -join '|')) 'Regression composition differs.'
$framework = 'net10.0-windows10.0.18362.0'
$binaryRoot = Join-Path $buildRoot "FancyWM.Tests/bin/Release/$framework/win-x64"
if (!(Test-Path (Join-Path $binaryRoot 'FancyWM.Tests.dll'))) { $binaryRoot = Join-Path $buildRoot "FancyWM.Tests/bin/Release/$framework" }
Require ((Test-Path (Join-Path $binaryRoot 'FancyWM.Tests.dll')) -and (Test-Path (Join-Path $binaryRoot 'FancyWM.dll'))) 'Release test binaries missing.'
New-Item -ItemType Directory -Path $archiveRoot | Out-Null
$archiveManifest = @(Get-ChildItem -LiteralPath $binaryRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($binaryRoot, $_.FullName).Replace('\','/')
    $destination = Join-Path $archiveRoot $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination
    [pscustomobject]@{Path=$relative;Hash=Hash $destination;Length=$_.Length}
})
$archiveManifest | Export-Csv -NoTypeInformation $archiveManifestPath
$production = @($archiveManifest | Where-Object Path -CEQ 'FancyWM.dll')
$fixture = @($archiveManifest | Where-Object Path -CEQ 'FancyWM.Tests.dll')
Require ($production.Count -eq 1 -and $fixture.Count -eq 1) 'Archived production or fixture binary is not unique.'
$evidence = @($restoreLog,$buildLog,$testLog,$trxPath,$dotnetInfo | ForEach-Object { [pscustomobject]@{Path=[IO.Path]::GetRelativePath($snapshotRoot,$_).Replace('\','/');SHA256=Hash $_} })
$provenance = [ordered]@{
    SnapshotId=$SnapshotId;Commit=$snapshot.Commit;Tree=$snapshot.Tree;ManifestSHA256=Hash $manifestPath;SourceFileCount=$manifest.Count;
    ProductionDelta=$false;ProductionSourceSHA256=$sourceHashes['FancyWM/Utilities/FocusHelper.cs'];FixtureSourceSHA256=$sourceHashes['FancyWM.Tests/Utilities/FocusHelperTest.WorkerRetention.cs'];
    FancyWMDllSHA256=$production[0].Hash;FancyWMTestsDllSHA256=$fixture[0].Hash;ArchiveManifestSHA256=Hash $archiveManifestPath;ArchiveFileCount=$archiveManifest.Count;
    Regression=[ordered]@{Leaves=3;Passed=3;Failed=0;TrxSHA256=Hash $trxPath};Evidence=$evidence
}
$provenance | ConvertTo-Json -Depth 6 | Set-Content $provenancePath
foreach ($record in $manifest) { Require ((Hash (Join-Path $sourceRoot $record.Path)) -ceq $record.SHA256) "Frozen source changed during prepare: $($record.Path)" }
$provenance | ConvertTo-Json -Depth 6
