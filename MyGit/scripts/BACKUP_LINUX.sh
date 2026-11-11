#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
[ -f .env ] || { echo 'Missing .env' >&2; exit 1; }
mkdir -p backups
file="mygit-$(date +%Y%m%d-%H%M%S).tar.gz"
restart() { docker compose up -d; }
trap restart EXIT
docker compose stop
docker run --rm \
  -v mygit_forgejo_data:/snapshot/forgejo_data:ro \
  -v mygit_forgejo_config:/snapshot/forgejo_config:ro \
  -v mygit_postgres_data:/snapshot/postgres_data:ro \
  -v "${PWD}/backups:/backup" \
  -v "${PWD}:/project:ro" \
  alpine:3.21 tar -czf "/backup/$file" -C /snapshot forgejo_data forgejo_config postgres_data -C /project .env docker-compose.yml
echo "Backup created: backups/$file"
echo 'WARNING: archive contains secrets. Encrypt before storing elsewhere.'
