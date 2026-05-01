# Review Backlog

Сводка findings из 1-го (commit `96ae41c`) и 2-го (`698bab9`) авто-ревью раундов. 13 ревьюеров параллельно. Что закрыто на месте — `[x]`. Что закрыто через ADR — ссылка на ADR. Остальное — задачи на ближайшие коммиты.

**Последнее обновление:** 2026-05-01 (после PR #3 — fix/p0-from-2nd-review)

---

## P0 — блокеры до production

### Закрыто (1-й раунд — PR #1, PR #2)

- [x] **API versioning отсутствует** (api-design) → ADR-0004 + патч ТЗ §8.0
- [x] **RPO/RTO не зафиксированы** (database) → ADR-0002 + патч ТЗ §10.5
- [x] **Workstation GC vs Server GC не обоснован** (performance, memory) → ADR-0003 + патч ТЗ §2.2, §2.3
- [x] **§6.2.1: race condition в `can_call_gemini`** (resilience, business-logic) → атомарный `try_reserve_gemini` (1-й раунд) + правильная транзакционность (2-й раунд, PR #3)
- [x] **§8: нет единого формата ошибок (RFC 7807 ProblemDetails)** (api-design) → ТЗ §8.0
- [x] **§19.16 DMCA ссылается на `places.hidden`** (business-logic, database) → ТЗ §4.1 (колонки + partial-индексы)
- [x] **observability blind spots** (observability) → **ADR-0006** + патч ТЗ §10.6
- [x] **§5.2.2: одиночный Tier-2 сигнал перезаписывает атрибуты** (business-logic) → **ADR-0008** + патч ТЗ §5.2.2
- [x] **§5.3: brigading 4 сигнала/день × 5 дней не детектируется** (business-logic) → **ADR-0007** + патч ТЗ §5.3
- [x] **§10.5: backup без verify-restore, нет `pg_dump --format=custom`** (database, resilience) → ТЗ §10.5 (restore.sh, verify-restore.sh)
- [x] **§12.2 materialized view `places_summary` без DDL и без `REFRESH ... CONCURRENTLY`** (performance, database) → ТЗ §12.2 (полный DDL + advisory lock)

### Закрыто (2-й раунд — PR #3)

- [x] **§12.2 MV `places_summary` ссылается на несуществующие `p.lat`/`p.lng`** (database) → переписано на `ST_Y/ST_X(geom::geometry)`
- [x] **ADR-0007 SQL патт. 4 и 6 используют удалённую `ip_address`** (database) → переписано на `subnet_hash` напрямую + новый составной индекс
- [x] **`try_reserve_gemini` ручной rollback вне транзакции** (code, resilience) → переписано на единый CTE с ROLLBACK через исключение
- [x] **`coordinated_value_flip` коррелированный subquery O(n²)** (performance) → переписано через `RANGE BETWEEN ... PRECEDING AND FOLLOWING` window function + вынесено в nightly job
- [x] **Python parser OTel +10MB → OOM при `mem_limit:300m`** (memory, infra) → ТЗ §2.3 + ADR-0001 RAM map: `mem_limit: 320m`
- [x] **Алерт `gemini_low_confidence_discarded_total` без instrument-кода в TZ** (observability) → ТЗ §6.5 (extraction): `metrics.increment(...)` в ветке `confidence < 0.6`. Также добавлен `parser_channel_items_total` в `fetch_channel_incremental`.
- [x] **ADR-0001 RAM map не пересчитан после ADR-0003 + ADR-0006** (architecture) → ADR-0001 §RAM бюджет — единый источник правды; API `mem_limit: 230m → 260m`; ADR-0003 синхронизирован
- [x] **`OTEL_EXPORTER_OTLP_HEADERS` token в env → утечка через `docker compose config`** (security, infra) → ADR-0006 + README: `OTEL_EXPORTER_OTLP_HEADERS_FILE` через Docker secret; `secrets/` в .gitignore

### Требуют правки ТЗ

- [ ] **§6.6, §7.4: нет timeout/retry/circuit breaker на Google Places, Cloudflare Turnstile** (resilience). Фикс при имплементации: `httpx.AsyncClient(timeout=10)` в парсере, Polly `AddTransientHttpErrorPolicy + circuit breaker` в .NET API, fallback при CB open для Turnstile (skip with logging).
- [ ] **§1361 compose: connection string inline, нет `env_file: .env`** (infra). Закрывается при создании реального `source/infra/docker-compose.yml`.

---

## P1 — технический долг до MVP

### Закрыто (1-й раунд — PR #1, PR #2)

- [x] `.gitignore`: добавлены `*.pem *.key *.crt *.p12 *.pfx certs/ rclone.conf coverage/ TestResults/`
- [x] `README.md`: расширен (Prerequisites, Quick Start, env vars, назначения папок)
- [x] **§12.2 materialized view `places_summary` без DDL и без `REFRESH ... CONCURRENTLY`** → §12.2 полный DDL
- [x] **§12.3: нет stampede protection для `IMemoryCache`** → §12.2 SemaphoreSlim pattern в ТЗ

### Закрыто (2-й раунд — PR #3)

- [x] **README workflow рассинхрон с ADR-0005** → переписано на trunk-based + feature branches
- [x] **CI триггерится только на `develop`, feature branches без CI** → `feat/**`, `fix/**`, `main` в push-триггерах
- [x] **OSRM противоречие §1.5 vs §13/Этап 5** — оба упоминания содержат явный отказ с обоснованием (§8.5 + §13/Этап 5 после PR #1). Backlog-пункт закрыт без отдельной правки.
- [x] **DMCA дубль в backlog (P0+P1)** — разделён: P0 (схема `places.hidden`) закрыт; в P1 остаётся только WHERE-клауза в API DAL — закроется при имплементации.
- [x] **`source/frontend/README.md` ссылается на несуществующий ADR** — заменено на «решение до Этапа 3».

### Требуют правки ТЗ или имплементации

- [ ] **§12.2 connection pool 10+5=15 при `max_connections=20`** (performance, database). Фикс: PgBouncer в transaction mode.
- [ ] **§6.3 hourly cron `signals_aggregate` + `anomaly_detect` без overlap protection** (architecture, resilience). Фикс: `pg_try_advisory_lock` + перенести hourly на 02:30/04:30/06:30… (см. ADR-0007: nightly уже на 04:30).
- [ ] **§6.4.1 staging merge не атомарен** (business-logic). Фикс: merge в одной транзакции с advisory lock на `signals_aggregate`.
- [ ] **§1344-1396 compose: `depends_on` без `condition: service_healthy`, `restart: unless-stopped` без `max_attempts`** (infra). Закрывается при создании реального compose.
- [ ] **§1402-1405 нет logrotate для логов** (infra). Закрывается при создании реального compose.
- [ ] **§10.6 UptimeRobot — только liveness, не latency** (observability). Фикс: latency probe через стороннюю синтетику или Cloudflare Workers Analytics.
- [ ] **§20.6 `/internal/health` обновляется раз в час** (observability). Фикс: критичные метрики (parser_last_success, queue_failed_count) — каждые 5 минут.
- [ ] **§17.6 GET для side-effect (запись `affiliate_clicks`)** (api-design). Фикс: `POST /api/v1/aff/click`.
- [ ] **§7.6 helpful_count абуз через ротацию устройств** (business-logic). Фикс: `subnet_hash` в дедупе голосов.
- [ ] **DAL: WHERE `hidden = false` во всех read-запросах** (business-logic, database). При имплементации.
- [ ] **§4.3 `place_signals` без партиционирования** (database). Фикс: партиционирование по месяцам, `DROP TABLE` вместо DELETE.
- [ ] **§1463 `pg_dump` без явного `PGPASSWORD`** (infra, security). Фикс: документировать `.pgpass` или `PGPASSWORD=${DB_PASSWORD}` с secret.
- [ ] **OTel SDK без bounded queue** (resilience). Фикс при имплементации: явные `MaxQueueSize=2048`, `MaxExportBatchSize=512` в OTel конфиге.
- [ ] **LLM-арбитраж по `coordinated_value_flip` без 48h timeout fallback** (resilience). ADR-0007 описывает идею, нужна имплементация.
- [ ] **Split-flip атака (5 за `is_open=true` + 5 за `is_open=false`) проходит независимо** (business-logic). Фикс: дополнительный паттерн в nightly job — count signals per (place_id, attribute_key) без value-группировки, alert если ≥10 в 7d при ≥3 разных значениях.
- [ ] **Critical 90-day staleness сбрасывает значение в null** (business-logic). Фикс: при истечении окна оставлять старое значение с `stale=true`, блокировать обновление до нового консенсуса.
- [ ] **Provisional Medium сбрасывается через 7 дней без противоречия** (business-logic). Фикс: provisional-сброс только при противоречащем сигнале.
- [ ] **Соль для subnet_hash без 30-day overlap при ротации** (security, business-logic). Фикс: 2 активных соли в перекрытии.
- [ ] **Advisory lock константы выбраны произвольно — нужен реестр** (database). Завести `docs/tech/advisory-lock-registry.md`.
- [ ] **Partial indexes по `hidden=false` создают seq scan на recompute jobs** (database). Фикс: добавить полный btree индекс на `id`.
- [ ] **secret-grep в CI без `*.sh`/`*.md`, нет gitleaks** (infra, security). Фикс: подключить `zricethezav/gitleaks-action` с `.gitleaks.toml`.
- [ ] **`source/infra/scripts/verify-restore.sh` упомянут в ТЗ, отсутствует в дереве** (infra). Закрывается при создании реальных скриптов.
- [ ] **Нет `source/infra/.env.example`** (infra). Quick Start в README падает.
- [ ] **`verify-restore.sh` без `DROP DATABASE IF EXISTS` перед CREATE** (resilience). При создании скрипта.
- [ ] **`retryAfter` в теле ProblemDetails дублирует HTTP header** (api-design). Фикс: убрать из тела, оставить только header `Retry-After`.
- [ ] **`/api/v1/route/places` нет `maxWaypoints` лимита** (api-design, performance). Фикс: max=10 + 400 ProblemDetails при превышении.
- [ ] **Idempotency-Key — поведение при коллизии после TTL не определено** (api-design). Фикс: одно предложение в §8.0.
- [ ] **`X-Correlation-Id` без CORS exposed** (api-design). Фикс: `Access-Control-Expose-Headers: X-Correlation-Id`.
- [ ] **MV refresh без performance regression gate** (testing). Фикс: integration test, alert при >45s.
- [ ] **Vendor lock-in Grafana Cloud — dashboards/alerts вне git** (architecture). Фикс: экспортировать в `docs/monitoring/` JSON.
- [ ] **`develop` ветка остаётся в репо без protection** (architecture, infra). Фикс: либо удалить, либо защитить ruleset-ом.

---

## P2 — улучшения

- [ ] §16: 6 открытых вопросов без трекинга (docs)
- [ ] §8: error codes 400/429/503 не описаны для большинства endpoints (docs, api-design)
- [ ] §10: нет примера `docker-compose.yml` в деплой-секции (docs, infra)
- [ ] §17.6: `affiliate_clicks` синхронный INSERT на каждый клик — async batch (performance)
- [ ] §10.1: `effective_cache_size=384MB` завышен для 1GB shared (database)
- [ ] §17.6: `affiliate_clicks` без TTL (database)
- [ ] §6.6: `fetch_and_cache_photos` без timeout/error handling на R2 upload (resilience)
- [ ] §20.7: Groq/Ollama fallback не настраивается заранее (resilience)
- [ ] §8: ETag/If-None-Match не специфицированы → CDN игнорирует условные запросы (api-design)
- [ ] §20.2: при недоступном Gemini UGC автопубликуется через 24ч → окно для спама без LLM (business-logic)
- [ ] ADR-0006: cardinality бюджет — добавить explicit правило «никаких high-cardinality labels (`place_id`, `user_session`)»
- [ ] ADR-0006: calendar-based refresh policy для free-tier лимитов (раз в 6 мес)
- [ ] ADR-0008: разрешение конфликта `entrance_fee` vs `price_gel` — одна строка в §Дополнительные правила
- [ ] ADR-0006: ссылка «см. ADR-0001 RAM map» — добавить anchor `#ram-бюджет` в ADR-0001 (сейчас ссылка нарративная)
- [ ] CI lint-docs: `test -f` → `markdownlint-cli` или `pymarkdown`
- [ ] OTLP egress на дешёвом VPS — задокументировать batch-интервалы (`OTEL_BSP_SCHEDULE_DELAY=30000`, `OTEL_METRIC_EXPORT_INTERVAL=60000`)

---

## Метаданные

- Использованы агенты: code-reviewer, security-reviewer, architecture-reviewer, performance-reviewer, resilience-reviewer, business-logic-reviewer, testing-reviewer, docs-reviewer, memory-reviewer, database-reviewer, infra-reviewer, observability-reviewer, api-design-reviewer
- 1-й раунд: commit `96ae41c` (initial structure)
- 2-й раунд: range `96ae41c..698bab9` (после PR #1 + PR #2)
- Полные сводки ревью — в истории сессий, не сохранены в репо целиком.
