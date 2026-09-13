param(
    [ValidateSet('Check', 'Apply')][string]$Mode = 'Check',
    [string]$DependencyRoot,
    [string]$GitDirectory
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = (git rev-parse --show-toplevel).Trim()
$manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'patches/winman-windows/manifest.json') -Raw | ConvertFrom-Json
if (!$DependencyRoot) { $DependencyRoot = Join-Path $repositoryRoot $manifest.submodule }
$DependencyRoot = [IO.Path]::GetFullPath($DependencyRoot)
if (!$GitDirectory) { $GitDirectory = (git -C $DependencyRoot rev-parse --absolute-git-dir).Trim() }
$gitArguments = @('-C', $DependencyRoot, '--git-dir', $GitDirectory, '--work-tree', $DependencyRoot)
$headCommit = (git @gitArguments rev-parse HEAD).Trim()
if ($LASTEXITCODE -or $headCommit -ne $manifest.pinned_commit) { throw 'Dependency HEAD differs from pinned patch commit' }
$gitlink = git -C $repositoryRoot ls-tree HEAD -- $manifest.submodule
if ($LASTEXITCODE -or $gitlink -notmatch $manifest.pinned_commit) { throw 'Root gitlink differs from pinned patch commit' }
$patchPath = Join-Path $repositoryRoot $manifest.patch
if ((Get-FileHash -LiteralPath $patchPath).Hash -ne $manifest.patch_sha256) { throw 'Patch checksum mismatch' }
$states = foreach ($record in $manifest.files) {
    $filePath = [IO.Path]::GetFullPath((Join-Path $DependencyRoot $record.path))
    if (!$filePath.StartsWith($DependencyRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Patch target escapes dependency: $($record.path)"
    }
    $blob = (git @gitArguments hash-object "--path=$($record.path)" -- $filePath).Trim()
    if ($LASTEXITCODE) { throw "Cannot hash patch input: $($record.path)" }
    if ($blob -eq $record.applied_blob) { 'applied' }
    elseif ($blob -eq $record.base_blob) { 'base' }
    else { throw "Conflict: preserve local changes in $($record.path)" }
}
if (@($states | Where-Object { $_ -eq 'applied' }).Count -eq $manifest.files.Count) {
    git @gitArguments apply --reverse --check -- $patchPath
    if ($LASTEXITCODE) { throw 'Applied patch reverse check failed' }
    Write-Output "$($manifest.perf_id): already applied; pinned SHA and all target blobs match"
    return
}
if (@($states | Where-Object { $_ -eq 'base' }).Count -ne $manifest.files.Count) {
    throw 'Conflict: mixed base/applied files; no automatic mutation performed'
}
git @gitArguments apply --check -- $patchPath
if ($LASTEXITCODE) { throw 'Patch applicability check failed' }
if ($Mode -eq 'Check') {
    Write-Output "$($manifest.perf_id): clean applicable; run with -Mode Apply before building"
    return
}
git @gitArguments apply -- $patchPath
if ($LASTEXITCODE) { throw 'Patch application failed' }
foreach ($record in $manifest.files) {
    $blob = (git @gitArguments hash-object "--path=$($record.path)" -- (Join-Path $DependencyRoot $record.path)).Trim()
    if ($LASTEXITCODE -or $blob -ne $record.applied_blob) { throw "Applied blob mismatch: $($record.path)" }
}
Write-Output "$($manifest.perf_id): applied and verified; gitlink/index unchanged"
