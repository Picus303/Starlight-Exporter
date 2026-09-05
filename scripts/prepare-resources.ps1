[CmdletBinding()]
param(
    [string] $SourceArchive,
    [string] $OutputPath = ".local/resources/resources.zip",
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$lockPath = Join-Path $repositoryRoot "resources.lock.json"
$resourceLock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json

function Resolve-WorkspacePath([string] $Path) {
    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }

    return [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

$resolvedOutput = Resolve-WorkspacePath $OutputPath
$outputParent = Split-Path -Parent $resolvedOutput
if ([string]::IsNullOrWhiteSpace($outputParent)) {
    throw "The output archive must have a parent directory."
}

if ((Test-Path -LiteralPath $resolvedOutput) -and -not $Force) {
    throw "Output already exists: '$resolvedOutput'. Pass -Force to replace it atomically."
}

$downloadPath = $null
$temporaryOutput = $null
$backupOutput = $null
try {
    if ([string]::IsNullOrWhiteSpace($SourceArchive)) {
        $downloadPath = Join-Path ([IO.Path]::GetTempPath()) ("starlight-resources-" + [Guid]::NewGuid().ToString("N") + ".zip")
        $downloadUri = "https://github.com/$($resourceLock.repository)/archive/$($resourceLock.revision).zip"
        Write-Host "Downloading pinned resources $($resourceLock.revision)..."
        Invoke-WebRequest -Uri $downloadUri -OutFile $downloadPath
        $resolvedSource = $downloadPath
    }
    else {
        $resolvedSource = Resolve-WorkspacePath $SourceArchive
    }

    if (-not (Test-Path -LiteralPath $resolvedSource -PathType Leaf)) {
        throw "Source archive not found: '$resolvedSource'."
    }

    New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
    $temporaryOutput = Join-Path $outputParent ("." + [IO.Path]::GetFileName($resolvedOutput) + "." + [Guid]::NewGuid().ToString("N") + ".tmp")

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $sourceStream = [IO.File]::Open($resolvedSource, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $sourceZip = [IO.Compression.ZipArchive]::new($sourceStream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            $files = @($sourceZip.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
            $requiredEntries = @($resourceLock.requiredEntries)
            $prefix = ""

            $missingAtRoot = @($requiredEntries | Where-Object { $null -eq $sourceZip.GetEntry([string]$_) })
            if ($missingAtRoot.Count -gt 0) {
                $firstRequired = [string]$requiredEntries[0]
                $suffix = "/$firstRequired"
                $matches = @($files | Where-Object { $_.FullName.EndsWith($suffix, [StringComparison]::Ordinal) })
                if ($matches.Count -ne 1) {
                    throw "Could not identify a unique archive root containing '$firstRequired'."
                }

                $prefix = $matches[0].FullName.Substring(0, $matches[0].FullName.Length - $firstRequired.Length)
            }

            foreach ($required in $requiredEntries) {
                if ($null -eq $sourceZip.GetEntry($prefix + $required)) {
                    throw "Required resource entry is missing: '$required'."
                }
            }

            $targetStream = [IO.FileStream]::new(
                $temporaryOutput,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $targetZip = [IO.Compression.ZipArchive]::new($targetStream, [IO.Compression.ZipArchiveMode]::Create, $false)
                try {
                    $copied = 0
                    foreach ($sourceEntry in $files) {
                        if (-not $sourceEntry.FullName.StartsWith($prefix, [StringComparison]::Ordinal)) {
                            continue
                        }

                        $targetName = $sourceEntry.FullName.Substring($prefix.Length)
                        if ([string]::IsNullOrWhiteSpace($targetName)) {
                            continue
                        }

                        $targetEntry = $targetZip.CreateEntry($targetName, [IO.Compression.CompressionLevel]::Optimal)
                        $targetEntry.LastWriteTime = $sourceEntry.LastWriteTime
                        $input = $sourceEntry.Open()
                        $output = $targetEntry.Open()
                        try {
                            $input.CopyTo($output)
                        }
                        finally {
                            $output.Dispose()
                            $input.Dispose()
                        }
                        $copied++
                    }
                }
                finally {
                    $targetZip.Dispose()
                }
            }
            finally {
                $targetStream.Dispose()
            }
        }
        finally {
            $sourceZip.Dispose()
        }
    }
    finally {
        $sourceStream.Dispose()
    }

    $verificationZip = [IO.Compression.ZipFile]::OpenRead($temporaryOutput)
    try {
        foreach ($required in $resourceLock.requiredEntries) {
            if ($null -eq $verificationZip.GetEntry([string]$required)) {
                throw "Normalized archive verification failed for '$required'."
            }
        }
    }
    finally {
        $verificationZip.Dispose()
    }

    $sourceHash = (Get-FileHash -LiteralPath $resolvedSource -Algorithm SHA256).Hash
    $preparedHash = (Get-FileHash -LiteralPath $temporaryOutput -Algorithm SHA256).Hash
    if (Test-Path -LiteralPath $resolvedOutput) {
        $backupOutput = $resolvedOutput + ".backup"
        [IO.File]::Replace($temporaryOutput, $resolvedOutput, $backupOutput, $true)
        Remove-Item -LiteralPath $backupOutput -Force
        $backupOutput = $null
    }
    else {
        [IO.File]::Move($temporaryOutput, $resolvedOutput)
    }
    $temporaryOutput = $null

    $metadata = [ordered]@{
        repository = $resourceLock.repository
        revision = $resourceLock.revision
        preparedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        sourceSha256 = $sourceHash
        preparedSha256 = $preparedHash
        entryCount = $copied
    }
    $metadataPath = $resolvedOutput + ".metadata.json"
    $metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding UTF8

    Write-Host "Prepared $copied files at '$resolvedOutput'."
    Write-Host "SHA256: $preparedHash"
}
finally {
    if ($null -ne $temporaryOutput -and (Test-Path -LiteralPath $temporaryOutput)) {
        Remove-Item -LiteralPath $temporaryOutput -Force
    }
    if ($null -ne $downloadPath -and (Test-Path -LiteralPath $downloadPath)) {
        Remove-Item -LiteralPath $downloadPath -Force
    }
    if ($null -ne $backupOutput -and (Test-Path -LiteralPath $backupOutput)) {
        Remove-Item -LiteralPath $backupOutput -Force
    }
}
