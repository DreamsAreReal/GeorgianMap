# Georgia Places — Backlog

Структура: эпики из ТЗ §13 + кросс-секционные эпики (Infra, Observability, Security). Каждая задача → статус → ссылка на PR/ADR. Residual P0/P1/P2 из код-ревью трекаются в [REVIEW-BACKLOG.md](REVIEW-BACKLOG.md), здесь — крупные единицы работы.

**Статусы:** `done` ✅ | `wip` 🚧 | `next` ⏭ | `todo` ⬜ | `blocked` ⛔

**Последнее обновление:** 2026-05-02

---

## Epic 0 — Foundation (✅ done)

Каркас репо, инфра, скелет backend, наблюдаемость, branch protection.

| # | Задача | Статус | PR | Заметки |
|---|--------|--------|-----|---------|
| 0.1 | Структура репо + ТЗ + .gitignore + ruleset | ✅ | #1 | `main` ruleset активен |
| 0.2 | 5 ADR (monolith, RPO, GC, versioning, branch) | ✅ | #1 | |
| 0.3 | 4 ADR (observability, brigading, consensus, MV) | ✅ | #2 | |
| 0.4 | Закрыть 8 P0 + 5 P1 из 2-го авто-ревью | ✅ | #3 | |
| 0.5 | Infra (compose, nginx, postgres init, ops scripts) | ✅ | #4 | |
| 0.6 | Backend skeleton (.NET 9 Clean Arch + middleware) | ✅ | #4 | Serilog + OTel + Sentry + Polly + ProblemDetails |
| 0.7 | Place domain + EF schema + GET /api/v1/places | ✅ | #5 | Cursor pagination, PostGIS filters |
| 0.8 | ADR-0009 + publish image to ghcr.io | ✅ | #6 | `ghcr.io/dreamsarereal/georgianmap-api:latest` |

---

## Epic 1 — Read API for places (🚧 wip)

§13 Этап 1. Read API + statics. **Цель: пользователь видит карту с маркерами, кликает — открывается deeplink.**

| # | Задача | Статус | Refs | Заметки |
|---|--------|--------|------|---------|
| 1.1 | `GET /api/v1/places` (list) | ✅ | #5 / TZ §8.1 | bbox/near/category/attrs/cursor |
| 1.2 | `GET /api/v1/places/{id}` (детали) | ⏭ | TZ §8.2 | + `navigation_links` (Yandex/Google deeplinks) |
| 1.3 | `GET /api/v1/filters` | ⬜ | TZ §8.4 | Динамический список + min/max ranges |
| 1.4 | `GET /api/v1/route/places` | ⬜ | TZ §8.5 | Ломаная + PostGIS буфер, max waypoints=10 |
| 1.5 | OpenAPI/Swagger полное описание | ⬜ | REVIEW-BACKLOG P2 | Tags, examples, error codes |
| 1.6 | ETag для GET endpoints | ⬜ | REVIEW-BACKLOG P2 | CDN сможет 304 |

---

## Epic 2 — Parser (Python, Tier-1 sources) (⬜ todo)

§13 Этап 2. **Цель: 5K-50K мест в БД от authoritative источников.**

| # | Задача | Статус | Refs | Заметки |
|---|--------|--------|------|---------|
| 2.1 | Каркас Python проекта (pyproject.toml, ruff, pytest) | ⬜ | source/parser/ | |
| 2.2 | Dockerfile парсера (slim, OTel SDK включён) | ⬜ | TZ §6 | |
| 2.3 | Staging-схема в БД (раздел §6.4.1) | ⬜ | EF migration | `staging.places`, `staging.place_signals` |
| 2.4 | Source: Google Places API (`google_places_enrich`) | ⬜ | TZ §6.5, §11 | Polly-стиль retry + rate-limit |
| 2.5 | Source: Geofabrik OSM dump (`osm_import`) | ⬜ | TZ §6 | Weekly, osmium |
| 2.6 | Source: Wikidata SPARQL (`wikidata_enrich`) | ⬜ | TZ §6 | Weekly |
| 2.7 | Atomic merge `staging → public` (advisory_lock) | ⬜ | REVIEW-BACKLOG P1 | Закроет staging-merge race |
| 2.8 | Healthchecks.io ping after each job | ⬜ | TZ §10.6 | |

