param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$CandidateSnapshotId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$BaselineSnapshotId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Require ($LASTEXITCODE -eq 0 -and [StringComparer]::OrdinalIgnoreCase.Equals(
    [IO.Path]::GetFullPath((Get-Location).Path), [IO.Path]::GetFullPath($repositoryRoot))) 'Run from repository root.'
$candidateRoot = Join-Path $repositoryRoot "artifacts/performance/$CandidateSnapshotId"
$candidateSource = Join-Path $candidateRoot 'source'
$candidateManifestPath = Join-Path $candidateRoot 'manifest.csv'
$candidateSnapshotPath = Join-Path $candidateRoot 'snapshot.json'
$baselineRoot = Join-Path $repositoryRoot "artifacts/performance/$BaselineSnapshotId"
Require ($CandidateSnapshotId -cne $BaselineSnapshotId -and (Test-Path $candidateManifestPath) -and (Test-Path $candidateSnapshotPath)) 'Candidate snapshot is missing or IDs are equal.'
Require (!(Test-Path $baselineRoot)) 'Mechanical baseline snapshot already exists.'

$candidateManifest = @(Import-Csv $candidateManifestPath)
$candidateSnapshot = Get-Content -Raw $candidateSnapshotPath | ConvertFrom-Json -DateKind String
Require ($candidateSnapshot.SnapshotId -ceq $CandidateSnapshotId -and $candidateSnapshot.ManifestSHA256 -ceq (Hash $candidateManifestPath)) 'Candidate snapshot metadata differs.'
foreach ($record in $candidateManifest) {
    $frozen = Join-Path $candidateSource $record.Path
    Require ((Test-Path $frozen -PathType Leaf) -and (Hash $frozen) -ceq $record.SHA256) "Candidate frozen source differs: $($record.Path)"
    if ($record.Path -match '\.(cs|csproj|xaml|props|targets|sln|slnx|ps1)$' -or $record.Path -match '(^|/)(global\.json|NuGet\.config)$' -or $record.Path -ceq 'version.json') {
        Require ((Hash (Join-Path $repositoryRoot $record.Path)) -ceq $record.SHA256) "Current build input differs from candidate snapshot: $($record.Path)"
    }
}
$required = @(
    'FancyWM/MultiDisplayTilingService.cs',
    'FancyWM.Tests/AlgorithmicLayouts/MultiDisplayTilingServiceIntegrationTest.cs',
    'FancyWM.Tests/AlgorithmicLayouts/MultiDisplayTilingServiceIntegrationTest.GcPause.cs',
    'scripts/performance/Prepare-DisplayRemovalGcPause.ps1',
    'scripts/performance/Measure-DisplayRemovalGcPause.ps1',
    'scripts/performance/Verify-DisplayRemovalGcPause.ps1',
    'scripts/performance/Append-DisplayRemovalGcPauseMeasurements.ps1'
)
foreach ($path in $required) { Require ($path -cin $candidateManifest.Path) "Candidate input is missing: $path" }

$productionPath = 'FancyWM/MultiDisplayTilingService.cs'
$candidateProductionPath = Join-Path $candidateSource $productionPath
$candidateText = [IO.File]::ReadAllText($candidateProductionPath)
$candidateBlock = @'
                if (removedTiling != null)
                {
                    _ = RetireTilingServiceAsync(removedTiling, collect: true, failures);
                }
'@
$baselineBlock = @'
                if (removedTiling != null)
                {
                    _ = RetireTilingServiceAsync(removedTiling, collect: true, failures);
                }
                else
                {
                    m_scheduleGarbageCollection();
                }
'@
$candidatePattern = '(?m)^                if \(removedTiling != null\)\r?\n                \{\r?\n                    _ = RetireTilingServiceAsync\(removedTiling, collect: true, failures\);\r?\n                \}'
$candidateMatches = [regex]::Matches($candidateText, $candidatePattern)
Require ($candidateMatches.Count -eq 1) 'Candidate display-removal admission block differs.'
$matchedBlock = $candidateMatches[0].Value
$sourceNewline = if ($matchedBlock.Contains("`r`n")) { "`r`n" } else { "`n" }
$baselineBlockNative = $matchedBlock + $sourceNewline + @'
                else
                {
                    m_scheduleGarbageCollection();
                }
'@.Replace("`n", $sourceNewline)

