param(
    [Parameter(Mandatory)][string]$BaselineId,
    [Parameter(Mandatory)][string]$CandidateId,
    [Parameter(Mandatory)][string]$ExperimentId,
    [switch]$BaselineReusesPadding
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function HashFile([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$artifactRoot = Join-Path $repositoryRoot 'artifacts/performance'
$experimentRoot = Join-Path $artifactRoot $ExperimentId
Require (!(Test-Path -LiteralPath $experimentRoot)) "Experiment already exists: $ExperimentId"
New-Item -ItemType Directory -Path $experimentRoot | Out-Null
$roots = @{ A = Join-Path $artifactRoot $BaselineId; B = Join-Path $artifactRoot $CandidateId }
function VerifyInputs {
    foreach ($variant in @('A','B')) {
        $root = $roots[$variant]
        foreach ($kind in @('source','binaries')) {
            $manifest = if ($kind -eq 'source') { 'manifest.csv' } else { 'binaries.csv' }
            foreach ($entry in Import-Csv (Join-Path $root $manifest)) {
                Require ((HashFile (Join-Path $root "$kind/$($entry.Path)")) -ceq $entry.SHA256) "Changed $variant $kind/$($entry.Path)"
            }
        }
    }
}
VerifyInputs
$sourceA = @{}; Import-Csv (Join-Path $roots.A 'manifest.csv') | ForEach-Object { $sourceA[$_.Path] = $_.SHA256 }
$sourceB = @{}; Import-Csv (Join-Path $roots.B 'manifest.csv') | ForEach-Object { $sourceB[$_.Path] = $_.SHA256 }
$sourceChanges = @(@($sourceA.Keys) + @($sourceB.Keys) | Sort-Object -Unique | Where-Object {
    $_ -match '^(FancyWM[^/]*|winman|winman-windows|ModernWpf)/' -and $sourceA[$_] -cne $sourceB[$_]
})
$expectedChanges = if ($BaselineReusesPadding) { @('FancyWM/TilingOverlayRenderer.cs') }
    else { @('FancyWM/TilingOverlayRenderer.cs','FancyWM/TilingService.Private.cs','FancyWM/TilingService.cs') }
Require (@(Compare-Object $sourceChanges $expectedChanges).Count -eq 0) 'A/B source delta differs from the declared production files.'
$dotnet = (Get-Command dotnet -CommandType Application).Source
& $dotnet --info *> (Join-Path $experimentRoot 'dotnet-info.log')
Require ($LASTEXITCODE -eq 0) 'dotnet --info failed.'
$runs = [Collections.Generic.List[object]]::new()
$rows = [Collections.Generic.List[object]]::new()
$filter = 'FullyQualifiedName=FancyWM.Tests.AlgorithmicLayouts.TilingServiceAlgorithmicIntegrationTest.OverlayPaddingMaterializationCounterScenario'
foreach ($pair in 1..5) {
    $order = if ($pair % 2 -eq 1) { @('A','B') } else { @('B','A') }
    foreach ($variant in $order) {
        $runId = "pair-$pair-$variant"
        $runRoot = Join-Path $experimentRoot $runId
        New-Item -ItemType Directory -Path $runRoot | Out-Null
        $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.WorkingDirectory = Join-Path $roots[$variant] 'binaries'
        foreach ($argument in @('vstest',(Join-Path $start.WorkingDirectory 'FancyWM.Tests.dll'),'/Platform:x64',"/TestCaseFilter:$filter",'/Logger:trx;LogFileName=result.trx',"/ResultsDirectory:$runRoot")) { $start.ArgumentList.Add($argument) }
        $start.Environment['FANCYWM_OVERLAY_PADDING_CHILD'] = '1'
        $start.Environment['DOTNET_TieredCompilation'] = '0'
        $startUtc = [DateTimeOffset]::UtcNow.ToString('o')
        $process = [Diagnostics.Process]::Start($start)
        $ownedPid = $process.Id
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(60000)) { $process.Kill($true); $process.WaitForExit(); throw "$runId timeout" }
        $endUtc = [DateTimeOffset]::UtcNow.ToString('o')
        $stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $runRoot 'stdout.log')
        $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $runRoot 'stderr.log')
        $exitCode = $process.ExitCode
        $process.Dispose()
        Require ($exitCode -eq 0) "$runId test process failed."
        $trxPath = Join-Path $runRoot 'result.trx'
        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
        $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
        Require ($results.Count -eq 1 -and $results[0].outcome -ceq 'Passed') "$runId TRX failed."
        $seen = @{}
        foreach ($output in $trx.SelectNodes("//*[local-name()='StdOut']")) {
            foreach ($line in $output.InnerText -split "`n") {
                if ($line.Trim() -match '^PERFCOUNTER overlay-padding-(1|10|50) ([a-z-]+) ([0-9]+)$') {
                    $count = [int]$Matches[1]; $metric = $Matches[2]; $value = [long]$Matches[3]
                    $key = "$count/$metric"
                    Require (!$seen.ContainsKey($key)) "$runId duplicate counter $key"
                    $seen[$key] = $value
                    $rows.Add([pscustomobject]@{ Pair = $pair; Variant = $variant; Count = $count; Metric = $metric; Value = $value })
                }
            }
        }
        Require ($seen.Count -eq 36) "$runId expected 36 counters."
        foreach ($count in @(1,10,50)) {
            $creates = $variant -eq 'A' -and !$BaselineReusesPadding
            $expect = @{
                iterations = 12; warmups = 6; 'settled-models' = $count + 1; 'settled-windows' = $count
                'models-created' = $(if ($creates) { 12 * ($count + 1) } else { 0 })
                'windows-created' = $(if ($creates) { 12 * $count } else { 0 })
                'tabs-created' = $(if ($creates) { 12 * $count } else { 0 })
                'svg-created' = $(if ($creates) { 12 * (5 * $count + 1) } else { 0 })
                'subscription-adds' = $(if ($creates) { 72 * $count } else { 0 })
            }
            foreach ($metric in $expect.Keys) { Require ($seen["$count/$metric"] -eq $expect[$metric]) "$runId $count/$metric unexpected." }
            foreach ($metric in @('allocated-bytes','elapsed-ticks','timestamp-frequency')) { Require ($seen["$count/$metric"] -gt 0) "$runId nonpositive $metric" }
        }
        $runs.Add([pscustomobject]@{ Pair = $pair; Variant = $variant; LauncherPid = $ownedPid; StartUtc = $startUtc; EndUtc = $endUtc; ExitCode = $exitCode; TrxSHA256 = HashFile $trxPath })
        $runs | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $experimentRoot 'runs.json')
        $rows | Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'counters.csv')
        Write-Output "$runId PASS"
    }
}
$comparisons = foreach ($pair in 1..5) {
    foreach ($count in @(1,10,50)) {
        $values = @{}
        $rows | Where-Object { $_.Pair -eq $pair -and $_.Count -eq $count } | ForEach-Object { $values["$($_.Variant)/$($_.Metric)"] = $_.Value }
        [pscustomobject]@{ Pair = $pair; Count = $count; BaselineBytes = $values['A/allocated-bytes']; CandidateBytes = $values['B/allocated-bytes']; BaselineTicks = $values['A/elapsed-ticks']; CandidateTicks = $values['B/elapsed-ticks'] }
    }
}
$comparisons | Export-Csv -NoTypeInformation (Join-Path $experimentRoot 'comparisons.csv')
VerifyInputs
[ordered]@{
    Verdict = 'MANAGED_COUNTERS_VERIFIED'
    BaselineId = $BaselineId; CandidateId = $CandidateId; ExperimentId = $ExperimentId
    BaselineReusesPadding = [bool]$BaselineReusesPadding
    Runs = $runs.Count; Rows = $rows.Count; Comparisons = $comparisons.Count
    AllocationWins = @($comparisons | Where-Object { $_.CandidateBytes -lt $_.BaselineBytes }).Count
    ElapsedWins = @($comparisons | Where-Object { $_.CandidateTicks -lt $_.BaselineTicks }).Count
    ProductionDelta = $sourceChanges
    CounterSHA256 = HashFile (Join-Path $experimentRoot 'counters.csv')
    RunsSHA256 = HashFile (Join-Path $experimentRoot 'runs.json')
    ScriptSHA256 = HashFile $PSCommandPath
    NativeCpu = 'NOT_MEASURED'; NativeGpu = 'NOT_MEASURED'; NativePresentation = 'E2E_PENDING'
    LedgerAppended = $false
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $experimentRoot 'verification.json')
Get-Content (Join-Path $experimentRoot 'verification.json')
