$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = 'kasa-operator-rollback-' + $PID
$env:POS_DB_OWNER_PASSWORD = [Guid]::NewGuid().ToString('N')
$env:POS_DB_APP_PASSWORD = [Guid]::NewGuid().ToString('N')
$env:POS_DB_PORT = '55443'

try {
    docker compose --file (Join-Path $root 'compose.yaml') --project-name $project up -d --wait db
    if ($LASTEXITCODE -ne 0) { throw 'Disposable PostgreSQL failed to start.' }
    Get-Content -LiteralPath (Join-Path $root 'db\rollback\038_store_operator_auth.down.sql') -Raw |
        docker compose --file (Join-Path $root 'compose.yaml') --project-name $project exec -T `
            db psql -XAt -U kasa_owner -d kasa -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) { throw 'Migration rollback failed.' }
    $result = docker compose --file (Join-Path $root 'compose.yaml') --project-name $project exec -T `
        db psql -XAt -U kasa_owner -d kasa -v ON_ERROR_STOP=1 -c `
        "SELECT to_regclass('staff_store_roles') IS NULL AND to_regclass('operator_sessions') IS NULL AND EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'staff_subject_key');"
    if ($LASTEXITCODE -ne 0 -or $result.Trim() -ne 't') { throw 'Rollback schema verification failed.' }
    Write-Output 'PASS: migration 038 rollback restores the legacy staff schema'
}
finally {
    docker compose --file (Join-Path $root 'compose.yaml') --project-name $project down --volumes *> $null
}