New-Item -ItemType Directory -Path (Join-Path $baselineRoot 'source') -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $candidateSource -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $baselineRoot 'source') -Recurse
}
$baselineProductionPath = Join-Path $baselineRoot "source/$productionPath"
$baselineText = [IO.File]::ReadAllText($baselineProductionPath)
$baselineText = $baselineText.Replace($matchedBlock, $baselineBlockNative)
Require ($baselineText -cne $candidateText -and $baselineText.Contains($baselineBlockNative) -and ([regex]::Matches($baselineText, [regex]::Escape($baselineBlockNative))).Count -eq 1) 'Mechanical baseline transformation failed.'
[IO.File]::WriteAllText($baselineProductionPath, $baselineText, [Text.UTF8Encoding]::new($true))

$baselineManifest = foreach ($record in $candidateManifest) {
    [pscustomobject]@{ Path=$record.Path; SHA256=Hash (Join-Path $baselineRoot "source/$($record.Path)") }
}
$baselineManifestPath = Join-Path $baselineRoot 'manifest.csv'
$baselineManifest | Export-Csv -NoTypeInformation -LiteralPath $baselineManifestPath
$changedSources = @($candidateManifest | Where-Object {
    $baseline = @($baselineManifest | Where-Object Path -CEQ $_.Path)
    $baseline.Count -ne 1 -or $baseline[0].SHA256 -cne $_.SHA256
})
Require ($changedSources.Count -eq 1 -and $changedSources[0].Path -ceq $productionPath) 'Mechanical baseline must differ in one production source only.'

$baselineSnapshot = [ordered]@{
    SnapshotId=$BaselineSnapshotId;BaselineOf=$CandidateSnapshotId;Root=$repositoryRoot;
    Commit=$candidateSnapshot.Commit;Tree=$candidateSnapshot.Tree;Status=$candidateSnapshot.Status;Submodules=$candidateSnapshot.Submodules;
    ManifestSHA256=Hash $baselineManifestPath;MechanicalDelta=$productionPath;
    CandidateProductionSHA256=Hash $candidateProductionPath;BaselineProductionSHA256=Hash $baselineProductionPath;
    Scope='Current OnDisplayRemoved lifecycle with only duplicate/unknown collection scheduling restored.';
    Exclusions=$candidateSnapshot.Exclusions
}
$baselineSnapshot | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $baselineRoot 'snapshot.json')

