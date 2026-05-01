#!/usr/bin/env bash
# Daily Postgres backup → Cloudflare R2 via rclone.
# Cron: 0 1 * * *  root  /opt/georgia-places/source/infra/scripts/backup.sh
# RPO=24h per ADR-0002.

set -euo pipefail

# Source .env from the same directory tree.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INFRA_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
# shellcheck disable=SC1091
set -a; source "${INFRA_DIR}/.env"; set +a

DATE="$(date -u +%Y-%m-%dT%H-%M-%SZ)"
DUMP_NAME="db-${DATE}.dump"
HOST_DUMP="/tmp/${DUMP_NAME}"
CONTAINER_DUMP="/tmp/${DUMP_NAME}"
PG_CONTAINER="gp-postgres"

cleanup() { rm -f "${HOST_DUMP}"; }
trap cleanup EXIT

cd "${INFRA_DIR}"

# pg_dump --format=custom keeps metadata + supports parallel restore (ADR-0002).
# PGPASSWORD is acceptable on a single-tenant VPS (REVIEW-BACKLOG note).
docker compose exec -T \
    -e PGPASSWORD="${DB_PASSWORD}" \
    "${PG_CONTAINER#gp-}" \
    pg_dump -U "${DB_USER}" -d "${DB_NAME}" \
        --format=custom --compress=9 \
        -f "${CONTAINER_DUMP}"

# Pull from container to host.
docker compose cp "${PG_CONTAINER#gp-}:${CONTAINER_DUMP}" "${HOST_DUMP}"

# Sanity: refuse to upload a dump smaller than 1KB (likely corrupted).
SIZE=$(stat -c%s "${HOST_DUMP}" 2>/dev/null || stat -f%z "${HOST_DUMP}")
if (( SIZE < 1024 )); then
    echo "BACKUP FAIL: dump suspiciously small (${SIZE} bytes)" >&2
    exit 1
fi

# Push to R2. r2: remote configured in rclone.conf (mode 0600, in .gitignore).
rclone copy "${HOST_DUMP}" "r2:${R2_BUCKET}/" --progress

# Notify Healthchecks.io — last step, only if everything above succeeded.
curl --retry 3 -fsS "https://hc-ping.com/${HEALTHCHECKS_BACKUP_UUID}" >/dev/null

echo "BACKUP OK: ${DUMP_NAME} (${SIZE} bytes)"
