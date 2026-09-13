[CmdletBinding()]
param(
    [ValidateRange(0, 1000)]
    [int]$WarmupCount = 3,

    [ValidateRange(1, 1000)]
    [int]$RunCount = 12,

    [string]$AssemblyPath,

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$SnapshotId = 'UNSPECIFIED-CURRENT-SOURCE',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$ExperimentId = 'UI-THEME-CONVERT-001',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$BaselineSnapshotId,

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$CandidateSnapshotId,

    [string]$BaselineAssemblyPath,
    [string]$BaselineBinaryManifestPath,
    [switch]$Append,
    [string]$OutputPath,
    [ValidateSet('unspecified', 'baseline', 'candidate')]
    [string]$Variant = 'unspecified',
    [ValidateRange(0, 5)][int]$Pair = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedHtmlSha256 = 'd12a96805de60a3ff2df2c5e5450bd559d9cfea826681a7e86d049010f1ddadd'
$expectedCssSha256 = '4b9aba64593667689d8ff551043713d2aee4e9f29ccdc9be492daf0cbfa1b929'
$invariantCulture = [System.Globalization.CultureInfo]::InvariantCulture

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = (Resolve-Path (Join-Path $scriptDirectory '..\..')).Path
$benchmarkSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()

function Get-VerifiedSnapshot {
    param([string]$Id, [switch]$Current)

    $snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$Id"
    $manifestPath = Join-Path $snapshotRoot 'manifest.csv'
    $metadata = Get-Content -LiteralPath (Join-Path $snapshotRoot 'snapshot.json') -Raw | ConvertFrom-Json
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath).Hash
    if ($metadata.SnapshotId -ne $Id -or $manifestHash -ne $metadata.ManifestSHA256) {
        throw "Snapshot identity/manifest checksum mismatch: $Id"
    }
    $records = @(Import-Csv -LiteralPath $manifestPath | Where-Object {
        $_.Path -match '^FancyWM.ThemeEngine/.*\.(cs|csproj)$' -or
        ($Current -and $_.Path -match '^(FancyWM.ThemeEngine.Tests/.*\.(cs|csproj)|scripts/performance/Measure-ThemeConversion\.ps1)$')
    })
    if ('FancyWM.ThemeEngine/StyledDocument.cs' -notin $records.Path -or
        'FancyWM.ThemeEngine/Wpf/CssToWpfResourceConverter.cs' -notin $records.Path -or
        ($Current -and 'scripts/performance/Measure-ThemeConversion.ps1' -notin $records.Path)) {
        throw "Snapshot omits required production/benchmark sources: $Id"
    }
    foreach ($record in $records) {
        if ((Get-FileHash -LiteralPath (Join-Path "$snapshotRoot/source" $record.Path)).Hash -ne $record.SHA256 -or
            ($Current -and (Get-FileHash -LiteralPath (Join-Path $repositoryRoot $record.Path)).Hash -ne $record.SHA256)) {
            throw "Source checksum mismatch for $Id : $($record.Path)"
        }
    }
    return $manifestHash.ToLowerInvariant()
}

