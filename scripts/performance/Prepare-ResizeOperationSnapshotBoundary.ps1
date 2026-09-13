param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$SnapshotId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }

$repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Require ($LASTEXITCODE -eq 0 -and [StringComparer]::OrdinalIgnoreCase.Equals(
    [IO.Path]::GetFullPath((Get-Location).Path),
    [IO.Path]::GetFullPath($repositoryRoot))) 'Run from repository root.'
$snapshotRoot = Join-Path $repositoryRoot "artifacts/performance/$SnapshotId"
Require (!(Test-Path -LiteralPath $snapshotRoot)) 'Snapshot already exists.'

& (Join-Path $PSScriptRoot 'Save-ImplementationSnapshot.ps1') -SnapshotId $SnapshotId | Out-Null
$validationRoot = Join-Path $snapshotRoot 'validation'
$binaryRoot = Join-Path $snapshotRoot 'release-test-binaries'
New-Item -ItemType Directory -Path $validationRoot,$binaryRoot | Out-Null

$targeted = 'FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.CanResizePreservesRepeatedProviderReadOrder|FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.CanResizePreservesMutationDuringRepeatedPositionRead|FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.ResizePreservesRepeatedProviderReadOrder|FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest.ResizeOperationSnapshotBoundaryCounterScenario'
$affected = 'FullyQualifiedName~TilingServiceAlgorithmicIntegrationTest'
foreach ($configuration in @('Debug','Release')) {
    foreach ($suite in @('targeted','affected')) {
        $filter = if ($suite -ceq 'targeted') { $targeted } else { $affected }
        $log = Join-Path $validationRoot "$configuration-$suite.log"
        & dotnet test FancyWM.Tests/FancyWM.Tests.csproj --configuration $configuration --no-restore `
            --filter $filter --logger "trx;LogFileName=$configuration-$suite.trx" `
            --results-directory $validationRoot *> $log
        Require ($LASTEXITCODE -eq 0) "$configuration $suite tests failed."
    }
}

$outputRoot = Join-Path $repositoryRoot 'FancyWM.Tests/bin/Release/net10.0-windows10.0.18362.0'
Require (Test-Path (Join-Path $outputRoot 'FancyWM.Tests.dll')) 'Release test output is missing.'
Copy-Item -Path (Join-Path $outputRoot '*') -Destination $binaryRoot -Recurse
$archive = @(Get-ChildItem -LiteralPath $binaryRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{
        Path = [IO.Path]::GetRelativePath($binaryRoot,$_.FullName).Replace('\','/')
        Length = $_.Length
        SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
    }
})
$archive | Export-Csv -NoTypeInformation (Join-Path $snapshotRoot 'release-test-binaries.csv')
Require (@($archive | Where-Object Path -CEQ 'FancyWM.dll').Count -eq 1 -and
    @($archive | Where-Object Path -CEQ 'FancyWM.Tests.dll').Count -eq 1) 'Required archived binaries are missing.'

[ordered]@{
    Verdict='BASELINE_FIXTURE_PASS';SnapshotId=$SnapshotId;ProductionDelta=$false;
    Fixture='FancyWM.Tests/AlgorithmicLayouts/TilingServiceAlgorithmicIntegrationTest.OperationSnapshot.cs';
    TargetedDebug='4/4';TargetedRelease='4/4';AffectedDebug='PASS';AffectedRelease='PASS';
    FancyWMDllSHA256=(@($archive | Where-Object Path -CEQ 'FancyWM.dll')[0].SHA256);
    FancyWMTestsDllSHA256=(@($archive | Where-Object Path -CEQ 'FancyWM.Tests.dll')[0].SHA256);
    CreatedUtc=[DateTimeOffset]::UtcNow.ToString('o')
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $validationRoot 'fixture-summary.json')
Get-Content -Raw (Join-Path $validationRoot 'fixture-summary.json')
