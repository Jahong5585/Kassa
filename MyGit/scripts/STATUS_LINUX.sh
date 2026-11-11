#!/usr/bin/env bash
cd "$(dirname "$0")/.."
docker compose ps
docker compose logs --tail 40 forgejo