if ($BaselineSnapshotId -or $CandidateSnapshotId) {
    if (!$BaselineSnapshotId -or !$CandidateSnapshotId -or $AssemblyPath -or $OutputPath -or $Pair -ne 0 -or !$PSBoundParameters.ContainsKey('ExperimentId')) {
        throw 'Comparison requires both snapshot IDs and an explicit ExperimentId; AssemblyPath, OutputPath and Pair belong to single-process mode.'
    }
    $baselineManifestHash = Get-VerifiedSnapshot $BaselineSnapshotId
    $candidateManifestHash = Get-VerifiedSnapshot $CandidateSnapshotId -Current
    $baselineRoot = Join-Path $repositoryRoot "artifacts/performance/$BaselineSnapshotId"
    if (!$BaselineAssemblyPath) {
        $archiveName = if (Test-Path -LiteralPath "$baselineRoot/theme-baseline/FancyWM.ThemeEngine.dll") { 'theme-baseline' } else { 'release-test-binaries' }
        $BaselineAssemblyPath = Join-Path $baselineRoot "$archiveName/FancyWM.ThemeEngine.dll"
        if (!$BaselineBinaryManifestPath) {
            $BaselineBinaryManifestPath = Join-Path $baselineRoot $(if ($archiveName -eq 'theme-baseline') { 'theme-baseline-hashes.csv' } else { 'release-test-binaries.csv' })
        }
    }
    if (!$BaselineBinaryManifestPath) { throw 'An explicit baseline assembly requires its archived binary hash manifest.' }
    $baselineBinaryRecords = @(Import-Csv -LiteralPath $BaselineBinaryManifestPath)
    $requiredBinaries = @('FancyWM.ThemeEngine.dll', 'AngleSharp.dll', 'AngleSharp.Css.dll')
    foreach ($name in $requiredBinaries) {
        $records = @($baselineBinaryRecords | Where-Object { (Split-Path $_.Path -Leaf) -eq $name })
        $path = Join-Path (Split-Path $BaselineAssemblyPath) $name
        if ($records.Count -ne 1 -or (Get-FileHash -LiteralPath $path).Hash -ne $records[0].Hash) {
            throw "Baseline binary checksum mismatch: $name"
        }
    }
    $experimentRoot = Join-Path $repositoryRoot "artifacts/performance/$CandidateSnapshotId/$ExperimentId"
    if (Test-Path -LiteralPath $experimentRoot) { throw "Experiment already exists: $experimentRoot" }
    New-Item -ItemType Directory -Path $experimentRoot | Out-Null
    dotnet build (Join-Path $repositoryRoot 'FancyWM.ThemeEngine.Tests/FancyWM.ThemeEngine.Tests.csproj') --configuration Release --no-restore *> "$experimentRoot/build.log"
    if ($LASTEXITCODE) { throw "Fresh Release ThemeEngine build failed: $experimentRoot/build.log" }
    $candidateOutput = Join-Path $repositoryRoot 'FancyWM.ThemeEngine.Tests/bin/Release/net10.0-windows10.0.18362.0'
    # Both variants receive exactly the same dependencies; only the production
    # ThemeEngine assembly differs. Baseline dependency hashes must agree.
    foreach ($name in $requiredBinaries | Where-Object { $_ -ne 'FancyWM.ThemeEngine.dll' }) {
        if ((Get-FileHash -LiteralPath (Join-Path $candidateOutput $name)).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path (Split-Path $BaselineAssemblyPath) $name)).Hash) {
            throw "Dependency changed between variants: $name"
        }
    }
    foreach ($variantName in @('baseline', 'candidate')) {
        $binaryDirectory = Join-Path $experimentRoot $variantName
        New-Item -ItemType Directory -Path $binaryDirectory | Out-Null
        foreach ($name in $requiredBinaries) {
            $source = if ($variantName -eq 'baseline' -and $name -eq 'FancyWM.ThemeEngine.dll') { $BaselineAssemblyPath } else { Join-Path $candidateOutput $name }
            Copy-Item -LiteralPath $source -Destination (Join-Path $binaryDirectory $name)
        }
        Get-ChildItem -LiteralPath $binaryDirectory -File | Get-FileHash |
            Select-Object Path, Hash | Export-Csv -NoTypeInformation "$experimentRoot/$variantName-binaries.csv"
    }
    Copy-Item -LiteralPath $PSCommandPath -Destination "$experimentRoot/benchmark-source.ps1"
    $priorTiering = $env:DOTNET_TieredCompilation
    $rawRows = [System.Collections.Generic.List[object]]::new()
    try {
        $env:DOTNET_TieredCompilation = '0'
        foreach ($pairNumber in 1..5) {
            $variants = if ($pairNumber % 2) { @('baseline', 'candidate') } else { @('candidate', 'baseline') }
            foreach ($variantName in $variants) {
                if ((Get-FileHash -LiteralPath $PSCommandPath).Hash -ne $benchmarkSha256) { throw 'Benchmark changed during comparison.' }
                $runSnapshot = if ($variantName -eq 'baseline') { $BaselineSnapshotId } else { $CandidateSnapshotId }
                $runId = "$variantName-$pairNumber"
                & pwsh -NoProfile -File $PSCommandPath -WarmupCount $WarmupCount -RunCount $RunCount -SnapshotId $runSnapshot -ExperimentId $ExperimentId -Variant $variantName -Pair $pairNumber -AssemblyPath "$experimentRoot/$variantName/FancyWM.ThemeEngine.dll" -OutputPath "$experimentRoot/$runId.csv" *> "$experimentRoot/$runId.log"
                if ($LASTEXITCODE) { throw "Theme conversion process failed: $experimentRoot/$runId.log" }
                $processRows = @(Import-Csv -LiteralPath "$experimentRoot/$runId.csv")
                if ($processRows.Count -ne 6 * $RunCount) { throw "Incomplete theme measurements: $runId" }
                foreach ($row in $processRows) { $rawRows.Add($row) }
            }
        }
    }
    finally { $env:DOTNET_TieredCompilation = $priorTiering }
    foreach ($field in @('dictionary_sha256', 'html_sha256', 'css_sha256', 'benchmark_sha256', 'runtime', 'process_architecture', 'warmup_count', 'run_count')) {
        if (@($rawRows | Select-Object -ExpandProperty $field -Unique).Count -ne 1) { throw "Variants disagree on $field; no measurements accepted." }
    }
    if ($rawRows[0].process_architecture -ne 'X64' -or $rawRows[0].benchmark_sha256 -ne $benchmarkSha256) {
        throw 'Comparison requires identical x64 benchmark inputs.'
    }
    $null = Get-VerifiedSnapshot $CandidateSnapshotId -Current
    $rows = @($rawRows | ForEach-Object {
        [pscustomobject][ordered]@{
            experiment_id = $_.experiment_id; snapshot_id = $_.snapshot_id
            scenario = $_.scenario; configuration = $_.configuration
            window_count = 0; metric = $_.metric
            unit = if ($_.metric -eq 'managed_allocated') { 'bytes/apply' } elseif ($_.metric -eq 'elapsed') { 'ms/apply' } else { 'count/apply' }
            run = $_.run; value = $_.value; iterations = 1; instrument = $_.instrument
            perf_id = 'PERF-015'; variant = $_.variant; pair = $_.pair
            binary_sha256 = $_.assembly_sha256; benchmark_sha256 = $_.benchmark_sha256
        }
    })
    $rows | Export-Csv -NoTypeInformation "$experimentRoot/measurements.csv"
    $spread = @(foreach ($variantName in @('baseline', 'candidate')) {
        foreach ($metric in @('elapsed', 'managed_allocated')) {
            $values = @($rows | Where-Object { $_.variant -eq $variantName -and $_.metric -eq $metric } |
                ForEach-Object { [double]::Parse($_.value, $invariantCulture) } | Sort-Object)
            $middle = [int][Math]::Floor($values.Count / 2)
            [pscustomobject]@{ Variant = $variantName; Metric = $metric; Samples = $values.Count; Min = $values[0]
                Median = $(if ($values.Count % 2) { $values[$middle] } else { ($values[$middle - 1] + $values[$middle]) / 2 }); Max = $values[-1] }
        }
    })
    [pscustomobject]@{ ExperimentId = $ExperimentId; BaselineSnapshot = $BaselineSnapshotId; CandidateSnapshot = $CandidateSnapshotId
        BaselineManifestSHA256 = $baselineManifestHash; CandidateManifestSHA256 = $candidateManifestHash
        BenchmarkSHA256 = $benchmarkSha256; DictionarySHA256 = $rawRows[0].dictionary_sha256
        HtmlSHA256 = $rawRows[0].html_sha256; CssSHA256 = $rawRows[0].css_sha256
        WarmupCount = $WarmupCount; RunCount = $RunCount; Pairs = 5; Rows = $rows.Count; Spread = $spread
        Limitation = 'Converter method only; complete raw dictionary equality; no native WPF rendering or process CPU/GPU measurement.'
    } | ConvertTo-Json -Depth 5 | Set-Content "$experimentRoot/summary.json"
    if ($Append) {
        $ledgerPath = Join-Path $repositoryRoot 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv'
        if (@(Import-Csv -LiteralPath $ledgerPath | Where-Object { $_.experiment_id -eq $ExperimentId }).Count) { throw 'Experiment ID already exists in optimization ledger.' }
        $newCsv = @($rows | ConvertTo-Csv -NoTypeInformation)
        if ((Get-Content -LiteralPath $ledgerPath -TotalCount 1) -ne $newCsv[0]) { throw 'Optimization ledger schema differs.' }
        $original = [IO.File]::ReadAllBytes($ledgerPath)
        if ($original.Length -eq 0 -or $original[-1] -ne 10) { throw 'Optimization ledger must end in a newline before appending.' }
        [IO.File]::AppendAllText($ledgerPath, (($newCsv | Select-Object -Skip 1) -join "`r`n") + "`r`n", [Text.UTF8Encoding]::new($false))
        $result = [IO.File]::ReadAllBytes($ledgerPath)
        $preservedPrefix = [byte[]]::new($original.Length)
        [Array]::Copy($result, $preservedPrefix, $original.Length)
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($preservedPrefix)) -ne
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($original))) { throw 'Optimization ledger original byte prefix changed.' }
    }
    Write-Output "PERF-015: five alternating Release pairs, full dictionary equality, $($rows.Count) rows: $experimentRoot/measurements.csv"
    return
}
if ($Append -or $BaselineAssemblyPath -or $BaselineBinaryManifestPath) { throw 'Comparison-only parameter used without both snapshot IDs.' }
if ([System.Diagnostics.Debugger]::IsAttached) { throw 'Theme measurements require no attached debugger.' }
$sourceManifestSha256 = if ($SnapshotId -eq 'UNSPECIFIED-CURRENT-SOURCE') { 'UNSPECIFIED' } else { Get-VerifiedSnapshot $SnapshotId }

