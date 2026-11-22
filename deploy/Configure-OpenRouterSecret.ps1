[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SourceEnvFile,

    [Parameter(Mandatory = $true)]
    [string] $TargetEnvFile,

    [Guid] $OrganizationId = [Guid]::Empty,

    [Guid] $StoreId = [Guid]::Empty,

    [Guid] $StaffId = [Guid]::Empty,

    [string] $TargetAccessOverrideFile
)

$ErrorActionPreference = 'Stop'

function Read-EnvValue {
    param([string] $Path, [string] $Name)

    $line = Get-Content -LiteralPath $Path | Where-Object {
        $_ -match ('^\s*' + [regex]::Escape($Name) + '\s*=')
    } | Select-Object -Last 1
    if ($null -eq $line) {
        throw "Missing $Name in source environment file."
    }

    $value = ($line -split '=', 2)[1].Trim()
    if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
        ($value.StartsWith("'") -and $value.EndsWith("'"))) {
        $value = $value.Substring(1, $value.Length - 2)
    }
    if ([string]::IsNullOrWhiteSpace($value) -or $value.Contains("`r") -or $value.Contains("`n")) {
        throw "$Name is empty or invalid."
    }
    return $value
}

function Set-EnvValue {
    param([System.Collections.Generic.List[string]] $Lines, [string] $Name, [string] $Value)

    $pattern = '^\s*' + [regex]::Escape($Name) + '\s*='
    $replacement = "$Name=$Value"
    $found = $false
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        if ($Lines[$index] -match $pattern) {
            $Lines[$index] = $replacement
            $found = $true
        }
    }
    if (-not $found) {
        $Lines.Add($replacement)
    }
}

$source = (Resolve-Path -LiteralPath $SourceEnvFile).Path
$target = (Resolve-Path -LiteralPath $TargetEnvFile).Path
$key = Read-EnvValue -Path $source -Name 'OPENROUTER_API_KEY'

$lines = [System.Collections.Generic.List[string]]::new()
foreach ($line in Get-Content -LiteralPath $target) {
    $lines.Add($line)
}

Set-EnvValue $lines 'OPENROUTER_API_KEY' $key
Set-EnvValue $lines 'Ai__ProcurementEnabled' 'true'
Set-EnvValue $lines 'Ai__Provider' 'OpenRouter'
Set-EnvValue $lines 'KASA_AI_OPENROUTER_REFERER' 'https://kassa.qr-space.uz'
Set-EnvValue $lines 'KASA_AI_OPENROUTER_TITLE' 'Kassa Minimarket'

$scopeIds = @($OrganizationId, $StoreId, $StaffId)
$hasAnyScope = @($scopeIds | Where-Object { $_ -ne [Guid]::Empty }).Count -gt 0
$hasCompleteScope = @($scopeIds | Where-Object { $_ -eq [Guid]::Empty }).Count -eq 0
if ($hasAnyScope -and -not $hasCompleteScope) {
    throw 'OrganizationId, StoreId and StaffId must be supplied together.'
}
if ($hasCompleteScope -and [string]::IsNullOrWhiteSpace($TargetAccessOverrideFile)) {
    throw 'TargetAccessOverrideFile is required when an access scope is supplied.'
}

$backup = "$target.backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Copy-Item -LiteralPath $target -Destination $backup
$temporary = "$target.tmp-$([guid]::NewGuid().ToString('N'))"
try {
    [System.IO.File]::WriteAllLines($temporary, $lines, [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $target -Force
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Force
    }
}

if ($hasCompleteScope) {
    $overridePath = [System.IO.Path]::GetFullPath($TargetAccessOverrideFile)
    $accessName = "Ai__ProcurementAccess__$($OrganizationId.ToString('D'))__$($StoreId.ToString('D'))__$($StaffId.ToString('D'))"
    $overrideLines = @(
        'services:'
        '  api:'
        '    environment:'
        "      $accessName`: 'true'"
    )
    $overrideDirectory = Split-Path -Parent $overridePath
    if (-not (Test-Path -LiteralPath $overrideDirectory)) {
        New-Item -ItemType Directory -Path $overrideDirectory | Out-Null
    }
    $overrideTemporary = "$overridePath.tmp-$([guid]::NewGuid().ToString('N'))"
    try {
        [System.IO.File]::WriteAllLines($overrideTemporary, $overrideLines, [System.Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $overrideTemporary -Destination $overridePath -Force
    }
    finally {
        if (Test-Path -LiteralPath $overrideTemporary) {
            Remove-Item -LiteralPath $overrideTemporary -Force
        }
    }
}

Write-Host 'OpenRouter settings updated. Secret value was not displayed.'
Write-Host "Backup: $backup"
if ($hasCompleteScope) {
    Write-Host 'Scoped access override updated. No secret value was displayed.'
}
