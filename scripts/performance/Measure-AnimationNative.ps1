param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Z0-9-]+$')][string]$SnapshotId,
    [ValidateRange(1, 10)][int]$Runs = 5,
    [ValidateRange(1, 20)][int]$Iterations = 8,
    [ValidateSet(1, 5, 50)][int]$OwnerLimit = 1
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$snapshotRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"))
if (Test-Path -LiteralPath $snapshotRoot) { throw "Snapshot already exists: $SnapshotId" }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or ![Environment]::UserInteractive) {
    throw 'The native capture requires an elevated interactive tracing token.'
}
$wpr = (Get-Item -LiteralPath (Join-Path $env:SystemRoot 'System32/wpr.exe') -ErrorAction Stop).FullName
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$xperf = (Get-Command xperf.exe -ErrorAction Stop).Source
$python = (Get-Command python.exe -ErrorAction Stop).Source
$statusBefore = & $wpr -status
if ($LASTEXITCODE -ne 0 -or ($statusBefore -join "`n") -notmatch 'not recording') {
    throw 'A WPR recording may already be active. No recording was modified.'
}
& "$PSScriptRoot/Save-ImplementationSnapshot.ps1" -SnapshotId $SnapshotId | Out-Null
$measurementRoot = Join-Path $snapshotRoot 'native'
New-Item -ItemType Directory -Path $measurementRoot | Out-Null
$statusBefore | Set-Content -LiteralPath (Join-Path $measurementRoot 'wpr-before.log')
$commands = [Collections.Generic.List[object]]::new()

function Invoke-Logged([string]$ProgramPath, [string[]]$Arguments, [string]$Name, [int]$TimeoutSeconds = 180) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $ProgramPath
    $start.WorkingDirectory = $repositoryRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $startedUtc = [DateTimeOffset]::UtcNow
    if (!$process.Start()) { throw "Failed to start $Name" }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $timedOut = !$process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        $process.Kill($true) # Only this owned subprocess and its descendants.
        $process.WaitForExit()
    }
    $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $measurementRoot "$Name.log")
    $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $measurementRoot "$Name.err.log")
    $receipt = [pscustomobject]@{
        Name = $Name; FileName = $ProgramPath; Arguments = @($Arguments)
        ProcessId = $process.Id; ExitCode = $process.ExitCode; TimedOut = $timedOut
        StartedUtc = $startedUtc.ToString('o'); FinishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    $commands.Add($receipt)
    $commands | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $measurementRoot 'commands.json')
    $process.Dispose()
    if ($timedOut -or $receipt.ExitCode -ne 0) { throw "$Name failed; inspect the retained command logs." }
    Write-Output "$Name completed."
}

Invoke-Logged $dotnet @('build', (Join-Path $snapshotRoot 'source/scripts/performance/native/FancyWM.AnimationNativeHarness.csproj'),
    '-c', 'Release', '-p:PlatformTarget=x64', '-p:AllowUnsafeBlocks=true', '--nologo', '-v:minimal') 'build-release' 600
$builtRoot = Join-Path $snapshotRoot 'source/scripts/performance/native/bin/Release/net10.0-windows10.0.18362.0'
$binaryRoot = Join-Path $snapshotRoot 'binaries'
Copy-Item -LiteralPath $builtRoot -Destination $binaryRoot -Recurse
$binaryManifest = @(Get-ChildItem -LiteralPath $binaryRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($binaryRoot, $_.FullName)
        Length = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$binaryManifest | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $measurementRoot 'binary-manifest.csv')
$profile = Join-Path $measurementRoot 'AnimationNative.wprp'
Copy-Item -LiteralPath "$PSScriptRoot/native/AnimationNative.wprp" -Destination $profile
Invoke-Logged $wpr @('-exportprofile', 'CPU+GPU+DesktopComposition',
    (Join-Path $measurementRoot 'current-builtin-profiles.wprp'), '-filemode') 'export-profiles'
# Built-in exports can change their StackCaching setting even for the same
# System32 executable (M4 versus M5). Replay the immutable N3 export as an
# explicit custom profile, retaining the current export for diagnosis.
$baselineProfile = Join-Path $repositoryRoot 'artifacts/performance/FWM-ANIMATION-NATIVE-BASELINE-20260910-N3/native/builtin-profiles.wprp'
$captureProfile = Join-Path $measurementRoot 'builtin-profiles.wprp'
Copy-Item -LiteralPath $baselineProfile -Destination $captureProfile
[xml]$profileXml = Get-Content -LiteralPath $captureProfile -Raw
$captureProfileName = $profileXml.WindowsPerformanceRecorder.Profiles.Profile.Name
$captureProfileSpec = "${captureProfile}!${captureProfileName}"
Invoke-Logged $wpr @('-profiledetails', $captureProfileSpec, '-filemode') 'profile-details'
$profileCheck = 'import importlib.util,pathlib,sys; spec=importlib.util.spec_from_file_location("verifier",sys.argv[1]); module=importlib.util.module_from_spec(spec); spec.loader.exec_module(module); assert module.profile_semantics(pathlib.Path(sys.argv[2]))==module.profile_semantics(pathlib.Path(sys.argv[3])),"Built-in profile settings differ from N3 before tracing"; print("N3 profile controls match")'
Invoke-Logged $python @('-B', '-c', $profileCheck,
    (Join-Path $snapshotRoot 'source/scripts/performance/Verify-AnimationTopology.py'),
    (Join-Path $measurementRoot 'builtin-profiles.wprp'),
    (Join-Path $repositoryRoot 'artifacts/performance/FWM-ANIMATION-NATIVE-BASELINE-20260910-N3/native/builtin-profiles.wprp')) 'verify-profile-controls'
