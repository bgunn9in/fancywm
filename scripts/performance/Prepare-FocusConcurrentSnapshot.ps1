param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId,
    [Parameter(Mandatory)][ValidateSet('Baseline', 'Candidate')][string]$Role
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Hash([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Load-Trx([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    }
    finally { $reader.Dispose() }
}

function Verify-Regression([string]$Path, [bool]$ExpectedRed) {
    $document = Load-Trx $Path
    $root = $document.DocumentElement
    Require ($root.LocalName -ceq 'TestRun' -and $root.NamespaceURI -ceq 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010') 'Invalid regression TRX root.'
    $leaves = @($root.SelectNodes('.//*[local-name()="UnitTestResult"]') | Where-Object {
        $_.SelectNodes('./*[local-name()="InnerResults"]/*[local-name()="UnitTestResult"]').Count -eq 0
    })
    $expected = @(
        'ConcurrentEnqueuesWaitForSharedGateAndReplaceExactlyOnePendingWinner',
        'OverlappedWorkerAdmissionsPreserveLatestOwnershipAndCompletionOrder (direct-success)',
        'OverlappedWorkerAdmissionsPreserveLatestOwnershipAndCompletionOrder (alt-retry)',
        'OverlappedWorkerAdmissionsPreserveLatestOwnershipAndCompletionOrder (attach-failure)',
        'ConcurrentWorkerTransitionsStayWithinAllocationBudget (direct-success)',
        'ConcurrentWorkerTransitionsStayWithinAllocationBudget (alt-retry)',
        'ConcurrentWorkerTransitionsStayWithinAllocationBudget (attach-failure)',
        'FocusConcurrentAdmissionCounterScenario'
    )
    Require ($leaves.Count -eq 8 -and @($leaves.testName | Sort-Object) -join '|' -ceq @($expected | Sort-Object) -join '|') 'Regression leaf set differs.'
    $allocation = @($leaves | Where-Object testName -CLike 'ConcurrentWorkerTransitionsStayWithinAllocationBudget*')
    $semantic = @($leaves | Where-Object testName -CNotLike 'ConcurrentWorkerTransitionsStayWithinAllocationBudget*')
    Require ($allocation.Count -eq 3 -and $semantic.Count -eq 5 -and @($semantic | Where-Object outcome -CNE 'Passed').Count -eq 0) 'Semantic regression result differs.'
    if ($ExpectedRed) {
        Require (@($allocation | Where-Object outcome -CNE 'Failed').Count -eq 0) 'Baseline allocation regression must fail exactly three leaves.'
        foreach ($leaf in $allocation) {
            $message = @($leaf.SelectNodes('./*[local-name()="Output"]/*[local-name()="ErrorInfo"]/*[local-name()="Message"]'))
            Require ($message.Count -eq 1 -and $message[0].InnerText -cmatch 'RunWorker allocates above its per-transition budget: mode=(direct-success|alt-retry|attach-failure), total=6192 B, per-transition=98[,.]286 B, budget=32 B/transition\.') 'Baseline failed for a reason other than the measured worker-transition allocation.'
        }
    }
    else {
        Require (@($allocation | Where-Object outcome -CNE 'Passed').Count -eq 0) 'Candidate allocation regression did not pass.'
    }
    [pscustomobject]@{
        Leaves = 8
        Passed = @($leaves | Where-Object outcome -CEQ 'Passed').Count
        Failed = @($leaves | Where-Object outcome -CEQ 'Failed').Count
        RunId = $root.id
    }
}

$rootOutput = @(& git -C $PSScriptRoot rev-parse --show-toplevel)
Require ($LASTEXITCODE -eq 0) 'Cannot resolve repository root.'
$repositoryRoot = [IO.Path]::GetFullPath(($rootOutput -join '').Trim())
Require ([StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath((Get-Location).Path), $repositoryRoot)) 'Run from repository root.'
Require ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [Runtime.InteropServices.Architecture]::X64) 'Use x64 PowerShell.'

$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$sourceRoot = Join-Path $snapshotRoot 'source'
$manifestPath = Join-Path $snapshotRoot 'manifest.csv'
$snapshotPath = Join-Path $snapshotRoot 'snapshot.json'
Require ((Test-Path -LiteralPath $manifestPath) -and (Test-Path -LiteralPath $snapshotPath)) 'Snapshot metadata is missing.'
$manifest = @(Import-Csv -LiteralPath $manifestPath)
$snapshot = Get-Content -Raw -LiteralPath $snapshotPath | ConvertFrom-Json
Require ($snapshot.SnapshotId -ceq $SnapshotId -and $snapshot.ManifestSHA256 -ceq (Hash $manifestPath)) 'Snapshot identity differs.'
$head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$tree = (& git -C $repositoryRoot rev-parse 'HEAD^{tree}').Trim()
Require ($head -ceq $snapshot.Commit -and $tree -ceq $snapshot.Tree) 'HEAD/tree differs from snapshot.'
$sourceHashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
foreach ($record in $manifest) {
    Require ($record.Path -cnotmatch '\\|(^|/)(\.\.?)(/|$)' -and $record.SHA256 -cmatch '^[0-9A-F]{64}$' -and $sourceHashes.TryAdd($record.Path, $record.SHA256)) "Malformed manifest row: $($record.Path)"
    Require ((Hash (Join-Path $sourceRoot $record.Path)) -ceq $record.SHA256) "Frozen source changed: $($record.Path)"
}
$required = @(
    'FancyWM/Utilities/FocusHelper.cs',
    'FancyWM.Tests/Utilities/FocusHelperTest.ConcurrentAdmission.cs',
    'FancyWM.Tests/Utilities/FocusHelperTest.WorkerAdmission.cs',
    'scripts/performance/Prepare-FocusConcurrentSnapshot.ps1',
    'scripts/performance/Measure-FocusConcurrentAdmission.ps1',
    'scripts/performance/Verify-FocusConcurrentAdmission.ps1',
    'scripts/performance/Append-FocusConcurrentMeasurements.ps1'
)
foreach ($path in $required) { Require ($sourceHashes.ContainsKey($path)) "Required frozen input missing: $path" }
Require ((Hash $PSCommandPath) -ceq $sourceHashes['scripts/performance/Prepare-FocusConcurrentSnapshot.ps1']) 'Executing prepare script differs from frozen input.'

$focusSource = Get-Content -Raw -LiteralPath (Join-Path $sourceRoot 'FancyWM/Utilities/FocusHelper.cs')
if ($Role -ceq 'Baseline') {
    Require ($focusSource.Contains('() => IsCurrent(request.Token)') -and !$focusSource.Contains('RunFallback(request.Window, request.Native, this, request.Token')) 'Baseline worker current-state callback shape differs.'
}
else {
    Require (!$focusSource.Contains('() => IsCurrent(request.Token)') -and $focusSource.Contains('RunFallback(request.Window, request.Native, this, request.Token')) 'Candidate worker current-state callback shape differs.'
}

$roleName = $Role.ToLowerInvariant()
$buildRoot = Join-Path $snapshotRoot 'build-workspace'
$archiveRoot = Join-Path $snapshotRoot 'release-test-binaries'
$archiveManifestPath = Join-Path $snapshotRoot 'release-test-binaries.csv'
$provenancePath = Join-Path $snapshotRoot 'release-test-binaries.provenance.json'
$validationRoot = Join-Path $snapshotRoot 'validation'
foreach ($path in @($buildRoot, $archiveRoot, $archiveManifestPath, $provenancePath, $validationRoot)) {
    Require (!(Test-Path -LiteralPath $path)) "Immutable prepare output already exists: $path"
}
New-Item -ItemType Directory -Path $buildRoot | Out-Null
New-Item -ItemType Directory -Path $validationRoot | Out-Null
foreach ($record in $manifest) {
    $destination = Join-Path $buildRoot $record.Path
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot $record.Path) -Destination $destination
}

$project = Join-Path $buildRoot 'FancyWM.Tests/FancyWM.Tests.csproj'
$restoreLog = Join-Path $validationRoot "$roleName-release-restore.log"
$buildLog = Join-Path $validationRoot "$roleName-release-build.log"
$testLog = Join-Path $validationRoot "$roleName-focus-concurrent-regression.log"
$trxName = "$roleName-focus-concurrent-regression.trx"
$trxPath = Join-Path $validationRoot $trxName
$dotnetInfo = Join-Path $validationRoot "$roleName-dotnet-info.log"
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
& $dotnet --info *> $dotnetInfo
Require ($LASTEXITCODE -eq 0) 'dotnet --info failed.'
& $dotnet restore $project --runtime win-x64 --force --no-cache *> $restoreLog
Require ($LASTEXITCODE -eq 0) 'Isolated Release restore failed.'
& $dotnet build $project --configuration Release --runtime win-x64 --no-restore --no-incremental *> $buildLog
Require ($LASTEXITCODE -eq 0) 'Isolated Release build failed.'
$methods = @(
    'ConcurrentEnqueuesWaitForSharedGateAndReplaceExactlyOnePendingWinner',
    'OverlappedWorkerAdmissionsPreserveLatestOwnershipAndCompletionOrder',
    'ConcurrentWorkerTransitionsStayWithinAllocationBudget',
    'FocusConcurrentAdmissionCounterScenario'
)
$filter = ($methods | ForEach-Object { "FullyQualifiedName=FancyWM.Tests.Utilities.FocusHelperTest.$_" }) -join '|'
$testArguments = @('test', $project, '--configuration', 'Release', '--runtime', 'win-x64', '--no-restore', '--no-build', '--filter', $filter, '--logger', "trx;LogFileName=$trxName", '--results-directory', $validationRoot)
& $dotnet @testArguments *> $testLog
$testExit = $LASTEXITCODE
$expectedExit = if ($Role -ceq 'Baseline') { 1 } else { 0 }
Require ($testExit -eq $expectedExit -and (Test-Path -LiteralPath $trxPath)) "Regression exit differs: expected $expectedExit, got $testExit"
$regression = Verify-Regression $trxPath ($Role -ceq 'Baseline')

$framework = 'net10.0-windows10.0.18362.0'
$binaryRoot = Join-Path $buildRoot "FancyWM.Tests/bin/Release/$framework/win-x64"
if (!(Test-Path -LiteralPath (Join-Path $binaryRoot 'FancyWM.Tests.dll'))) {
    $binaryRoot = Join-Path $buildRoot "FancyWM.Tests/bin/Release/$framework"
}
Require ((Test-Path -LiteralPath (Join-Path $binaryRoot 'FancyWM.Tests.dll')) -and (Test-Path -LiteralPath (Join-Path $binaryRoot 'FancyWM.dll'))) 'Release test binaries missing.'
New-Item -ItemType Directory -Path $archiveRoot | Out-Null
$archiveManifest = @(Get-ChildItem -LiteralPath $binaryRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($binaryRoot, $_.FullName).Replace('\', '/')
    $destination = Join-Path $archiveRoot $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination
    [pscustomobject]@{ Path = $relative; Hash = Hash $destination; Length = $_.Length }
})
Require (@($archiveManifest | Where-Object Path -CEQ 'FancyWM.dll').Count -eq 1 -and @($archiveManifest | Where-Object Path -CEQ 'FancyWM.Tests.dll').Count -eq 1) 'Archived production/fixture binaries are not unique.'
$archiveManifest | Export-Csv -NoTypeInformation -LiteralPath $archiveManifestPath
$production = @($archiveManifest | Where-Object Path -CEQ 'FancyWM.dll')[0]
$fixture = @($archiveManifest | Where-Object Path -CEQ 'FancyWM.Tests.dll')[0]
$evidence = @($restoreLog, $buildLog, $testLog, $trxPath, $dotnetInfo | ForEach-Object {
    [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($snapshotRoot, $_).Replace('\', '/'); SHA256 = Hash $_ }
})
$provenance = [ordered]@{
    SnapshotId = $SnapshotId
    Role = $Role
    Commit = $head
    Tree = $tree
    ManifestSHA256 = Hash $manifestPath
    SourceFileCount = $manifest.Count
    PrepareScriptSHA256 = Hash $PSCommandPath
    ProductionSourceSHA256 = $sourceHashes['FancyWM/Utilities/FocusHelper.cs']
    FixtureSourceSHA256 = $sourceHashes['FancyWM.Tests/Utilities/FocusHelperTest.ConcurrentAdmission.cs']
    FancyWMDllSHA256 = $production.Hash
    FancyWMTestsDllSHA256 = $fixture.Hash
    ArchiveManifestSHA256 = Hash $archiveManifestPath
    ArchiveFileCount = $archiveManifest.Count
    Regression = $regression
    RegressionExpectedRed = $Role -ceq 'Baseline'
    Evidence = $evidence
}
$provenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $provenancePath
foreach ($record in $manifest) { Require ((Hash (Join-Path $sourceRoot $record.Path)) -ceq $record.SHA256) "Frozen source changed during prepare: $($record.Path)" }
$provenance | ConvertTo-Json -Depth 6
