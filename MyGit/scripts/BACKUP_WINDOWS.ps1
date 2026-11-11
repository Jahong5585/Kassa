# Cold backup of the full Forgejo instance: repositories, config, DB, env.
# Requires the stack to have been installed. It stops MyGit briefly for consistency.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path '.env')) { throw 'Missing .env, run install first.' }
New-Item -ItemType Directory -Path 'backups' -Force | Out-Null
$backupDir = (Resolve-Path 'backups').Path
$filename = 'mygit-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.tar.gz'
try {
    & docker compose stop
    if ($LASTEXITCODE -ne 0) { throw 'Cannot stop stack safely.' }
    & docker run --rm `
       -v 'mygit_forgejo_data:/snapshot/forgejo_data:ro' `
       -v 'mygit_forgejo_config:/snapshot/forgejo_config:ro' `
       -v 'mygit_postgres_data:/snapshot/postgres_data:ro' `
       -v "${backupDir}:/backup" `
       -v "${PWD}:/project:ro" `
       alpine:3.21 tar -czf "/backup/$filename" -C /snapshot forgejo_data forgejo_config postgres_data -C /project .env docker-compose.yml
    if ($LASTEXITCODE -ne 0) { throw 'Backup archive failed.' }
    Write-Host "Created: $backupDir\$filename"
    Write-Host 'WARNING: archive contains .env secrets; encrypt before off-site storage.'
} finally {
    & docker compose up -d
}