---

## Epic 3 — UGC API + moderation (⬜ todo)

§13 Этапы 3-4. **Цель: пользователь оставляет анонимный отзыв, LLM модерирует, signal попадает в `place_signals`.**

| # | Задача | Статус | Refs | Заметки |
|---|--------|--------|------|---------|
| 3.1 | EF migration: `place_signals`, `place_reviews`, `review_votes`, `processing_queue` | ⬜ | TZ §4.3-§4.5 | |
| 3.2 | `POST /api/v1/places/{id}/reports` + Cloudflare Turnstile | ⬜ | TZ §7.4 | Idempotency-Key header |
| 3.3 | `POST /api/v1/places/{id}/dispute` | ⬜ | TZ §8.7 | |
| 3.4 | `PUT /api/v1/reviews/{id}/vote` (helpful/unhelpful/flag) | ⬜ | TZ §7.6 | subnet_hash в дедупе (P1) |
| 3.5 | `POST /api/v1/aff/click` (вместо GET) | ⬜ | TZ §17.6 + REVIEW-BACKLOG P1 | |
| 3.6 | `try_reserve_gemini` + Gemini wrapper в API | ⬜ | TZ §6.2.1 | Уже atomic в спеке |
| 3.7 | `signals_aggregate` job: per-impact thresholds | ⬜ | TZ §5.2.2 + ADR-0008 | Critical/High/Medium/Low |
| 3.8 | `anomaly_detect` job: 24h + 7d patterns | ⬜ | TZ §5.3 + ADR-0007 | |
| 3.9 | `coordinated_value_flip` nightly + LLM-арбитраж | ⬜ | ADR-0007 | Fallback chain Gemini→Groq→Ollama |
| 3.10 | DAL: WHERE `hidden = false` во всех read-запросах | ⬜ | REVIEW-BACKLOG P1 | DMCA |

---

## Epic 4 — Frontend (⬜ todo)

§13 Этап 1 (часть) + §13 Этап 5. **Цель: интерактивная карта Грузии с фильтрами и UGC-формой.**

| # | Задача | Статус | Refs | Заметки |
|---|--------|--------|------|---------|
| 4.1 | ADR-0010: выбор фреймворка (vanilla / Vue / Svelte) | ⬜ | source/frontend/README.md | |
| 4.2 | MapLibre GL + MapTiler tiles | ⬜ | TZ §1, §11 | |
| 4.3 | Маркеры мест из `/api/v1/places` | ⬜ | TZ §8.1 | |
| 4.4 | Динамические фильтры из `/api/v1/filters` | ⬜ | TZ §8.4 | |
| 4.5 | Карточка места (`/api/v1/places/{id}`) с deeplinks | ⬜ | TZ §8.2 | |
| 4.6 | UGC форма + Cloudflare Turnstile | ⬜ | TZ §7.1, §7.4 | |
| 4.7 | A→B route picker | ⬜ | TZ §8.5 | + dislaimer о routing |
| 4.8 | Cloudflare Pages деплой | ⬜ | TZ §10 | |

---

## Epic 5 — Infra polish (🚧 wip)

Кросс-секционно. **Цель: всё в Docker, никаких host-side скриптов; observability стек в CI; чистый rollout.**

