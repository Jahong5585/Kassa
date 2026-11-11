Set-Location (Split-Path -Parent $PSScriptRoot)
docker compose ps
docker compose logs --tail 40 forgejo
