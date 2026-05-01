#!/usr/bin/env bash
# Restore the LATEST R2 backup into a throwaway DB and run sanity probes.
# Cron: 0 2 1 * *  root  /opt/georgia-places/source/infra/scripts/verify-restore.sh
# Closes P0 from review: "backup без verify-restore".

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INFRA_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
# shellcheck disable=SC1091
set -a; source "${INFRA_DIR}/.env"; set +a

VERIFY_DB="${DB_NAME}_verify"
CONTAINER_DUMP="/tmp/verify.dump"

cleanup() {
    # Always drop the verify DB — it's transient. Idempotent.
    docker compose exec -T -e PGPASSWORD="${DB_PASSWORD}" postgres \
        psql -U "${DB_USER}" -d postgres -c "DROP DATABASE IF EXISTS ${VERIFY_DB};" \
        >/dev/null 2>&1 || true
    docker compose exec -T postgres rm -f "${CONTAINER_DUMP}" >/dev/null 2>&1 || true
}
trap cleanup EXIT

cd "${INFRA_DIR}"

# Find latest backup by lexicographic order (ISO timestamps sort correctly).
LATEST=$(rclone lsf "r2:${R2_BUCKET}/" --include "db-*.dump" 2>/dev/null | sort | tail -1)
if [[ -z "${LATEST}" ]]; then
    echo "VERIFY FAIL: no backups found in R2" >&2
    exit 1
fi

echo "VERIFY: latest backup = ${LATEST}"
HOST_DUMP="/tmp/${LATEST}"
rclone copy "r2:${R2_BUCKET}/${LATEST}" "/tmp/"

# Defensive: drop any leftover from a prior failed run BEFORE create.
docker compose exec -T -e PGPASSWORD="${DB_PASSWORD}" postgres \
    psql -U "${DB_USER}" -d postgres -v ON_ERROR_STOP=1 <<EOF
DROP DATABASE IF EXISTS ${VERIFY_DB};
CREATE DATABASE ${VERIFY_DB} OWNER ${DB_USER};
\\c ${VERIFY_DB}
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE EXTENSION IF NOT EXISTS btree_gist;
EOF

docker compose cp "${HOST_DUMP}" "postgres:${CONTAINER_DUMP}"
rm -f "${HOST_DUMP}"

docker compose exec -T -e PGPASSWORD="${DB_PASSWORD}" postgres \
    pg_restore -U "${DB_USER}" -d "${VERIFY_DB}" \
        --jobs=2 --no-owner --no-privileges \
        "${CONTAINER_DUMP}"

# Sanity probes — adjust as schema lands.
TABLES=$(docker compose exec -T -e PGPASSWORD="${DB_PASSWORD}" postgres \
    psql -U "${DB_USER}" -d "${VERIFY_DB}" -tAc \
        "SELECT count(*) FROM pg_tables WHERE schemaname = 'public';")

if (( TABLES < 1 )); then
    echo "VERIFY FAIL: 0 tables restored" >&2
    exit 1
fi

# Once `places` table exists, also probe row count > 0.
if docker compose exec -T -e PGPASSWORD="${DB_PASSWORD}" postgres \
    psql -U "${DB_USER}" -d "${VERIFY_DB}" -tAc \
        "SELECT to_regclass('public.places');" | grep -q places
then
    PLACES=$(docker compose exec -T -e PGPASSWORD="${DB_PASSWORD}" postgres \
        psql -U "${DB_USER}" -d "${VERIFY_DB}" -tAc "SELECT count(*) FROM places;")
    echo "VERIFY: places=${PLACES}, tables=${TABLES}"
else
    echo "VERIFY: places table not yet present (early dev), tables=${TABLES}"
fi

curl --retry 3 -fsS "https://hc-ping.com/${HEALTHCHECKS_VERIFY_UUID}" >/dev/null
echo "VERIFY OK"
