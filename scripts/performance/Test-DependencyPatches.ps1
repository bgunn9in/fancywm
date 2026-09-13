param([Parameter(Mandatory)][string]$SnapshotId)
$ErrorActionPreference = 'Stop'
$repositoryRoot = (git rev-parse --show-toplevel).Trim()
$artifactRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId/dependency-check"
if (Test-Path -LiteralPath $artifactRoot) { throw "Verification already exists: $artifactRoot" }
New-Item -ItemType Directory -Path $artifactRoot | Out-Null
$sourceRoot = Join-Path $artifactRoot 'source'
foreach ($dependency in @('winman', 'winman-windows')) {
    $commit = ((git ls-tree HEAD -- $dependency) -split '\s+')[2]
    git -C $dependency archive --format=zip "--output=$artifactRoot/$dependency.zip" $commit
    if ($LASTEXITCODE) { throw "Cannot archive pinned $dependency" }
    Expand-Archive -LiteralPath "$artifactRoot/$dependency.zip" -DestinationPath "$sourceRoot/$dependency"
}
'<Project />' | Set-Content -LiteralPath "$sourceRoot/Directory.Build.props"
$dependencyRoot = Join-Path $sourceRoot 'winman-windows'
$gitDirectory = (git -C winman-windows rev-parse --absolute-git-dir).Trim()
foreach ($mode in @('Check', 'Apply', 'Apply', 'Check')) {
    & "$PSScriptRoot/Apply-DependencyPatches.ps1" -Mode $mode -DependencyRoot $dependencyRoot -GitDirectory $gitDirectory
}
dotnet build "$dependencyRoot/src/WinMan.Windows/WinMan.Windows.csproj" --configuration Release *> "$artifactRoot/build.log"
if ($LASTEXITCODE) { throw "Clean pinned dependency build failed: $artifactRoot/build.log" }
Get-FileHash -LiteralPath "$dependencyRoot/src/WinMan.Windows/bin/Release/net10.0-windows7.0/WinMan.Windows.dll" |
    Select-Object Path, Hash | Export-Csv -NoTypeInformation "$artifactRoot/binary.csv"
$manifest = Get-Content "$repositoryRoot/patches/winman-windows/manifest.json" -Raw | ConvertFrom-Json
$conflictRoot = Join-Path $artifactRoot 'conflict-fixture'
foreach ($record in $manifest.files) {
    $destination = Join-Path $conflictRoot $record.path
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $dependencyRoot $record.path) -Destination $destination
}
$conflictFile = Join-Path $conflictRoot $manifest.files[0].path
Add-Content -LiteralPath $conflictFile -Value 'local-change-sentinel'
$conflictHash = (Get-FileHash -LiteralPath $conflictFile).Hash
pwsh -NoProfile -File "$PSScriptRoot/Apply-DependencyPatches.ps1" -Mode Apply -DependencyRoot $conflictRoot -GitDirectory $gitDirectory *> "$artifactRoot/conflict.log"
if ($LASTEXITCODE -eq 0 -or (Get-FileHash -LiteralPath $conflictFile).Hash -ne $conflictHash) { throw 'Conflict was not rejected without mutation' }
Write-Output 'Unknown local hunk rejected and preserved in isolated conflict fixture.'
Write-Output "Clean pinned archive applies once, repeated apply/check succeeds, Release dependency builds: $artifactRoot"
