[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SnapshotPath,

    [Parameter(Mandatory)]
    [string] $PrivateAccountId,

    [Parameter(Mandatory)]
    [string] $AccountsDatabasePath,

    [string] $ResourcesPath = ".local/resources/resources.zip",
    [string] $OutputDirectory = ".local/smoke-test/export"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Resolve-RepositoryPath([string] $Path) {
    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    return [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Assert-File([string] $Path, [string] $Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label not found: '$Path'."
    }
}

$resolvedSnapshot = Resolve-RepositoryPath $SnapshotPath
$resolvedResources = Resolve-RepositoryPath $ResourcesPath
$resolvedAccounts = Resolve-RepositoryPath $AccountsDatabasePath
$resolvedOutput = Resolve-RepositoryPath $OutputDirectory

Assert-File $resolvedSnapshot "Snapshot"
Assert-File $resolvedAccounts "Private account database"
if (-not (Test-Path -LiteralPath $resolvedResources)) {
    throw "Resources not found: '$resolvedResources'."
}
if (Test-Path -LiteralPath $resolvedOutput) {
    throw "Smoke-test output already exists: '$resolvedOutput'."
}

Push-Location $repositoryRoot
try {
    & (Join-Path $PSScriptRoot "verify-offline.ps1") -ResourcesPath $resolvedResources
    if ($LASTEXITCODE -ne 0) {
        throw "Offline verification failed."
    }

    dotnet run --project src/StarlightExporter.Cli `
        --configuration Release `
        --no-build `
        -- build-db $resolvedSnapshot `
        --resources $resolvedResources `
        --output $resolvedOutput `
        --private-account-id $PrivateAccountId `
        --accounts-db $resolvedAccounts `
        --uid-mode preserve
    if ($LASTEXITCODE -ne 0) {
        throw "Smoke-test database preparation failed with exit code $LASTEXITCODE."
    }

    $databasePath = Join-Path $resolvedOutput "starlight.db"
    $reportPath = Join-Path $resolvedOutput "import-report.json"
    Assert-File $databasePath "Generated Starlight database"
    Assert-File $reportPath "Import report"

    $unexpected = @(Get-ChildItem -LiteralPath $resolvedOutput -File | Where-Object {
        $_.Name -notin @("starlight.db", "import-report.json")
    })
    if ($unexpected.Count -gt 0) {
        throw "Unexpected smoke-test artifact(s): $($unexpected.Name -join ', ')."
    }

    $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($report.result -notlike "success*" -or -not $report.moduleValidation.isCompatible) {
        throw "The generated import report does not describe a module-compatible success."
    }
    $targetLock = Get-Content -LiteralPath (Join-Path $repositoryRoot "starlight-target.lock.json") `
        -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not [string]::Equals(
            $report.targetStarlightCommit,
            $targetLock.starlightCommit,
            [StringComparison]::Ordinal) `
        -or -not [string]::Equals(
            $report.targetProtocolCommit,
            $targetLock.protocolCommit,
            [StringComparison]::Ordinal)) {
        throw "The import report target does not match starlight-target.lock.json."
    }

    Write-Host "Smoke-test package ready: '$resolvedOutput'."
    Write-Host "Player UID: $($report.privateUid); private account ID: $($report.privateAccountId)."
    Write-Host "Follow SMOKE_TEST.md to launch Starlight with this database."
}
finally {
    Pop-Location
}
