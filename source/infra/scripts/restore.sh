#!/usr/bin/env bash
# Restore Postgres from an R2 backup.
# Usage: ./restore.sh <YYYY-MM-DDTHH-MM-SSZ>
# Restores into a SEPARATE database <DB_NAME>_restore — does NOT touch live DB.
# Promotion to live is a manual ALTER (intentional — restore must be reviewed).

set -euo pipefail

if [[ $# -lt 1 ]]; then
    echo "Usage: $0 <DATE>  (e.g. 2026-05-01T01-00-00Z)" >&2
    echo "Available backups in R2:" >&2
    rclone ls "r2:${R2_BUCKET}/" 2>/dev/null | head -20 >&2 || true
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INFRA_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
# shellcheck disable=SC1091
set -a; source "${INFRA_DIR}/.env"; set +a

DATE="$1"
DUMP_NAME="db-${DATE}.dump"
HOST_DUMP="/tmp/${DUMP_NAME}"
CONTAINER_DUMP="/tmp/restore.dump"
RESTORE_DB="${DB_NAME}_restore"

cleanup() { rm -f "${HOST_DUMP}"; }
trap cleanup EXIT

cd "${INFRA_DIR}"

rclone copy "r2:${R2_BUCKET}/${DUMP_NAME}" "/tmp/" --progress

# Recreate the restore DB. PostGIS extension must be added BEFORE pg_restore
# because the dump contains references to PostGIS types.
docker compose exec -T -e PGPASSWORD="${DB_PASSWORD}" postgres \
    psql -U "${DB_USER}" -d postgres -v ON_ERROR_STOP=1 <<EOF
DROP DATABASE IF EXISTS ${RESTORE_DB};
CREATE DATABASE ${RESTORE_DB} OWNER ${DB_USER};
\\c ${RESTORE_DB}
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE EXTENSION IF NOT EXISTS btree_gist;
EOF

docker compose cp "${HOST_DUMP}" "postgres:${CONTAINER_DUMP}"

docker compose exec -T -e PGPASSWORD="${DB_PASSWORD}" postgres \
    pg_restore -U "${DB_USER}" -d "${RESTORE_DB}" \
        --jobs=2 --no-owner --no-privileges \
        "${CONTAINER_DUMP}"

# Sanity probe.
COUNT=$(docker compose exec -T -e PGPASSWORD="${DB_PASSWORD}" postgres \
    psql -U "${DB_USER}" -d "${RESTORE_DB}" -tAc "SELECT count(*) FROM pg_tables WHERE schemaname = 'public';")

echo "RESTORE OK: ${RESTORE_DB} contains ${COUNT} public tables."
echo "To promote: psql ... -c 'ALTER DATABASE ${DB_NAME} RENAME TO ${DB_NAME}_old; ALTER DATABASE ${RESTORE_DB} RENAME TO ${DB_NAME};'"
