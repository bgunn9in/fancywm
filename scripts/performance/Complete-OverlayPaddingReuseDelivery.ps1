param([Parameter(Mandatory)][string]$SnapshotId, [switch]$NestedNotifications)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function HashFile([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$artifactRoot = Join-Path $repositoryRoot 'artifacts/performance'
$root = Join-Path $artifactRoot $SnapshotId
$validation = Join-Path $root 'validation'
$summaryPath = Join-Path $validation 'validation-summary.json'
Require (!(Test-Path -LiteralPath $summaryPath)) 'Summary already exists.'
$driverLog = Join-Path $artifactRoot $(if ($NestedNotifications) { 'overlay-nested-c2-validation.log' } else { 'overlay-padding-c2-validation.log' })
$driverText = Get-Content -LiteralPath $driverLog -Raw
foreach ($configuration in @('Debug','Release')) {
    Require ($driverText.Contains("$configuration GUI, full x64 package and all test projects passed.")) "$configuration validation did not complete."
}
Copy-Item -LiteralPath $driverLog -Destination (Join-Path $validation 'driver.log')
$tests = foreach ($configuration in @('Debug','Release')) {
    foreach ($project in @('FancyWM.Tests','FancyWM.Layouts.Tests','FancyWM.ThemeEngine.Tests')) {
        $trxPath = Join-Path $validation "$configuration-$project.trx"
        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
        $leaves = @($trx.SelectNodes("//*[local-name()='UnitTestResult']") | Where-Object { $_.SelectNodes("./*[local-name()='InnerResults']/*[local-name()='UnitTestResult']").Count -eq 0 })
        $expected = if ($project -eq 'FancyWM.Tests') { if ($configuration -eq 'Debug') { 2047 } else { 2103 } } elseif ($project -eq 'FancyWM.Layouts.Tests') { 160 } else { 31 }
        if ($NestedNotifications -and $project -eq 'FancyWM.Tests') { $expected += 3 }
        Require ($leaves.Count -eq $expected -and @($leaves | Where-Object outcome -CNE 'Passed').Count -eq 0) "Unexpected full test outcome: $configuration $project"
        if ($project -eq 'FancyWM.Tests') { Require (@($leaves | Where-Object testName -Like 'OverlayPadding*').Count -eq 4) 'Missing padding tests.' }
        if ($NestedNotifications -and $project -eq 'FancyWM.Tests') { Require (@($leaves | Where-Object testName -Like 'OverlayNested*').Count -eq 3) 'Missing nested-notification tests.' }
        [pscustomobject]@{ Configuration = $configuration; Project = $project; Passed = $leaves.Count; Failed = 0; TrxSHA256 = HashFile $trxPath }
    }
}
$frozenFiles = 0
foreach ($entry in Import-Csv (Join-Path $root 'manifest.csv')) {
    Require ((HashFile (Join-Path $root "source/$($entry.Path)")) -ceq $entry.SHA256) "Frozen source changed: $($entry.Path)"
    $frozenFiles++
}
$candidateRoot = Join-Path $artifactRoot $(if ($NestedNotifications) { 'FWM-OVERLAY-NESTED-NOTIFICATION-20260912-C1' } else { 'FWM-OVERLAY-PADDING-REUSE-20260912-C1' })
$candidateSource = @{}; Import-Csv (Join-Path $candidateRoot 'manifest.csv') | ForEach-Object { $candidateSource[$_.Path] = $_.SHA256 }
foreach ($entry in Import-Csv (Join-Path $root 'manifest.csv')) {
    if ($entry.Path -match '^(FancyWM[^/]*|winman|winman-windows|ModernWpf)/') {
        Require ($candidateSource[$entry.Path] -ceq $entry.SHA256) "Candidate/delivery source differs: $($entry.Path)"
        Require ((HashFile (Join-Path $repositoryRoot $entry.Path)) -ceq $entry.SHA256) "Live executable/test source differs: $($entry.Path)"
    }
}
$measurementRoot = Join-Path $artifactRoot $(if ($NestedNotifications) { 'FWM-OVERLAY-NESTED-NOTIFICATION-20260912-M1' } else { 'FWM-OVERLAY-PADDING-REUSE-20260912-M2' })
$reverification = Join-Path $validation 'measurement-reverification.json'
& python (Join-Path $PSScriptRoot 'Verify-OverlayPaddingReuse.py') $measurementRoot --output $reverification *> (Join-Path $validation 'measurement-reverification.log')
Require ($LASTEXITCODE -eq 0) 'Independent measurement reverification failed.'
$measurement = Get-Content -LiteralPath $reverification -Raw | ConvertFrom-Json
Require ($measurement.Verdict -eq 'PASS') 'Measurement verdict differs.'
$packageRelative = 'FancyWM.Package/AppPackages/FancyWM.Package_0.0.0.0_x64_Test/FancyWM.Package_0.0.0.0_x64.msix'
$packageSource = Join-Path $repositoryRoot $packageRelative
$buildLog = Get-Content -LiteralPath (Join-Path $validation 'Release-full.log') -Raw
Require ($buildLog.Contains($packageSource.Replace('/','\'))) 'Package path absent from successful Release build log.'
$releaseRoot = Join-Path $root 'release'
Require (!(Test-Path -LiteralPath $releaseRoot)) 'Release archive already exists.'
New-Item -ItemType Directory -Path $releaseRoot | Out-Null
$packageArchive = Join-Path $releaseRoot 'FancyWM.Package_0.0.0.0_x64.msix'
Copy-Item -LiteralPath $packageSource -Destination $packageArchive
Require ((HashFile $packageArchive) -ceq (HashFile $packageSource)) 'Package copy differs.'
$zip = [IO.Compression.ZipFile]::OpenRead($packageArchive)
try {
    $entry = $zip.GetEntry('FancyWM.GUI/FancyWM.dll')
    Require ($null -ne $entry) 'Embedded FancyWM.dll missing.'
    $stream = $entry.Open()
    $memory = [IO.MemoryStream]::new()
    try { $stream.CopyTo($memory); $dllBytes = $memory.ToArray() } finally { $stream.Dispose(); $memory.Dispose() }
    $embeddedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($dllBytes))
    $machine = [BitConverter]::ToUInt16($dllBytes, [BitConverter]::ToInt32($dllBytes,0x3c) + 4)
    Require ($machine -eq 0x8664) 'Embedded DLL is not AMD64.'
    $ridDll = Join-Path $repositoryRoot 'FancyWM/bin/x64/Release/net10.0-windows10.0.18362.0/win-x64/FancyWM.dll'
    Require ($embeddedHash -ceq (HashFile $ridDll)) 'Embedded DLL differs from built RID DLL.'
    $reader = [IO.StreamReader]::new($zip.GetEntry('AppxManifest.xml').Open())
    try { [xml]$appx = $reader.ReadToEnd() } finally { $reader.Dispose() }
    Require ($appx.Package.Identity.ProcessorArchitecture -ceq 'x64') 'Package architecture differs.'
} finally { $zip.Dispose() }
$dependencyLog = Join-Path $root 'dependency-replay.log'
$dependencyText = Get-Content -LiteralPath $dependencyLog -Raw
Require ($dependencyText.Contains('Unknown local hunk rejected and preserved') -and $dependencyText.Contains('Clean pinned archive applies once')) 'Dependency replay incomplete.'
$dependencyBinary = @(Import-Csv (Join-Path $root 'dependency-check/binary.csv'))
Require ($dependencyBinary.Count -eq 1 -and (HashFile $dependencyBinary[0].Path) -ceq $dependencyBinary[0].Hash) 'Dependency binary changed.'
$ledger = Join-Path $repositoryRoot 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv'
$ledgerHash = HashFile $ledger
Require ($ledgerHash -ceq '84C1D3A2A255F2B4963F1A30960455A56501527B297DB76B4A7E8984FF001F11' -and (Get-Item -LiteralPath $ledger).Length -eq 41782945) 'Historical ledger changed.'
$logs = @(Get-ChildItem -LiteralPath $validation -File -Filter '*.log' | Sort-Object Name | ForEach-Object { [pscustomobject]@{ File = $_.Name; SHA256 = HashFile $_.FullName } })
[ordered]@{
    Verdict = 'LOCAL_DELIVERY_PASS_NATIVE_PENDING'
    SnapshotId = $SnapshotId
    FrozenSourceFiles = $frozenFiles
    SourceManifestSHA256 = HashFile (Join-Path $root 'manifest.csv')
    CandidateExecutableAndTestSourcesUnchanged = $true
    FullTests = @($tests)
    ValidationLogs = $logs
    MeasurementReverificationSHA256 = HashFile $reverification
    DependencyReplaySHA256 = HashFile $dependencyLog
    DependencyBinarySHA256 = $dependencyBinary[0].Hash
    ReleasePackage = [ordered]@{
        Path = 'release/FancyWM.Package_0.0.0.0_x64.msix'; Source = $packageRelative
        Bytes = (Get-Item -LiteralPath $packageArchive).Length; SHA256 = HashFile $packageArchive
        EmbeddedPath = 'FancyWM.GUI/FancyWM.dll'; EmbeddedBytes = $dllBytes.Length
        EmbeddedSHA256 = $embeddedHash; Architecture = 'x64'; EmbeddedMachine = 'AMD64'
        Installed = $false; Launched = $false; Published = $false
    }
    Ledger = [ordered]@{ Rows = 41325; Bytes = 41782945; SHA256 = $ledgerHash; Appended = $false }
    NativeBehavior = 'E2E_PENDING'; NativeCpu = 'NOT_MEASURED'; NativeGpu = 'NOT_MEASURED'; NativePresentation = 'E2E_PENDING'
    NestedNotificationRegression = $(if ($NestedNotifications) { 'NO_HWND_VERIFIED' } else { 'BASELINE_GAP_RETAINED' })
    SummaryWriterSHA256 = HashFile $PSCommandPath
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath
Get-Content -LiteralPath $summaryPath
