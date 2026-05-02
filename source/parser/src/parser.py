"""
Georgia Places parser — Tier-1 OSM via Overpass API.

Single-pass cron-job:
1. Query Overpass for relevant POIs in Georgia bbox.
2. Map OSM tags to PlaceCategory vocabulary.
3. UPSERT into `places` (osm_id+osm_type as natural key).
4. REFRESH MATERIALIZED VIEW places_summary.

No external API keys required. Overpass is free, rate-limited (~1 RPS).
"""
from __future__ import annotations

import json
import os
import sys
import time
from collections.abc import Iterator
from dataclasses import dataclass

import httpx
import psycopg
import structlog
from psycopg import sql
from psycopg.rows import dict_row

# ── logging ────────────────────────────────────────────────────────────
structlog.configure(
    processors=[
        structlog.processors.add_log_level,
        structlog.processors.TimeStamper(fmt="iso"),
        structlog.processors.JSONRenderer(),
    ],
)
log = structlog.get_logger()

# ── configuration ──────────────────────────────────────────────────────
OVERPASS_ENDPOINTS = [
    "https://overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
    "https://overpass.private.coffee/api/interpreter",
]
OVERPASS_TIMEOUT_S = 90
HTTP_TIMEOUT_S = 120
SLEEP_BETWEEN_QUERIES_S = 2  # be polite to public Overpass

# Georgia bbox (south,west,north,east) — covers the country with small margin.
GEORGIA_BBOX = (41.0, 39.9, 43.6, 46.8)

# OSM-tag → PlaceCategory vocabulary (must match Domain.PlaceCategory.Known).
TAG_TO_CATEGORY: dict[tuple[str, str], str] = {
    ("tourism", "viewpoint"):        "viewpoint",
    ("tourism", "museum"):           "museum",
    ("tourism", "attraction"):       "viewpoint",
    ("tourism", "winery"):           "winery",
    ("historic", "monastery"):       "monastery",
    ("historic", "church"):          "monastery",
    ("historic", "castle"):          "fortress",
    ("historic", "fort"):            "fortress",
    ("historic", "ruins"):           "fortress",
    ("historic", "memorial"):        "viewpoint",
    ("amenity", "restaurant"):       "restaurant",
    ("amenity", "monastery"):        "monastery",
    ("amenity", "marketplace"):      "market",
    ("natural", "peak"):             "mountain",
    ("natural", "waterfall"):        "waterfall",
    ("natural", "cave_entrance"):    "cave",
    ("natural", "beach"):            "beach",
    ("natural", "hot_spring"):       "thermal_spring",
    ("waterway", "waterfall"):       "waterfall",
    ("leisure", "park"):             "park",
    ("leisure", "water_park"):       "aquapark",
}

# Per-tag Overpass queries — finer split than per-category.
# `natural` previously was one regex with 5 values that 504-d on every
# Overpass mirror; per-tag queries keep each result small and give us
# partial success when one tag's dataset is overwhelmingly large.
CATEGORIES_TO_FETCH: list[tuple[str, str]] = [
    ("tourism", "viewpoint"),
    ("tourism", "museum"),
    ("tourism", "attraction"),
    ("tourism", "winery"),
    ("historic", "monastery"),
    ("historic", "castle"),
    ("historic", "fort"),
    ("historic", "ruins"),
    ("historic", "church"),
    ("historic", "memorial"),
    ("natural", "peak"),
    ("natural", "waterfall"),
    ("natural", "cave_entrance"),
    ("natural", "beach"),
    ("natural", "hot_spring"),
    ("leisure", "park"),
    ("leisure", "water_park"),
    ("amenity", "restaurant"),
    ("amenity", "marketplace"),
]


@dataclass(slots=True, frozen=True)
class OsmPoi:
    osm_type: str
    osm_id: int
    name: str
    lat: float
    lng: float
    category: str
    tags: dict[str, str]


# ── Overpass client ────────────────────────────────────────────────────
def _build_query(key: str, value: str) -> str:
    south, west, north, east = GEORGIA_BBOX
    bbox = f"{south},{west},{north},{east}"
    # Single-value tag match — much cheaper for the server than a regex.
    return (
        f"[out:json][timeout:{OVERPASS_TIMEOUT_S}];"
        f"("
        f"node[\"{key}\"=\"{value}\"]({bbox});"
        f"way[\"{key}\"=\"{value}\"]({bbox});"
        f");"
        f"out center tags;"
    )


def _fetch_overpass(query: str) -> dict | None:
    """Try each endpoint until one returns a successful JSON response.

    Sends as form-encoded `data=…` — what every Overpass UI does, and the
    only encoding overpass-api.de's main mirror reliably accepts (text/plain
    POSTs to it return 406).
    """
    for endpoint in OVERPASS_ENDPOINTS:
        try:
            with httpx.Client(timeout=HTTP_TIMEOUT_S, follow_redirects=False) as client:
                resp = client.post(endpoint, data={"data": query})
            if resp.status_code != 200:
                log.warning("overpass.http_error", endpoint=endpoint, status=resp.status_code)
                continue
            try:
                return resp.json()
            except json.JSONDecodeError as exc:
                log.warning("overpass.bad_json", endpoint=endpoint, error=str(exc))
                continue
        except httpx.HTTPError as exc:
            log.warning("overpass.network_error", endpoint=endpoint, error=str(exc))
            continue
    return None


