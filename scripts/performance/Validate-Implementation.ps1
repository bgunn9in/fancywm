param([Parameter(Mandatory)][string]$SnapshotId)
$ErrorActionPreference = 'Stop'
$repositoryRoot = (git rev-parse --show-toplevel).Trim()
$artifactRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId/validation"
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
& "$PSScriptRoot/Apply-DependencyPatches.ps1" -Mode Apply *> (Join-Path $artifactRoot 'dependency-patches.log')
$msbuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe'
foreach ($configuration in @('Debug', 'Release')) {
    & $msbuildPath FancyWM.sln /t:Restore /p:Platform=x64 /p:RuntimeIdentifier=win-x64 "/p:Configuration=$configuration" /v:minimal *> (Join-Path $artifactRoot "$configuration-restore.log")
    if ($LASTEXITCODE) { throw "Restore failed: $configuration" }
    dotnet build FancyWM.GUI/FancyWM.GUI.csproj --no-restore --configuration $configuration --property:WarningLevel=0 *> (Join-Path $artifactRoot "$configuration-gui.log")
    if ($LASTEXITCODE) { throw "GUI build failed: $configuration" }
    & $msbuildPath FancyWM.sln /p:Platform=x64 "/p:Configuration=$configuration" /p:GenerateTemporaryStoreCertificate=False /v:minimal *> (Join-Path $artifactRoot "$configuration-full.log")
    if ($LASTEXITCODE) { throw "Full build failed: $configuration" }
    foreach ($project in @('FancyWM.Tests', 'FancyWM.Layouts.Tests', 'FancyWM.ThemeEngine.Tests')) {
        dotnet test "$project/$project.csproj" --configuration $configuration --no-restore --logger "trx;LogFileName=$configuration-$project.trx" --results-directory $artifactRoot *> (Join-Path $artifactRoot "$configuration-$project.log")
        if ($LASTEXITCODE) { throw "Tests failed: $configuration $project" }
        Get-Content (Join-Path $artifactRoot "$configuration-$project.log") | Select-Object -Last 3
    }
    Write-Output "$configuration GUI, full x64 package and all test projects passed."
}
