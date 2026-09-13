param(
    [Parameter(Mandatory)][string]$BaselineSnapshotId,
    [Parameter(Mandatory)][string]$CandidateSnapshotId,
    [Parameter(Mandatory)][string]$ExperimentId,
    [ValidateSet('Layout', 'RecentTimer', 'DesktopLiveness')][string]$Path = 'Layout'
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = (git rev-parse --show-toplevel).Trim()
$baselineRoot = Join-Path $repositoryRoot "artifacts/performance/$BaselineSnapshotId"
$candidateRoot = Join-Path $repositoryRoot "artifacts/performance/$CandidateSnapshotId"
$experimentRoot = Join-Path $candidateRoot $ExperimentId
if (Test-Path -LiteralPath $experimentRoot) { throw "Experiment already exists: $experimentRoot" }
foreach ($snapshotRoot in @($baselineRoot, $candidateRoot)) {
    foreach ($record in (Import-Csv (Join-Path $snapshotRoot 'manifest.csv'))) {
        if ($record.Path -match '^(FancyWM.Layouts/|winman/|winman-windows/|scripts/performance/).+\.(cs|csproj|props|targets|txt|json|ps1)$') {
            $sourcePath = Join-Path "$snapshotRoot/source" $record.Path
            if ((Get-FileHash -LiteralPath $sourcePath).Hash -ne $record.SHA256) { throw "Snapshot changed: $sourcePath" }
            if ($snapshotRoot -eq $candidateRoot -and (Get-FileHash -LiteralPath (Join-Path $repositoryRoot $record.Path)).Hash -ne $record.SHA256) {
                throw "Candidate differs from snapshot: $($record.Path)"
            }
        }
    }
}
New-Item -ItemType Directory -Path $experimentRoot | Out-Null
dotnet build scripts/performance/FancyWM.PerformanceHarness.csproj --configuration Release *> "$experimentRoot/candidate-build.log"
if ($LASTEXITCODE) { throw 'Candidate harness build failed' }
$baselineProject = if ($Path -eq 'Layout') { 'scripts/performance/baseline/LayoutBaseline.csproj' } else { 'scripts/performance/baseline/winman-windows/WinManWindowsBaseline.csproj' }
$baselineBinary = if ($Path -eq 'Layout') { 'scripts/performance/baseline/bin/Release/net10.0/FancyWM.Layouts.dll' } else { 'scripts/performance/baseline/winman-windows/bin/Release/net10.0-windows7.0/WinMan.Windows.dll' }
$binaryName = Split-Path $baselineBinary -Leaf
dotnet build $baselineProject --configuration Release "--property:BaselineSourceRoot=$baselineRoot/source" *> "$experimentRoot/baseline-build.log"
if ($LASTEXITCODE) { throw 'Baseline source build failed' }
foreach ($variant in @('baseline', 'candidate')) {
    New-Item -ItemType Directory -Path "$experimentRoot/$variant" | Out-Null
    Copy-Item -Path scripts/performance/bin/Release/net10.0-windows7.0/* -Destination "$experimentRoot/$variant"
}
Copy-Item -LiteralPath $baselineBinary -Destination "$experimentRoot/baseline/$binaryName"
$binaryManifest = foreach ($variant in @('baseline', 'candidate')) {
    foreach ($binary in Get-ChildItem -LiteralPath "$experimentRoot/$variant" -File) {
        [pscustomobject]@{Variant=$variant; File=$binary.Name; SHA256=(Get-FileHash -LiteralPath $binary.FullName).Hash}
    }
}
$binaryManifest | Export-Csv -NoTypeInformation "$experimentRoot/binaries.csv"
$previousTiering = $env:DOTNET_TieredCompilation
$previousSnapshot = $env:FWM_PERF_SNAPSHOT
$previousTrace = $env:FWM_PERF_LAYOUT_TRACE
try {
    $env:DOTNET_TieredCompilation = '0'
    $env:FWM_PERF_LAYOUT_TRACE = '0'
    if ($Path -eq 'Layout') {
        foreach ($variant in @('baseline', 'candidate')) {
            dotnet "$experimentRoot/$variant/FancyWM.PerformanceHarness.dll" --verify-layout > "$experimentRoot/$variant-digest.txt"
            if ($LASTEXITCODE) { throw "Mutation replay failed: $variant" }
        }
        if (Compare-Object (Get-Content "$experimentRoot/baseline-digest.txt") (Get-Content "$experimentRoot/candidate-digest.txt")) {
            throw 'Baseline/candidate mutation digests differ'
        }
    }
    $scenarioArgument = if ($Path -eq 'Layout') { '--layouts-only' } elseif ($Path -eq 'RecentTimer') { '--recent-only' } else { '--desktop-liveness-only' }
    $measurements = foreach ($pair in 1..5) {
        $variants = if ($pair % 2) { @('baseline', 'candidate') } else { @('candidate', 'baseline') }
        foreach ($variant in $variants) {
            $env:FWM_PERF_SNAPSHOT = if ($variant -eq 'baseline') { $BaselineSnapshotId } else { $CandidateSnapshotId }
            dotnet "$experimentRoot/$variant/FancyWM.PerformanceHarness.dll" $scenarioArgument > "$experimentRoot/$variant-$pair.csv"
            if ($LASTEXITCODE) { throw "Measurement failed: $variant pair $pair" }
            foreach ($row in Import-Csv "$experimentRoot/$variant-$pair.csv") {
                if ($row.experiment_id -like '*GUARD-MODEL') { continue }
                $pathKind = if ($Path -eq 'RecentTimer') { 'RECENT' } elseif ($Path -eq 'DesktopLiveness') { 'DESKTOP-LIVENESS' } elseif ($row.experiment_id -eq 'EXP-LAYOUT-STABLE-PASS') { 'LAYOUT' } else { 'FLEX' }
                $row.experiment_id = "$ExperimentId-$pathKind"
                $row | Add-Member -NotePropertyName perf_id -NotePropertyValue $(if ($Path -eq 'Layout') { 'PERF-005' } elseif ($Path -eq 'RecentTimer') { 'PERF-001' } else { 'PERF-002' })
                $row | Add-Member -NotePropertyName variant -NotePropertyValue $variant
                $row | Add-Member -NotePropertyName pair -NotePropertyValue $pair
                $row | Add-Member -NotePropertyName binary_sha256 -NotePropertyValue ($binaryManifest | Where-Object { $_.Variant -eq $variant -and $_.File -eq $binaryName }).SHA256
                $row | Add-Member -NotePropertyName benchmark_sha256 -NotePropertyValue ($binaryManifest | Where-Object { $_.Variant -eq $variant -and $_.File -eq 'FancyWM.PerformanceHarness.dll' }).SHA256
                $row
            }
        }
    }
    $measurements | Export-Csv -NoTypeInformation "$experimentRoot/measurements.csv"
    Write-Output "$Path comparison; five alternating process pairs; $($measurements.Count) measurement rows."
    Write-Output "$experimentRoot/measurements.csv"
}
finally {
    $env:DOTNET_TieredCompilation = $previousTiering
    $env:FWM_PERF_SNAPSHOT = $previousSnapshot
    $env:FWM_PERF_LAYOUT_TRACE = $previousTrace
}