if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
    $AssemblyPath = Join-Path $repositoryRoot 'FancyWM\bin\Release\net10.0-windows10.0.18362.0\FancyWM.ThemeEngine.dll'
}

if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) {
    throw "Release ThemeEngine assembly not found at '$AssemblyPath'. Build FancyWM in Release first, or pass -AssemblyPath."
}

$resolvedAssemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$assembly = [System.Reflection.Assembly]::LoadFrom($resolvedAssemblyPath)
$null = [System.Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $resolvedAssemblyPath) 'AngleSharp.dll'))
$null = [System.Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $resolvedAssemblyPath) 'AngleSharp.Css.dll'))
$debugAttribute = $assembly.GetCustomAttributes([System.Diagnostics.DebuggableAttribute], $false) | Select-Object -First 1
if ($debugAttribute -and $debugAttribute.IsJITOptimizerDisabled) { throw 'Theme measurements require an optimized Release assembly.' }

# This is ThemeEngineManager.HtmlTemplate at snapshot 4fb943b. Joining with LF
# makes the input independent of the checkout's line-ending configuration.
$htmlTemplate = @(
    '<panel>'
    '    <panel-bar>'
    '        <panel-bar-header>'
    '            <panel-bar-handle></panel-bar-handle>'
    '            <panel-bar-button></panel-bar-button>'
    '        </panel-bar-header>'
    '        <panel-bar-tab></panel-bar-tab>'
    '    </panel-bar>'
    '    <window>'
    '        <window-actions></window-actions>'
    '    </window>'
    '    <window class="preview"></window>'
    '</panel>'
    '<panel class="preview"></panel>'
) -join "`n"

