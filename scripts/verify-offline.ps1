[CmdletBinding()]
param(
    [string] $ResourcesPath = ".local/resources/resources.zip",
    [switch] $SkipRealResources,
    [switch] $SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$env:MSBUILDDISABLENODEREUSE = "1"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$targetLock = Get-Content -LiteralPath (Join-Path $repositoryRoot "starlight-target.lock.json") `
    -Raw -Encoding UTF8 | ConvertFrom-Json
$expectedStarlightCommit = $targetLock.starlightCommit
$expectedProtocolCommit = $targetLock.protocolCommit
$previousTestResources = $env:STARLIGHT_EXPORTER_TEST_RESOURCES

function Invoke-Checked([string] $Label, [scriptblock] $Command) {
    Write-Host "==> $Label"
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

function Assert-GitCommit([string] $Path, [string] $Expected, [string] $Label) {
    $actual = (& git -C $Path rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read the $Label revision at '$Path'. Are submodules initialized recursively?"
    }
    if (-not [string]::Equals($actual, $Expected, [StringComparison]::Ordinal)) {
        throw "$Label revision mismatch: expected $Expected, got $actual."
    }
    Write-Host "$Label revision: $actual"
}

function Assert-GitClean([string] $Path, [string] $Label) {
    $changes = @(& git -C $Path status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect the $Label worktree at '$Path'."
    }
    if ($changes.Count -ne 0) {
        throw "$Label worktree must remain unmodified."
    }
    Write-Host "$Label worktree: clean"
}

Push-Location $repositoryRoot
try {
    Assert-GitCommit "vendor/Starlight" $expectedStarlightCommit "Starlight"
    Assert-GitCommit "vendor/Starlight/Protocol" $expectedProtocolCommit "Starlight Protocol"
    Assert-GitClean "vendor/Starlight/Protocol" "Starlight Protocol"
    Assert-GitClean "vendor/Starlight" "Starlight"

    if (-not $SkipRestore) {
        Invoke-Checked "Exporter dependency restore" {
            dotnet restore StarlightExporter.slnx
        }
        Invoke-Checked "Pinned Starlight server dependency restore" {
            dotnet restore vendor/Starlight/Source/Starlight/Starlight.csproj
        }
    }

    Invoke-Checked "Exporter formatting" {
        dotnet format whitespace StarlightExporter.slnx --include src tests --no-restore --verify-no-changes
    }
    Invoke-Checked "Pinned Starlight server Release build" {
        dotnet build vendor/Starlight/Source/Starlight/Starlight.csproj `
            --configuration Release `
            --no-restore
    }
    Invoke-Checked "Exporter Release build" {
        dotnet build src/StarlightExporter.Cli/StarlightExporter.Cli.csproj `
            --configuration Release --no-restore
    }
    Invoke-Checked "Exporter unit tests" {
        dotnet test tests/StarlightExporter.UnitTests/StarlightExporter.UnitTests.csproj `
            --configuration Release --no-restore
    }
    Invoke-Checked "Official client offline tests" {
        dotnet test tests/StarlightExporter.OfficialTests/StarlightExporter.OfficialTests.csproj `
            --configuration Release --no-restore
    }

    if (-not $SkipRealResources) {
        $resolvedResources = if ([IO.Path]::IsPathRooted($ResourcesPath)) {
            [IO.Path]::GetFullPath($ResourcesPath)
        }
        else {
            [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ResourcesPath))
        }
        if (-not (Test-Path -LiteralPath $resolvedResources)) {
            throw "Real resources not found at '$resolvedResources'. Use -SkipRealResources only in CI."
        }

        $resourceLock = Get-Content -LiteralPath (Join-Path $repositoryRoot "resources.lock.json") `
            -Raw -Encoding UTF8 | ConvertFrom-Json
        $resourceMetadataPath = $resolvedResources + ".metadata.json"
        if (-not (Test-Path -LiteralPath $resourceMetadataPath -PathType Leaf)) {
            throw "Prepared-resource metadata not found: '$resourceMetadataPath'."
        }
        $resourceMetadata = Get-Content -LiteralPath $resourceMetadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not [string]::Equals($resourceMetadata.repository, $resourceLock.repository, [StringComparison]::Ordinal) `
            -or -not [string]::Equals($resourceMetadata.revision, $resourceLock.revision, [StringComparison]::Ordinal)) {
            throw "Prepared resources do not match resources.lock.json."
        }
        $resourceHash = (Get-FileHash -LiteralPath $resolvedResources -Algorithm SHA256).Hash
        if (-not [string]::Equals($resourceHash, $resourceMetadata.preparedSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Prepared-resource SHA256 mismatch: expected $($resourceMetadata.preparedSha256), got $resourceHash."
        }
        Write-Host "Resources revision: $($resourceMetadata.revision); SHA256: $resourceHash"
        $env:STARLIGHT_EXPORTER_TEST_RESOURCES = $resolvedResources

        Invoke-Checked "Real-resource module preflight" {
            dotnet run --project src/StarlightExporter.Cli `
                --configuration Release `
                --no-build `
                -- inspect tests/Fixtures/minimal-valid.json --resources $resolvedResources
        }
    }

    Invoke-Checked "Starlight module and database compatibility tests" {
        dotnet test tests/StarlightExporter.StarlightCompatibilityTests/StarlightExporter.StarlightCompatibilityTests.csproj `
            --configuration Release --no-restore
    }

    if (-not $SkipRealResources) {
        Invoke-Checked "Pinned Starlight server smoke test" {
            dotnet test tests/StarlightExporter.ServerSmokeTests/StarlightExporter.ServerSmokeTests.csproj `
                --configuration Release --no-restore
        }
    }

    Write-Host "Offline verification passed."
}
finally {
    $env:STARLIGHT_EXPORTER_TEST_RESOURCES = $previousTestResources
    Pop-Location
}
