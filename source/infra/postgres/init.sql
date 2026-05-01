-- Init script applied once on first postgres start (entrypoint scans /docker-entrypoint-initdb.d).
-- Idempotent — safe on re-init.

-- PostGIS spatial extension required for GEOGRAPHY/GEOMETRY columns (TZ §4.1).
CREATE EXTENSION IF NOT EXISTS postgis;

-- Crypto / hashing helpers used by IP subnet anonymization (ADR-0007).
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- Faster btree for partial / functional indexes used by hot reads.
CREATE EXTENSION IF NOT EXISTS btree_gist;

-- Application schema is created/managed by EF Core migrations from the API.
-- This file only ensures extensions are present and timezone is UTC.
SET TIMEZONE = 'UTC';
ALTER DATABASE CURRENT SET TIMEZONE TO 'UTC';