# Fixed production-shaped default theme input used by UI-THEME-CONVERT-001.
# It exercises the selectors and values generated by ThemeEngineManager.
$cssText = @(
    'window:focus, window.preview, panel.preview {'
    '    border-color: #99AABB;'
    '    border-width: 2px;'
    '    border-radius: 8px;'
    '}'
    'window.preview, panel.preview {'
    '    background-color: rgba(153,170,187,0.1);'
    '}'
    'panel-bar {'
    '    border-color: rgba(0,0,0,0.1);'
    '    border-width: 0.5px;'
    '    border-radius: 4px;'
    '    background-color: #1F1F1F;'
    '    filter: drop-shadow(0px 2px 2px rgba(0,0,0,0.2));'
    '}'
    'panel-bar-tab {'
    '    color: #FFFFFF;'
    '}'
    'panel-bar-handle, panel-bar-button {'
    '    color: #000000;'
    '    background-color: #99AABB;'
    '    border-radius: 4px;'
    '}'
    'panel-bar-button:hover {'
    '    background-color: #AABBCC;'
    '}'
    'panel-bar-button:active {'
    '    background-color: #778899;'
    '}'
) -join "`n"

function Get-StringSha256 {
    param([Parameter(Mandatory)][string]$Value)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

$htmlSha256 = Get-StringSha256 $htmlTemplate
$cssSha256 = Get-StringSha256 $cssText
if ($htmlSha256 -ne $expectedHtmlSha256 -or $cssSha256 -ne $expectedCssSha256) {
    throw "Experiment input hash mismatch. HTML=$htmlSha256 CSS=$cssSha256"
}

$assemblySha256 = (Get-FileHash -LiteralPath $resolvedAssemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()
$relativeAssemblyPath = [System.IO.Path]::GetRelativePath($repositoryRoot, $resolvedAssemblyPath).Replace('\', '/')
$scenario = 'fixed production-shaped ThemeEngine CSS conversion; empty custom CSS'
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
$configuration = "Release; $architecture; no debugger/coverage/tracing; $WarmupCount warmups; $RunCount samples/process; DOTNET_TieredCompilation=$env:DOTNET_TieredCompilation; forced GC outside timed regions"
$rows = [System.Collections.Generic.List[object]]::new()
$dictionarySha256 = $null
$rawValueField = [FancyWM.ThemeEngine.Wpf.CssValue].GetField('m_value', [System.Reflection.BindingFlags]'Instance,NonPublic')
$cssTextProperty = [AngleSharp.Css.Dom.ICssValue].GetProperty('CssText')
if (!$rawValueField -or !$cssTextProperty) { throw 'Full raw CSS dictionary verification is unavailable.' }

function Get-DictionarySha256 {
    param([Parameter(Mandatory)]$Resources)

    # Compare every public key and unconverted CSS value. A null raw value differs
    # from an empty CssText; length prefixes avoid delimiter collisions. WPF lazy
    # conversion is deliberately not part of this converter-method experiment.
    [string[]]$keys = @($Resources.Keys)
    if ($keys.Length -ne $Resources.Count) { throw 'Public dictionary key enumeration is incomplete.' }
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    $canonical = [Text.StringBuilder]::new()
    foreach ($key in $keys) {
        $null = $canonical.Append($key.Length.ToString($invariantCulture)).Append(':').Append($key)
        $rawValue = $rawValueField.GetValue($Resources[$key])
        if ($null -eq $rawValue) { $null = $canonical.Append('-1:') }
        else {
            $cssValueText = [string]$cssTextProperty.GetValue($rawValue)
            $null = $canonical.Append($cssValueText.Length.ToString($invariantCulture)).Append(':').Append($cssValueText)
        }
    }
    return Get-StringSha256 $canonical.ToString()
}

function Add-Measurement {
    param(
        [Parameter(Mandatory)][int]$Run,
        [Parameter(Mandatory)][string]$Metric,
        [Parameter(Mandatory)][string]$Unit,
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Instrument
    )

    $rows.Add([pscustomobject][ordered]@{
        experiment_id        = $experimentId
        snapshot_id          = $snapshotId
        scenario             = $scenario
        configuration        = $configuration
        metric               = $Metric
        unit                 = $Unit
        run                  = $Run
        value                = $Value
        instrument           = $Instrument
        local_artifact       = $relativeAssemblyPath
        assembly_sha256      = $assemblySha256
        html_sha256          = $htmlSha256
        css_sha256           = $cssSha256
        runtime              = [Environment]::Version.ToString()
        process_architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        source_manifest_sha256 = $sourceManifestSha256
        benchmark_sha256      = $benchmarkSha256
        dictionary_sha256     = $dictionarySha256
        variant               = $Variant
        pair                  = $Pair
        warmup_count          = $WarmupCount
        run_count             = $RunCount
    })
}

for ($run = 0; $run -lt $WarmupCount; $run++) {
    $converter = [FancyWM.ThemeEngine.Wpf.CssToWpfResourceConverter]::new()
    $null = $converter.Convert($htmlTemplate, $cssText)
}

for ($run = 1; $run -le $RunCount; $run++) {
    # These collections isolate allocation samples; they are outside the timed
    # region and do not model a proposed production behavior.
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    [GC]::Collect()

    $gen0Before = [GC]::CollectionCount(0)
    $gen1Before = [GC]::CollectionCount(1)
    $gen2Before = [GC]::CollectionCount(2)
    $allocatedBefore = [GC]::GetTotalAllocatedBytes($true)

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $converter = [FancyWM.ThemeEngine.Wpf.CssToWpfResourceConverter]::new()
    $resources = $converter.Convert($htmlTemplate, $cssText)
    $stopwatch.Stop()

    $allocatedAfter = [GC]::GetTotalAllocatedBytes($true)
    $gen0After = [GC]::CollectionCount(0)
    $gen1After = [GC]::CollectionCount(1)
    $gen2After = [GC]::CollectionCount(2)

    # Observe the actual returned dictionary after both allocation and elapsed
    # counters have stopped. Every timed result must have identical semantics.
    $actualDictionarySha256 = Get-DictionarySha256 $resources
    if ($dictionarySha256 -and $dictionarySha256 -ne $actualDictionarySha256) { throw 'The complete resource dictionary changed between samples.' }
    $dictionarySha256 = $actualDictionarySha256

    Add-Measurement $run 'elapsed' 'ms' ($stopwatch.Elapsed.TotalMilliseconds.ToString('F3', $invariantCulture)) 'System.Diagnostics.Stopwatch'
    Add-Measurement $run 'managed_allocated' 'bytes' (($allocatedAfter - $allocatedBefore).ToString($invariantCulture)) 'GC.GetTotalAllocatedBytes(precise=true)'
    Add-Measurement $run 'resource_dictionary_entries' 'count' ($resources.Count.ToString($invariantCulture)) 'IReadOnlyDictionary.Count'
    Add-Measurement $run 'gen0_collections' 'count' (($gen0After - $gen0Before).ToString($invariantCulture)) 'GC.CollectionCount'
    Add-Measurement $run 'gen1_collections' 'count' (($gen1After - $gen1Before).ToString($invariantCulture)) 'GC.CollectionCount'
    Add-Measurement $run 'gen2_collections' 'count' (($gen2After - $gen2Before).ToString($invariantCulture)) 'GC.CollectionCount'
}

if ($OutputPath) {
    if (Test-Path -LiteralPath $OutputPath) { throw "Output already exists: $OutputPath" }
    $rows | Export-Csv -LiteralPath $OutputPath -NoTypeInformation
}
else { $rows | ConvertTo-Csv -NoTypeInformation }
