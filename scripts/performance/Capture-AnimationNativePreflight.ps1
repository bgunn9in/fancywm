param([Parameter(Mandatory)][string]$SnapshotId)

$ErrorActionPreference = 'Stop'

function Invoke-CapturedProcess([string]$FileName, [string[]]$Arguments) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (!$process.Start()) { throw "Failed to start $FileName" }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return [pscustomobject]@{
        FileName = $FileName
        Arguments = @($Arguments)
        ExitCode = $process.ExitCode
        ExitCodeHex = '0x{0:X8}' -f [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$process.ExitCode), 0)
        StdOut = $stdout.TrimEnd()
        StdErr = $stderr.TrimEnd()
    }
}

$repositoryRoot = (git rev-parse --show-toplevel).Trim()
$artifactRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
$preflightRoot = Join-Path $artifactRoot 'preflight'
New-Item -ItemType Directory -Force -Path $preflightRoot | Out-Null
$summaryPath = Join-Path $preflightRoot 'native-preflight-summary.json'
if (Test-Path -LiteralPath $summaryPath) { throw "Preflight already exists: $summaryPath" }

$wpr = Get-Command wpr.exe -ErrorAction Stop
$toolNames = @('wpr.exe', 'wpa.exe', 'wpaexporter.exe', 'xperf.exe', 'tracerpt.exe')
$tools = foreach ($toolName in $toolNames) {
    $tool = Get-Command $toolName -ErrorAction SilentlyContinue
    [ordered]@{
        Name = $toolName
        Path = if ($tool) { $tool.Source } else { $null }
        FileVersion = if ($tool) { $tool.FileVersionInfo.FileVersion } else { $null }
    }
}

$statusBefore = Invoke-CapturedProcess $wpr.Source @('-status')
if ($statusBefore.ExitCode -ne 0 -or $statusBefore.StdOut -notmatch 'not recording') {
    throw 'A WPR recording may already be active; the preflight did not modify it.'
}
$profiles = Invoke-CapturedProcess $wpr.Source @('-profiles')
$groups = Invoke-CapturedProcess (Get-Command whoami.exe -ErrorAction Stop).Source @('/groups')
$privileges = Invoke-CapturedProcess (Get-Command whoami.exe -ErrorAction Stop).Source @('/priv')

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$os = Get-CimInstance Win32_OperatingSystem
$computer = Get-CimInstance Win32_ComputerSystem
$video = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion,
    CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FancyWMDpiProbe
{
    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);
}
'@
$dpi = [FancyWMDpiProbe]::GetDpiForSystem()
$display = [ordered]@{
    SystemDpi = $dpi
    ScalePercent = [Math]::Round(100.0 * $dpi / 96.0, 3)
    VirtualLeft = [FancyWMDpiProbe]::GetSystemMetrics(76)
    VirtualTop = [FancyWMDpiProbe]::GetSystemMetrics(77)
    VirtualWidth = [FancyWMDpiProbe]::GetSystemMetrics(78)
    VirtualHeight = [FancyWMDpiProbe]::GetSystemMetrics(79)
    MonitorCount = [FancyWMDpiProbe]::GetSystemMetrics(80)
}

$profileRoots = @(
    'C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit',
    'C:\Program Files\Windows Kits\10\Windows Performance Toolkit'
)
$localProfiles = @(foreach ($profileRoot in $profileRoots) {
    if (Test-Path -LiteralPath $profileRoot) {
        Get-ChildItem -LiteralPath $profileRoot -Recurse -File -Include *.wprp -ErrorAction Stop |
            Sort-Object FullName | ForEach-Object {
                [ordered]@{
                    Path = $_.FullName
                    Length = $_.Length
                    SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
                }
            }
    }
})

$attempt = Invoke-CapturedProcess $wpr.Source @('-start', 'CPU', '-filemode')
$etlPath = Join-Path $preflightRoot 'cpu-smoke.etl'
$stop = $null
if ($attempt.ExitCode -eq 0) {
    try {
        $stop = Invoke-CapturedProcess $wpr.Source @('-stop', $etlPath)
    }
    finally {
        $statusAfterStop = Invoke-CapturedProcess $wpr.Source @('-status')
        if ($statusAfterStop.StdOut -notmatch 'not recording') {
            throw 'The preflight could not confirm that its WPR recording stopped.'
        }
    }
}
$statusAfter = Invoke-CapturedProcess $wpr.Source @('-status')

$statusBefore | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $preflightRoot 'wpr-status-before.json')
$profiles | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $preflightRoot 'wpr-profiles.json')
$groups | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $preflightRoot 'whoami-groups.json')
$privileges | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $preflightRoot 'whoami-privileges.json')
$attempt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $preflightRoot 'wpr-start-cpu.json')
$statusAfter | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $preflightRoot 'wpr-status-after.json')
if ($stop) { $stop | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $preflightRoot 'wpr-stop-cpu.json') }

$verdict = if ($attempt.ExitCode -eq 0 -and $stop.ExitCode -eq 0 -and (Test-Path -LiteralPath $etlPath)) {
    'AVAILABLE'
} else {
    'NATIVE_BLOCKED'
}
$blocker = if ($verdict -eq 'NATIVE_BLOCKED') {
    "wpr.exe -start CPU -filemode failed with $($attempt.ExitCodeHex): $($attempt.StdErr -replace '[\r\n]+', ' ')"
} else {
    $null
}

$evidence = @(Get-ChildItem -LiteralPath $preflightRoot -File | Sort-Object Name | ForEach-Object {
    [ordered]@{
        Path = $_.Name
        Length = $_.Length
        SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
    }
})
$summary = [ordered]@{
    SnapshotId = $SnapshotId
    Verdict = $verdict
    Blocker = $blocker
    RequiredNativeEvidence = @('kernel CPU sampling and context switches', 'GPU and DesktopComposition',
        'FancyWM process CPU', 'physical presentation latency')
    Repository = $repositoryRoot
    Commit = (git rev-parse HEAD).Trim()
    User = $identity.Name
    Elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Interactive = [Environment]::UserInteractive
    SessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    OS = [ordered]@{ Caption = $os.Caption; Version = $os.Version; Build = $os.BuildNumber; Architecture = $os.OSArchitecture }
    Computer = [ordered]@{ Manufacturer = $computer.Manufacturer; Model = $computer.Model; LogicalProcessors = $computer.NumberOfLogicalProcessors }
    Display = $display
    Video = $video
    Tools = $tools
    LocalProfiles = $localProfiles
    WprAttempt = [ordered]@{ Arguments = $attempt.Arguments; ExitCode = $attempt.ExitCode; ExitCodeHex = $attempt.ExitCodeHex }
    RecordingActiveBefore = $false
    RecordingActiveAfter = $false
    RawEtlCreated = Test-Path -LiteralPath $etlPath
    Evidence = $evidence
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath
$summary | ConvertTo-Json -Depth 8
