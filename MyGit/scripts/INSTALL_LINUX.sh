#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
command -v docker >/dev/null || { echo 'Install Docker Engine + Docker Compose first.' >&2; exit 1; }
docker info >/dev/null
docker compose version >/dev/null

random_hex() {
  if command -v openssl >/dev/null; then openssl rand -hex 32
  elif command -v python3 >/dev/null; then python3 -c 'import secrets; print(secrets.token_hex(32))'
  else echo 'openssl or python3 required for secret generation' >&2; return 1; fi
}
if [ ! -e .env ]; then
  umask 077
  cat > .env <<ENVFILE
DB_PASSWORD=$(random_hex)
FORGEJO_SECRET_KEY=$(random_hex)
FORGEJO_INTERNAL_TOKEN=$(random_hex)
MYGIT_PORT=3020
MYGIT_ROOT_URL=http://localhost:3020/
ENVFILE
  chmod 600 .env
  echo 'Generated .env with secure secrets. Never commit or share it.'
else
  echo 'Reusing existing .env and secrets.'
fi

docker compose config -q
docker compose up -d
echo 'MyGit is starting: http://localhost:3020/'
echo 'Finish the installation wizard and CREATE THE ADMINISTRATOR ACCOUNT.'
echo 'Logs: docker compose logs -f forgejo'