[ordered]@{
    SnapshotId = $SnapshotId; Runs = $Runs; Iterations = $Iterations; OwnerLimit = $OwnerLimit
    ProfileControl = 'Exact archived N3 CPU+GPU+DesktopComposition XML replayed as an explicit custom profile'
    Elevated = $true; Interactive = [Environment]::UserInteractive
    SessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    OS = [Environment]::OSVersion.VersionString; LogicalProcessors = [Environment]::ProcessorCount
    Video = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion,
        CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate)
    ProcessesBefore = @(Get-CimInstance Win32_Process | Sort-Object ProcessId | Select-Object ProcessId,
        ParentProcessId, Name, CreationDate, KernelModeTime, UserModeTime, ThreadCount, HandleCount)
    VisibilityControl = 'HWND_TOPMOST with NOOWNERZORDER before the 100 ms settling delay; native style/cloak/five-point hit-test checks outside timing boundaries'
    Tools = @($wpr, $dotnet, $xperf | ForEach-Object {
        [ordered]@{ Path = $_; Version = (Get-Item -LiteralPath $_).VersionInfo.FileVersion
            SHA256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
    })
    LedgerBeforeSHA256 = (Get-FileHash -LiteralPath (Join-Path $repositoryRoot 'docs/performance/OPTIMIZATION_MEASUREMENTS.csv')).Hash
    FullApplication = $false; MsixInstalled = $false; MsixLaunched = $false; Published = $false
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $measurementRoot 'environment.json')

$tracePath = Join-Path $measurementRoot 'animation.etl'
$ownedRecording = $false
try {
    Invoke-Logged $wpr @('-start', $captureProfileSpec,
        '-start', "${profile}!AnimationNative", '-filemode', '-instancename', $SnapshotId) 'wpr-start'
    $ownedRecording = $true
    for ($run = 1; $run -le $Runs; $run++) {
        Invoke-Logged $dotnet @((Join-Path $binaryRoot 'FancyWM.AnimationNativeHarness.dll'),
            (Join-Path $measurementRoot "run-$run"), $Iterations.ToString(), $OwnerLimit.ToString()) "run-$run" 120
    }
}
finally {
    if ($ownedRecording) {
        try {
            Invoke-Logged $wpr @('-stop', $tracePath, '-instancename', $SnapshotId) 'wpr-stop' 900
        }
        finally {
            $statusAfter = & $wpr -status -instancename $SnapshotId
            $statusAfter | Set-Content -LiteralPath (Join-Path $measurementRoot 'wpr-after.log')
            if ($LASTEXITCODE -ne 0 -or ($statusAfter -join "`n") -notmatch 'not recording') {
                # Preserve a measurement recording after a failed stop. A fresh
                # stop attempt must retain its ETL; cancel would discard evidence.
                throw "The owned trace did not stop cleanly. Preserve instance $SnapshotId and stop it to a fresh ETL path; no cancellation was attempted."
            }
        }
    }
}
Invoke-Logged $xperf @('-i', $tracePath, '-o', (Join-Path $measurementRoot 'trace-header.txt'), '-a', 'tracestats') 'trace-header'
Invoke-Logged $xperf @('-i', $tracePath, '-o', (Join-Path $measurementRoot 'trace-stats.txt'), '-a', 'tracestats', '-detail') 'trace-stats'
Invoke-Logged $xperf @('-i', $tracePath, '-o', (Join-Path $measurementRoot 'cpu-by-thread.csv'), '-a', 'cswitch', '-process', '-thread') 'cpu-by-thread'
Invoke-Logged $xperf @('-i', $tracePath, '-o', (Join-Path $measurementRoot 'sampled-profile.csv'), '-a', 'profile', '-detail') 'sampled-profile'
Invoke-Logged $xperf @('-i', $tracePath, '-o', (Join-Path $measurementRoot 'markers.csv'), '-a', 'dumper',
    '-provider', '{c72441c8-a777-4d33-8d80-794f778a01ca}', '-add_fieldnames') 'markers'

$evidence = @(Get-ChildItem -LiteralPath $measurementRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($measurementRoot, $_.FullName)
        Length = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$evidence | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $snapshotRoot 'native-evidence.csv')
Write-Output "Native capture complete: $SnapshotId. Verify the complete topology/presentation protocol before accepting this stage."