function Archive-Binaries([string]$SourceRoot, [string]$OutputRoot, [bool]$Restore) {
    $logRoot = Join-Path (Split-Path $OutputRoot) 'validation'
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    if ($Restore) {
        & dotnet restore (Join-Path $SourceRoot 'FancyWM.Tests/FancyWM.Tests.csproj') --runtime win-x64 *> (Join-Path $logRoot 'release-restore.log')
        Require ($LASTEXITCODE -eq 0) 'Mechanical baseline restore failed.'
    }
    & dotnet build (Join-Path $SourceRoot 'FancyWM.Tests/FancyWM.Tests.csproj') --configuration Release --no-restore --no-incremental *> (Join-Path $logRoot 'release-build.log')
    Require ($LASTEXITCODE -eq 0) "Release build failed: $SourceRoot"
    $binarySource = Join-Path $SourceRoot 'FancyWM.Tests/bin/Release/net10.0-windows10.0.18362.0'
    Require (Test-Path (Join-Path $binarySource 'FancyWM.Tests.dll')) 'Release test binary output is missing.'
    New-Item -ItemType Directory -Path $OutputRoot | Out-Null
    Copy-Item -Path (Join-Path $binarySource '*') -Destination $OutputRoot -Recurse
    $records = @(Get-ChildItem -LiteralPath $OutputRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{Path=[IO.Path]::GetRelativePath($OutputRoot,$_.FullName).Replace('\','/');Hash=Hash $_.FullName;Length=$_.Length}
    })
    $records | Export-Csv -NoTypeInformation -LiteralPath ((Split-Path $OutputRoot) + '/release-test-binaries.csv')
    return $records
}

$candidateArchiveRoot = Join-Path $candidateRoot 'release-test-binaries'
Require (!(Test-Path $candidateArchiveRoot)) 'Candidate binary archive already exists.'
$candidateArchive = Archive-Binaries $repositoryRoot $candidateArchiveRoot $false

$developmentRoot = Join-Path $baselineRoot 'development'
New-Item -ItemType Directory -Path $developmentRoot | Out-Null
foreach ($item in Get-ChildItem -LiteralPath (Join-Path $baselineRoot 'source') -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $developmentRoot -Recurse
}
$baselineArchiveRoot = Join-Path $baselineRoot 'release-test-binaries'
$baselineArchive = Archive-Binaries $developmentRoot $baselineArchiveRoot $true

$candidateDll = @($candidateArchive | Where-Object Path -CEQ 'FancyWM.dll')
$candidateTests = @($candidateArchive | Where-Object Path -CEQ 'FancyWM.Tests.dll')
$baselineDll = @($baselineArchive | Where-Object Path -CEQ 'FancyWM.dll')
$baselineTests = @($baselineArchive | Where-Object Path -CEQ 'FancyWM.Tests.dll')
Require ($candidateDll.Count -eq 1 -and $candidateTests.Count -eq 1 -and $baselineDll.Count -eq 1 -and $baselineTests.Count -eq 1 -and
    $candidateDll[0].Hash -cne $baselineDll[0].Hash) 'Production/fixture binary provenance differs.'

$validationRoot = Join-Path $baselineRoot 'validation'
$redRoot = Join-Path $validationRoot 'red-run'
New-Item -ItemType Directory -Path $redRoot | Out-Null
Copy-Item -Path (Join-Path $candidateArchiveRoot '*') -Destination $redRoot -Recurse
Copy-Item -LiteralPath (Join-Path $baselineArchiveRoot 'FancyWM.dll') -Destination (Join-Path $redRoot 'FancyWM.dll') -Force
$redTrx = Join-Path $validationRoot 'baseline-duplicate-removal-red.trx'
$filter = 'FullyQualifiedName=FancyWM.Tests.AlgorithmicLayouts.MultiDisplayTilingServiceIntegrationTest.UnknownAndDuplicateDisplayRemovalsDoNotRequestGarbageCollection'
& dotnet vstest (Join-Path $redRoot 'FancyWM.Tests.dll') /Platform:x64 "/TestCaseFilter:$filter" '/Logger:trx;LogFileName=baseline-duplicate-removal-red.trx' "/ResultsDirectory:$validationRoot" *> (Join-Path $validationRoot 'baseline-duplicate-removal-red.log')
Require ($LASTEXITCODE -eq 1 -and (Test-Path $redTrx)) 'Mechanical baseline regression did not fail as expected.'
[xml]$red = Get-Content -Raw $redTrx
$redLeaves = @($red.SelectNodes('//*[local-name()="UnitTestResult"]') | Where-Object { $_.SelectNodes('./*[local-name()="InnerResults"]/*[local-name()="UnitTestResult"]').Count -eq 0 })
Require ($redLeaves.Count -eq 1 -and $redLeaves[0].outcome -ceq 'Failed' -and $redLeaves[0].testName -ceq 'UnknownAndDuplicateDisplayRemovalsDoNotRequestGarbageCollection') 'Mechanical baseline red result differs.'

$candidateProvenance = [ordered]@{
    SnapshotId=$CandidateSnapshotId;BaselineSnapshotId=$BaselineSnapshotId;Verdict='PREPARED';Configuration='Release';Architecture='x64 testhost';
    ArchiveManifestSHA256=Hash (Join-Path $candidateRoot 'release-test-binaries.csv');FancyWMDllSHA256=$candidateDll[0].Hash;FancyWMTestsDllSHA256=$candidateTests[0].Hash;
    ProductionSourceSHA256=Hash $candidateProductionPath;FixtureSourceSHA256=Hash (Join-Path $candidateSource 'FancyWM.Tests/AlgorithmicLayouts/MultiDisplayTilingServiceIntegrationTest.GcPause.cs');
    BaselineFancyWMDllSHA256=$baselineDll[0].Hash;BaselineRedTrxSHA256=Hash $redTrx;CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')
}
$candidateProvenance | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $candidateRoot 'release-test-binaries.provenance.json')
$baselineProvenance = [ordered]@{
    SnapshotId=$BaselineSnapshotId;CandidateSnapshotId=$CandidateSnapshotId;Verdict='MECHANICAL_BASELINE_PREPARED';ChangedSources=@($productionPath);
    ArchiveManifestSHA256=Hash (Join-Path $baselineRoot 'release-test-binaries.csv');FancyWMDllSHA256=$baselineDll[0].Hash;FancyWMTestsDllSHA256=$baselineTests[0].Hash;
    ProductionSourceSHA256=Hash $baselineProductionPath;CandidateProductionSourceSHA256=Hash $candidateProductionPath;
    RedRegression='UnknownAndDuplicateDisplayRemovalsDoNotRequestGarbageCollection';RedTrxSHA256=Hash $redTrx;CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')
}
$baselineProvenance | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $baselineRoot 'release-test-binaries.provenance.json')
$candidateProvenance | ConvertTo-Json -Depth 5
