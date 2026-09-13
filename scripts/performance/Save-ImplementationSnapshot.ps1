param([Parameter(Mandatory)][string]$SnapshotId)
$ErrorActionPreference = 'Stop'
$repositoryRoot = (git rev-parse --show-toplevel).Trim()
$artifactRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
if (Test-Path -LiteralPath $artifactRoot) { throw "Snapshot already exists: $artifactRoot" }
New-Item -ItemType Directory -Path $artifactRoot | Out-Null
$sourceRoot = Join-Path $artifactRoot 'source'
$trackedPaths = @(git ls-files --recurse-submodules)
$extraPaths = @(git ls-files --others --exclude-standard -- PERFORMANCE_TODO.md PERFORMANCE_STATUS.md BEGIN_PROMT.md CONTINUE_PROMT.md docs/performance scripts/performance patches FancyWM FancyWM.GUI FancyWM.Layouts FancyWM.Tests FancyWM.Layouts.Tests FancyWM.ThemeEngine FancyWM.ThemeEngine.Tests FancyWM.DllImports)
$manifest = foreach ($relativePath in (($trackedPaths + $extraPaths) | Sort-Object -Unique)) {
    if ($relativePath -match '(^|/)(\.codex|\.git|bin|obj|artifacts)/|\.(pfx|pem|key|dmp|user)$|(^|/)(credentials|auth|secrets)\.') { continue }
    $sourcePath = Join-Path $repositoryRoot $relativePath
    if (!(Test-Path -LiteralPath $sourcePath -PathType Leaf)) { continue }
    $destination = Join-Path $sourceRoot $relativePath
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $destination
    [pscustomobject]@{ Path = $relativePath; SHA256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash }
}
$manifest | Export-Csv -NoTypeInformation -Path (Join-Path $artifactRoot 'manifest.csv')
[pscustomobject]@{
    SnapshotId = $SnapshotId
    Root = $repositoryRoot
    Commit = (git rev-parse HEAD)
    Tree = (git rev-parse 'HEAD^{tree}')
    Status = @(git status --short)
    Submodules = @(git submodule status --recursive)
    ManifestSHA256 = (Get-FileHash (Join-Path $artifactRoot 'manifest.csv')).Hash
    Exclusions = '.codex, credentials, private keys, dumps, ignored files; never copied or read'
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $artifactRoot 'snapshot.json')
git diff --binary | Set-Content (Join-Path $artifactRoot 'tracked.diff')
git diff --cached --binary | Set-Content (Join-Path $artifactRoot 'index.diff')
Get-Content (Join-Path $artifactRoot 'snapshot.json')
