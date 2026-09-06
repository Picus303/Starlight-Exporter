param(
    [string]$Path = ".local/official.env"
)

$ErrorActionPreference = "Stop"
$required = @(
    "STARLIGHT_EXPORTER_OFFICIAL_REGION",
    "STARLIGHT_EXPORTER_OFFICIAL_UID",
    "STARLIGHT_EXPORTER_OFFICIAL_EMAIL",
    "STARLIGHT_EXPORTER_OFFICIAL_PASSWORD"
)
$values = @{}

if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Official credential file not found at '$Path'."
}

foreach ($line in Get-Content -LiteralPath $Path) {
    $trimmed = $line.Trim()
    if ($trimmed.Length -eq 0 -or $trimmed.StartsWith("#", [StringComparison]::Ordinal)) {
        continue
    }

    $separator = $trimmed.IndexOf('=')
    if ($separator -le 0) {
        throw "Official credential file contains an invalid assignment."
    }

    $name = $trimmed.Substring(0, $separator).Trim()
    $value = $trimmed.Substring($separator + 1)
    if ($required -notcontains $name) {
        throw "Official credential file contains an unknown variable '$name'."
    }
    if ($values.ContainsKey($name)) {
        throw "Official credential file contains duplicate variable '$name'."
    }

    $values[$name] = $value
}

foreach ($name in $required) {
    if (-not $values.ContainsKey($name) -or [string]::IsNullOrWhiteSpace($values[$name])) {
        throw "Official credential file is missing required variable '$name'."
    }
}

$regions = @("os_euro", "os_usa", "os_asia", "os_cht")
if ($regions -notcontains $values["STARLIGHT_EXPORTER_OFFICIAL_REGION"]) {
    throw "Official region must be one of: $($regions -join ', ')."
}

$uid = [uint32]0
if (-not [uint32]::TryParse($values["STARLIGHT_EXPORTER_OFFICIAL_UID"], [ref]$uid) -or $uid -eq 0) {
    throw "Official UID must be a non-zero UInt32 value."
}

try {
    $email = [System.Net.Mail.MailAddress]::new($values["STARLIGHT_EXPORTER_OFFICIAL_EMAIL"])
} catch {
    throw "Official email is not structurally valid."
}
if ($email.Address -ne $values["STARLIGHT_EXPORTER_OFFICIAL_EMAIL"]) {
    throw "Official email must contain only the address."
}

if ($values["STARLIGHT_EXPORTER_OFFICIAL_PASSWORD"].Length -lt 8) {
    throw "Official password does not meet Starlight's documented minimum length."
}

Write-Output "Official credential file is structurally valid; four required values are present."