| # | Задача | Статус | Refs | Заметки |
|---|--------|--------|------|---------|
| 5.1 | Cron в контейнере (ofelia или alpine cron) | ⏭ | REVIEW-BACKLOG P1 | **Убирает host-cron — следующий PR**. Запускает: backup, verify-restore, refresh MV, signals_aggregate, anomaly_detect |
| 5.2 | Cloudflare Tunnel (для public access локального dev) | ✅ | этот PR | `tunnel` сервис в compose |
| 5.3 | TLS на Nginx (Let's Encrypt via certbot container) | ⬜ | TZ §10 | Только для prod |
| 5.4 | PgBouncer (transaction mode) | ⬜ | REVIEW-BACKLOG P1 | Защита connection pool |
| 5.5 | logrotate в контейнере для file-sink логов | ⬜ | REVIEW-BACKLOG P1 | После migration на ofelia — встроится |
| 5.6 | Партиционирование `place_signals` по месяцам | ⬜ | REVIEW-BACKLOG P1 | EF migration с DDL |
| 5.7 | gitleaks в CI вместо самопального grep | ⬜ | REVIEW-BACKLOG P1 | |
| 5.8 | Integration tests в CI (Docker on runner) | ⬜ | REVIEW-BACKLOG | services: postgres |
| 5.9 | Multi-arch образ (amd64 + arm64) | ⬜ | ADR-0009 | Если возьмём ARM VPS |

---

## Epic 6 — Observability hardening (⬜ todo)

После Epic 1. **Цель: dashboards + alerts действительно работают на проде.**

| # | Задача | Статус | Refs | Заметки |
|---|--------|--------|------|---------|
| 6.1 | Регистрация Grafana Cloud + Sentry, прописать `OTLP_ENDPOINT` + DSN | ⬜ | ADR-0006 | Owner manual step |
| 6.2 | Импортировать dashboards (export JSON в `docs/monitoring/`) | ⬜ | REVIEW-BACKLOG P1 | Vendor-portable |
| 6.3 | Alert rules в Grafana provisioning YAML | ⬜ | ADR-0006 | 6 P0 алертов |
| 6.4 | Smoke-тест: убить postgres → алерт пришёл за 5 мин | ⬜ | | |
| 6.5 | Cardinality budget audit | ⬜ | REVIEW-BACKLOG P2 | Запретить `place_id` как label |

---

## Epic 7 — DR + ops (⬜ todo)

После первого деплоя. **Цель: бэкап и restore проверены, runbook отработан.**

| # | Задача | Статус | Refs | Заметки |
|---|--------|--------|------|---------|
| 7.1 | Cloudflare R2 bucket + rclone.conf | ⬜ | TZ §10.5 | Owner manual step |
| 7.2 | `backup` job в ofelia → R2 | ⬜ | Epic 5.1 | |
| 7.3 | `verify-restore` job ежемесячно в ofelia | ⬜ | Epic 5.1 | Закрывает P0 «backup без verify» |
| 7.4 | DR runbook: «упал VPS → восстанавливаемся за 30 мин» | ⬜ | TZ §10.5 + ADR-0002 | Документ в `docs/tech/runbooks/` |

---

## Epic 8 — Production launch (⬜ todo)

После Epic 1-3 (read API + parser + UGC). **Цель: домен куплен, сервис live, первые 100 пользователей.**

| # | Задача | Статус | Refs | Заметки |
|---|--------|--------|------|---------|
| 8.1 | Купить VPS (Hetzner / Contabo) | ⬜ | TZ §1 | Owner manual step |
| 8.2 | Купить домен + Cloudflare orange-cloud | ⬜ | TZ §10 | |
| 8.3 | Init deploy: clone repo, fill .env+secrets, `docker compose pull && up -d` | ⬜ | README | |
| 8.4 | TLS issuance | ⬜ | Epic 5.3 | |
| 8.5 | Smoke checklist: все endpoints + observability видит трафик | ⬜ | TZ §13 | |

---

## Cross-cutting: REVIEW-BACKLOG

Residual P0/P1/P2 из 2 раундов авто-ревью — отдельный документ. Многие пункты автоматически закроются при выполнении эпиков выше; остальные — отдельные PR.

См. [REVIEW-BACKLOG.md](REVIEW-BACKLOG.md).

---

## Текущий фокус

**Сейчас (PR #7):** Epic 5.2 (tunnel) + этот backlog.

**Следующее:** Epic 5.1 — `ofelia` cron-контейнер. Это уберёт host-side bash-скрипты из деплоя (всё в Docker, как просил owner). После этого — Epic 1.2 (`GET /api/v1/places/{id}`).