def _iter_pois(payload: dict) -> Iterator[OsmPoi]:
    for el in payload.get("elements", []):
        tags = el.get("tags") or {}
        name = (
            tags.get("name:ru")
            or tags.get("name:en")
            or tags.get("name:ka")
            or tags.get("name")
        )
        if not name:
            continue

        category: str | None = None
        for (k, v), cat in TAG_TO_CATEGORY.items():
            if tags.get(k) == v:
                category = cat
                break
        if category is None:
            continue

        if el["type"] == "node":
            lat, lng = el.get("lat"), el.get("lon")
        elif el["type"] in ("way", "relation"):
            center = el.get("center") or {}
            lat, lng = center.get("lat"), center.get("lon")
        else:
            continue
        if lat is None or lng is None:
            continue

        yield OsmPoi(
            osm_type=el["type"],
            osm_id=el["id"],
            name=str(name)[:200],
            lat=float(lat),
            lng=float(lng),
            category=category,
            tags={k: str(v)[:200] for k, v in tags.items()},
        )


# ── DB writer ──────────────────────────────────────────────────────────
UPSERT_SQL = """
INSERT INTO places (
    name, geom, category, osm_id, osm_type, attributes, data_freshness_score,
    last_verified_at, created_at, updated_at
)
VALUES (
    %(name)s,
    ST_MakePoint(%(lng)s, %(lat)s)::geography,
    %(category)s,
    %(osm_id)s,
    %(osm_type)s,
    %(attributes)s::jsonb,
    0.7,
    now(), now(), now()
)
ON CONFLICT (osm_id, osm_type) WHERE osm_id IS NOT NULL DO UPDATE SET
    name                 = EXCLUDED.name,
    geom                 = EXCLUDED.geom,
    category             = EXCLUDED.category,
    attributes           = EXCLUDED.attributes,
    data_freshness_score = 0.7,
    last_verified_at     = now(),
    updated_at           = now();
"""


def _connect() -> psycopg.Connection:
    host = os.environ.get("DB_HOST", "postgres")
    port = os.environ.get("DB_PORT", "5432")
    db = os.environ["DB_NAME"]
    user = os.environ["DB_USER"]
    password = os.environ["DB_PASSWORD"]
    return psycopg.connect(
        host=host, port=port, dbname=db, user=user, password=password,
        application_name="gp-parser", row_factory=dict_row,
    )


def _attributes_from_tags(tags: dict[str, str]) -> dict[str, object]:
    """Project raw OSM tags into our 17-attribute dictionary (best-effort)."""
    attrs: dict[str, object] = {}

    fee = tags.get("fee")
    if fee == "no":
        attrs["free"] = True
    elif fee == "yes":
        attrs["free"] = False

    if tags.get("dog") == "yes":
        attrs["dogs"] = "friendly"
    elif tags.get("dog") == "no":
        attrs["dogs"] = "none"

    if tags.get("wheelchair") == "yes":
        attrs["wheelchair_accessible"] = True
    elif tags.get("wheelchair") == "no":
        attrs["wheelchair_accessible"] = False

    if tags.get("parking") in ("yes", "surface"):
        attrs["parking"] = True

    if tags.get("internet_access") in ("yes", "wlan", "wired"):
        attrs["wifi"] = True

    if tags.get("toilets") == "yes":
        attrs["bathrooms"] = True

    return attrs


def _insert_batch(conn: psycopg.Connection, pois: list[OsmPoi]) -> int:
    if not pois:
        return 0
    with conn.cursor() as cur:
        for p in pois:
            cur.execute(UPSERT_SQL, {
                "name":       p.name,
                "lng":        p.lng,
                "lat":        p.lat,
                "category":   p.category,
                "osm_id":     str(p.osm_id),
                "osm_type":   p.osm_type,
                "attributes": json.dumps(_attributes_from_tags(p.tags)),
            })
    return len(pois)


def _refresh_summary(conn: psycopg.Connection) -> None:
    with conn.cursor() as cur:
        cur.execute("REFRESH MATERIALIZED VIEW places_summary;")


# ── main ───────────────────────────────────────────────────────────────
def run() -> int:
    log.info("parser.start", bbox=GEORGIA_BBOX, categories=len(CATEGORIES_TO_FETCH))

    all_pois: list[OsmPoi] = []
    for key, value in CATEGORIES_TO_FETCH:
        query = _build_query(key, value)
        log.info("overpass.query", key=key, value=value)
        payload = _fetch_overpass(query)
        if payload is None:
            log.error("overpass.all_endpoints_failed", key=key, value=value)
            continue
        category_pois = list(_iter_pois(payload))
        log.info("overpass.parsed", key=key, value=value,
                 raw=len(payload.get("elements", [])), kept=len(category_pois))
        all_pois.extend(category_pois)
        time.sleep(SLEEP_BETWEEN_QUERIES_S)

    # Deduplicate within this run (some POIs match multiple categories, but
    # ON CONFLICT in DB also handles cross-run dedup).
    seen: set[tuple[str, int]] = set()
    deduped: list[OsmPoi] = []
    for p in all_pois:
        key = (p.osm_type, p.osm_id)
        if key in seen:
            continue
        seen.add(key)
        deduped.append(p)
    log.info("parser.dedup", before=len(all_pois), after=len(deduped))

    if not deduped:
        log.warning("parser.no_pois")
        return 1

    with _connect() as conn, conn.transaction():
        inserted = _insert_batch(conn, deduped)
        _refresh_summary(conn)
        log.info("parser.committed", upserts=inserted)

    log.info("parser.done", total=len(deduped))
    return 0


if __name__ == "__main__":
    sys.exit(run())
