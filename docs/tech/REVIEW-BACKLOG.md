# Review Backlog

Сводка findings из стартового ревью (commit `96ae41c`, 13 ревьюеров параллельно). Что закрыто на месте — отмечено `[x]`. Что закрыто через ADR — ссылка на ADR. Остальное — задачи на ближайшие коммиты.

**Последнее обновление:** 2026-04-30 (после PR #1 + ADR 0006-0008)

---

## P0 — блокеры до production

### Закрыто

- [x] **API versioning отсутствует** (api-design) → ADR-0004 + патч ТЗ §8.0
- [x] **RPO/RTO не зафиксированы** (database) → ADR-0002 + патч ТЗ §10.5
- [x] **Workstation GC vs Server GC не обоснован** (performance, memory) → ADR-0003 + патч ТЗ §2.2, §2.3
- [x] **§6.2.1: race condition в `can_call_gemini`** (resilience, business-logic) → атомарный `try_reserve_gemini` в ТЗ
- [x] **§8: нет единого формата ошибок (RFC 7807 ProblemDetails)** (api-design) → ТЗ §8.0
- [x] **§19.16 DMCA ссылается на `places.hidden`** (business-logic, database) → ТЗ §4.1 (колонки + partial-индексы)
- [x] **observability blind spots** (observability) → **ADR-0006** + патч ТЗ §10.6
- [x] **§5.2.2: одиночный Tier-2 сигнал перезаписывает атрибуты** (business-logic) → **ADR-0008** + патч ТЗ §5.2.2
- [x] **§5.3: brigading 4 сигнала/день × 5 дней не детектируется** (business-logic) → **ADR-0007** + патч ТЗ §5.3
- [x] **§10.5: backup без verify-restore, нет `pg_dump --format=custom`** (database, resilience) → ТЗ §10.5 (restore.sh, verify-restore.sh)
- [x] **§12.2 materialized view `places_summary` без DDL и без `REFRESH ... CONCURRENTLY`** (performance, database) → ТЗ §12.2 (полный DDL + advisory lock)

### Требуют правки ТЗ

- [ ] **§6.6, §7.4: нет timeout/retry/circuit breaker на Google Places, Cloudflare Turnstile** (resilience). Зависший вызов блокирует UGC. Фикс: явно прописать `httpx.get(..., timeout=10)` в псевдокоде, упомянуть Polly circuit breaker для Turnstile, fallback при CB open.
- [ ] **§1361 compose: connection string inline, нет `env_file: .env`** (infra). Риск утечки при `docker compose config`.

---

## P1 — технический долг до MVP

### Закрыто

- [x] `.gitignore`: добавлены `*.pem *.key *.crt *.p12 *.pfx certs/ rclone.conf coverage/ TestResults/`
- [x] `README.md`: расширен (Prerequisites, Quick Start, env vars, назначения папок)
- [x] **§12.2 materialized view `places_summary` без DDL и без `REFRESH ... CONCURRENTLY`** → §12.2 полный DDL
- [x] **§12.3: нет stampede protection для `IMemoryCache`** → §12.2 SemaphoreSlim pattern в ТЗ

### Требуют правки ТЗ

- [ ] **§12.2 connection pool 10+5=15 при `max_connections=20`** (performance, database). Фикс: либо PgBouncer transaction pool, либо `max_connections=30` + пересчёт `shared_buffers`.
- [ ] **§6.3 hourly cron `signals_aggregate` + `anomaly_detect` без overlap protection и пересекаются по CPU с daily-парсером** (architecture, resilience). Фикс: `pg_try_advisory_lock` в начале job, перенести hourly на 02:30/07:05 чтобы не пересекаться с 03:00 daily.
- [ ] **§6.4.1 staging merge не атомарен** (business-logic). `signals_aggregate` может увидеть сигналы без `places`. Фикс: либо merge в одной транзакции, либо advisory lock на `signals_aggregate` пока идёт merge.
- [ ] **§13 vs §1.5: противоречие про OSRM** (business-logic, docs). §1.5 говорит «нет routing engine», §13.5 предлагает OSRM. Фикс: убрать упоминание OSRM из §13.5 или зафиксировать как future work.
- [ ] **§1344-1396 compose: `depends_on` без `condition: service_healthy`, `restart: unless-stopped` без `max_attempts`** (infra). Фикс: добавить healthcheck postgres + `condition: service_healthy`, `restart: on-failure`, `max_attempts: 5`.
- [ ] **§1402-1405 нет logrotate для `/var/log/parser.log`** (infra). Фикс: добавить logrotate config.
- [ ] **§10.6 UptimeRobot — только liveness, не latency** (observability). Фикс: добавить latency probe через стороннюю синтетику или Cloudflare RUM.
- [ ] **§20.6 `/internal/health` обновляется раз в час** (observability). Задержка обнаружения инцидента до 60 мин. Фикс: критичные метрики (parser_last_success, queue_failed_count) обновлять каждые 5 минут.
- [ ] **§8 API: cursor-based pagination, `limit` cap, GET вместо POST для read** (api-design, performance). Фикс: cursor pagination, max `limit=100`, переписать `/route/places` на GET.
- [ ] **§8 API: Idempotency-Key на UGC POST** (api-design, business-logic). Фикс: принимать `Idempotency-Key: <uuid>` header, deduplicate 24h.
- [ ] **§17.6 GET для side-effect (запись `affiliate_clicks`)** (api-design). Фикс: `POST /api/v1/aff/click` вместо `GET /api/aff/redirect`.
- [ ] **§7.6 helpful_count абуз через ротацию устройств** (business-logic). Фикс: `subnet_hash` в дедупе голосов.
- [ ] **§19.16 DMCA ссылается на `places.hidden`, в схеме §4.1 такой колонки нет** (business-logic, database). Фикс: добавить `hidden BOOLEAN DEFAULT false` + индекс + WHERE-условие в API-запросах.
- [ ] **§4.3 `place_signals` без партиционирования — DELETE 90+ дней создаёт bloat** (database). Фикс: партиционирование по месяцам, `DROP TABLE` вместо DELETE.
- [ ] **§1463 `pg_dump` без явного `PGPASSWORD`** (infra, security). Фикс: документировать использование `.pgpass` или `PGPASSWORD=${DB_PASSWORD}`.

---

## P2 — улучшения

- [ ] §16: 6 открытых вопросов без трекинга (docs)
- [ ] §8: error codes 400/429/503 не описаны для большинства endpoints (docs, api-design)
- [ ] §10: нет примера `docker-compose.yml` в деплой-секции (docs, infra)
- [ ] §17.6: `affiliate_clicks` синхронный INSERT на каждый клик — async batch (performance)
- [ ] §10.1: `effective_cache_size=384MB` завышен для 1GB shared (database)
- [ ] §17.6: `affiliate_clicks` без TTL (database)
- [ ] §6.6: `fetch_and_cache_photos` без timeout/error handling на R2 upload (resilience)
- [ ] §19.21: per-channel productivity metric отсутствует — silent failure 3/10 каналов (observability)
- [ ] §20.7: Groq/Ollama fallback не настраивается заранее (resilience)
- [ ] нет PgBouncer (database)
- [ ] §8: ETag/If-None-Match не специфицированы → CDN игнорирует условные запросы (api-design)
- [ ] §20.2: при недоступном Gemini UGC автопубликуется через 24ч → окно для спама без LLM (business-logic)

---

## Метаданные

- Использованы агенты: code-reviewer, security-reviewer, architecture-reviewer, performance-reviewer, resilience-reviewer, business-logic-reviewer, testing-reviewer, docs-reviewer, memory-reviewer, database-reviewer, infra-reviewer, observability-reviewer, api-design-reviewer
- Полная сводка ревью — в чате коммита (не сохранена в репо целиком, чтобы не дублировать).
