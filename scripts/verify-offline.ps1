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
$expectedStarlightCommit = "c1cd286c4909d31d355006899c5905ef6adf9741"
$expectedProtocolCommit = "69d498bebad8945dc3005f87c8afdcf87d026884"

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

Push-Location $repositoryRoot
try {
    Assert-GitCommit "vendor/Starlight" $expectedStarlightCommit "Starlight"
    Assert-GitCommit "vendor/Starlight/Protocol" $expectedProtocolCommit "Starlight Protocol"

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
    Invoke-Checked "Exporter Release build" {
        dotnet build StarlightExporter.slnx --configuration Release --no-restore
    }
    Invoke-Checked "Pinned Starlight server Release build" {
        dotnet build vendor/Starlight/Source/Starlight/Starlight.csproj `
            --configuration Release `
            --no-restore
    }
    Invoke-Checked "Exporter Release tests" {
        dotnet test StarlightExporter.slnx --configuration Release --no-build --no-restore
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

        Invoke-Checked "Real-resource module preflight" {
            dotnet run --project src/StarlightExporter.Cli `
                --configuration Release `
                --no-build `
                -- inspect tests/Fixtures/minimal-valid.json --resources $resolvedResources
        }
    }

    Write-Host "Offline verification passed."
}
finally {
    Pop-Location
}
