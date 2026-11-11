# MyGit - first-time install, Windows 10/11 PowerShell.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

function New-RandomHex {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is not installed. Install Docker Desktop (Windows 10/11) and start it.'
}
& docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker Engine is not running. Start Docker Desktop first.' }
& docker compose version
if ($LASTEXITCODE -ne 0) { throw 'Docker Compose plugin was not found.' }

if (-not (Test-Path '.env')) {
    $db = New-RandomHex
    $secret = New-RandomHex
    $token = New-RandomHex
    $content = "DB_PASSWORD=$db`nFORGEJO_SECRET_KEY=$secret`nFORGEJO_INTERNAL_TOKEN=$token`nMYGIT_PORT=3020`nMYGIT_ROOT_URL=http://localhost:3020/`n"
    [System.IO.File]::WriteAllText((Join-Path (Get-Location) '.env'), $content, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host 'Created .env with unique random secrets. Keep it private.'
} else {
    Write-Host '.env exists: existing secrets are preserved.'
}

& docker compose config -q
if ($LASTEXITCODE -ne 0) { throw 'Docker Compose config validation failed.' }
& docker compose up -d
if ($LASTEXITCODE -ne 0) { throw 'Failed to start containers. Check Docker logs.' }
Write-Host ''
Write-Host 'MyGit started: http://localhost:3020'
Write-Host 'On first visit, complete the INSTALL wizard and create ADMINISTRATOR ACCOUNT.'
Write-Host 'The service is accessible ONLY on this computer until private access is configured.'
Write-Host 'Logs: docker compose logs -f forgejo'
