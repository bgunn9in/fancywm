param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../artifacts/portable')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

Push-Location -LiteralPath $repositoryRoot
try {
    & "$PSScriptRoot/performance/Apply-DependencyPatches.ps1" -Mode Apply
    $sourceCommit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the source commit' }
    $trackedChanges = @(git status --porcelain --untracked-files=no --ignore-submodules=all)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read tracked source status' }
    $sourceState = if ($trackedChanges.Count) { 'with tracked working-tree changes' } else { 'clean tracked sources' }

    $packageName = 'FancyWM-Portable-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
    $packageRoot = Join-Path $OutputRoot $packageName
    $archivePath = $packageRoot + '.zip'
    if ((Test-Path -LiteralPath $packageRoot) -or (Test-Path -LiteralPath $archivePath)) {
        throw 'Output already exists; previous packages will not be overwritten'
    }
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $packageRoot | Out-Null

    # Publishing both entry projects retains the CLI deps/runtimeconfig as well
    # as the GUI entry point. Restore is included, so clean checkouts also work.
    foreach ($project in @('FancyWM.GUI/FancyWM.GUI.csproj', 'FancyWM/FancyWM.csproj')) {
        & dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
            --property:Platform=AnyCPU --property:PublishSingleFile=false --property:PublishTrimmed=false `
            --output $packageRoot
        if ($LASTEXITCODE -ne 0) { throw "Portable build failed: $project" }
    }

    foreach ($file in @('FancyWM-GUI.exe', 'FancyWM-GUI.dll', 'FancyWM.exe', 'FancyWM.dll',
            'FancyWM-GUI.deps.json', 'FancyWM-GUI.runtimeconfig.json', 'FancyWM.deps.json',
            'FancyWM.runtimeconfig.json', 'FancyWM.bat', 'coreclr.dll', 'hostfxr.dll',
            'hostpolicy.dll', 'PresentationFramework.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $packageRoot $file) -PathType Leaf)) {
            throw "Missing portable file: $file"
        }
    }
    $sourceBatHash = (Get-FileHash -LiteralPath 'FancyWM/FancyWM.bat' -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath (Join-Path $packageRoot 'FancyWM.bat') -Algorithm SHA256).Hash -ne $sourceBatHash) {
        throw 'The project did not publish the source BAT unchanged'
    }

    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $packageRoot 'FancyWM.dll')).FileVersion
    $readme = @'
# FancyWM portable {0}

Release win-x64, self-contained .NET 10 Desktop Runtime.
Source commit: {1} ({2}). Existing dependency patches are applied and checked
using patches/winman-windows/manifest.json.

1. Extract the entire ZIP into a separate folder.
2. Exit any running FancyWM copy from its tray icon.
3. Run FancyWM-GUI.exe. No separate .NET installation is required.

Settings and logs use the existing %APPDATA%\FancyWM directory.
For CLI help/version without starting the UI, run FancyWM.exe --help or
FancyWM.exe --version. FancyWM.bat forwards arguments to the adjacent FancyWM.exe
and returns its exit code, including from a different working directory.

Rebuild from the repository: pwsh -File scripts/Build-Portable.ps1
The script creates a new directory and ZIP on each run; previous packages remain.
'@ -f $version, $sourceCommit, $sourceState
    Set-Content -LiteralPath (Join-Path $packageRoot 'README.md') -Value $readme -Encoding UTF8

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $archivePath)
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    Set-Content -LiteralPath ($archivePath + '.sha256') -Value "$hash  $packageName.zip" -Encoding ASCII
    [PSCustomObject]@{
        Archive = $archivePath
        SHA256 = $hash
        Version = $version
        SourceCommit = $sourceCommit
        SourceState = $sourceState
    }
}
finally {
    Pop-Location
}
