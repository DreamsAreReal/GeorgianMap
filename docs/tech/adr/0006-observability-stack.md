# ADR-0006: Observability stack

**Статус:** Accepted
**Дата:** 2026-04-30
**Автор:** owner
**Задача:** REVIEW-BACKLOG P0 (observability): отсутствует structured logging, correlation IDs, метрики latency, alerts на качество данных от Gemini. Без observability невозможно отладить инцидент за разумное время.

## Контекст

Текущий план в ТЗ (§10.6): UptimeRobot пинг `/health` каждые 5 мин + Healthchecks.io cron-пинги + Cloudflare Analytics (агрегаты). Это **liveness**, не observability.

Observability-ревьюер вынес вердикт: «не сможешь найти большинство проблем за 15 минут. Система слепа на всё кроме полного падения». Конкретно отсутствует:

1. Structured logs с уровнями и полями (только plain-text `>> /var/log/parser.log`)
2. CorrelationId через цепочку `cron → parser → DB → API → Cloudflare`
3. Latency-метрики API (p50/p95/p99 per endpoint)
4. Алерты на quality-degradation (Gemini тихо возвращает confidence < 0.6)
5. Per-channel productivity для парсера (3/10 каналов сломались — общий threshold не сработает)
6. Trace через `/api/v1/places/{id}` → DAL → Postgres

Ограничения:
- Бюджет инфраструктуры: $0/мес поверх $5-10 VPS (см. ADR-0001)
- Бюджет RAM на observability stack на VPS: ≤50 MB (см. ADR-0001 RAM map)
- Команда из 1 человека, 30 мин/мес обслуживания
- Один источник truth — иначе при инциденте время уходит на жонглирование вкладками

## Рассмотренные варианты

### Вариант A: Только локальные файлы + grep
- **Плюсы:** ноль внешних зависимостей, ноль RAM
- **Минусы:** при крэше VPS логи теряются, нет агрегации по нескольким сервисам, нет latency-percentilей, нет алертов
- **Сложность:** Low
- **Время на инцидент:** 1-3 часа (SSH + grep + ручная корреляция)

### Вариант B: Self-hosted ELK / Loki + Prometheus + Jaeger на этом же VPS
- **Плюсы:** полный контроль, никаких внешних зависимостей
- **Минусы:** ELK ~1 GB RAM, Loki + Promtail + Prometheus + Grafana ≥500 MB RAM — **физически не помещается** в 1 GB shared с Postgres + .NET API
- **Сложность:** High
- **Вердикт:** **отброшено** — нарушает RAM-бюджет

### Вариант C: Self-hosted на отдельном VPS
- **Плюсы:** изоляция, полный контроль
- **Минусы:** +$5-10/мес = удвоение инфра-бюджета; нужен мониторинг самого мониторинга
- **Сложность:** Medium
- **Вердикт:** **отброшено** — нарушает $0/мес бюджет

### Вариант D: Cloud free tier — несколько вендоров
- **Плюсы:** ноль RAM на VPS (push-only), 14+ day retention, готовые dashboards
- **Минусы:** vendor lock-in (но через OTLP — портативно), free tier может уменьшиться
- **Сложность:** Medium
- **Время на инцидент:** 5-15 минут (всё в одном UI)

## Решение

Выбран **Вариант D**, конкретный стек:

| Слой | Сервис | Free tier | Retention | RAM cost |
|------|--------|-----------|-----------|----------|
| Logs | **Grafana Cloud Loki** | 50 GB/мес | 14 дней | 0 (push через OTLP) |
| Metrics | **Grafana Cloud Prometheus** | 10K active series | 14 дней | 0 (push) |
| Traces | **Grafana Cloud Tempo** | 50 GB/мес | 14 дней | 0 (push) |
| Errors | **Sentry Free** | 5K errors + 50K performance events/мес | 30 дней | 0 (HTTP) |
| Cron | **Healthchecks.io** (уже в ТЗ) | 20 чеков | — | 0 |
| Synthetic | **UptimeRobot** (уже в ТЗ) | 50 мониторов, 5-мин интервал | — | 0 |

**Общий RAM cost на VPS:** ~30-40 MB (OpenTelemetry SDK в .NET) + ~5-10 MB (opentelemetry-sdk в Python). Укладывается в бюджет 50 MB.

### Транспорт: OpenTelemetry over OTLP HTTP

Все компоненты отправляют через OTLP HTTP (`https://otlp-gateway-prod-eu-west-2.grafana.net/otlp`) с auth через basic-auth (Grafana Cloud instance ID + API token). Никаких агентов на VPS — push-only.

**Почему OTel:**
- Один SDK для logs/metrics/traces в .NET и Python
- Backend-agnostic: завтра уйдём на Honeycomb / New Relic / self-hosted — меняется одна env var
- Стандарт CNCF, не вендорский SDK

### Backend-side stack

**.NET:**
- `Serilog` + `Serilog.Sinks.OpenTelemetry` → OTLP HTTP → Loki
- `OpenTelemetry.Extensions.Hosting` + `OpenTelemetry.Instrumentation.AspNetCore` + `OpenTelemetry.Instrumentation.Http` + `OpenTelemetry.Instrumentation.EntityFrameworkCore` → метрики и трейсы → OTLP → Prometheus + Tempo
- `Sentry.AspNetCore` → ошибки → Sentry HTTP

**Python parser:**
- `structlog` → JSON → stdout → push через `opentelemetry-sdk` после batch завершения job
- `opentelemetry-instrumentation-httpx` + `opentelemetry-instrumentation-psycopg` → трейсы Gemini/Google Places/DB calls
- `sentry-sdk` → ошибки → Sentry HTTP

### Correlation ID

**ASP.NET middleware:**

```csharp
app.Use(async (ctx, next) =>
{
    var corrId = ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault()
                 ?? ctx.TraceIdentifier;
    ctx.Response.Headers["X-Correlation-Id"] = corrId;
    using (LogContext.PushProperty("CorrelationId", corrId))
        await next();
});
```

OpenTelemetry автоматически распространяет `traceparent` (W3C Trace Context), `X-Correlation-Id` дублирует это для не-OTel клиентов и логов.

**Python parser:** на старте каждого job-run генерируется UUID, проставляется как `correlation_id` во все log-записи через `structlog.contextvars.bind_contextvars()`. UUID попадает в trace span как attribute.

**Cron-уровень:** Healthchecks ping URL содержит `?ping_id=<uuid>` — этот UUID становится `correlation_id` всего run.

### Алерты (через Grafana Cloud Alerting, free)

P0 алерты — на email + Telegram через Cloudflare Email Workers webhook:

1. `up{job="api"} == 0 for 2m` — API down
2. `histogram_quantile(0.95, http_request_duration_seconds) > 0.5 for 5m` — p95 > 500ms
3. `rate(http_requests_total{status=~"5.."}[5m]) > 0.05` — 5xx > 5%
4. `gemini_low_confidence_discarded_total / gemini_calls_total > 0.4 for 1h` — Gemini тихо деградирует
5. `time() - max(parser_last_success_timestamp) > 90000` — парсер не успешен >25h
6. `processing_queue_size{status="failed"} > 100` — очередь failed копится

P1 алерты — только email:

7. `dotnet_gc_heap_size_bytes / dotnet_gc_heap_hard_limit_bytes > 0.9` — heap почти исчерпан
8. `pg_database_size_bytes > 0.8 * disk_capacity` — диск 80%
9. `parser_channel_items_total{channel=~".+"} == 0 for 3 runs` — конкретный канал не даёт постов

## Бизнес-метрики (помимо технических)

Парсер и API экспортируют:

- `places_count_total` — общее количество мест
- `places_added_24h` — новые места за 24h
- `signals_aggregated_24h{tier="1|2"}`
- `ugc_published_total`, `ugc_hidden_total{reason="spam|toxic|...}`
- `gemini_calls_total{category="extraction|classification|arbitration"}`
- `gemini_low_confidence_discarded_total`
- `affiliate_clicks_total{partner="booking|...}`

Это обеспечивает не только debugging, но и продуктовую аналитику без отдельного тула.

## Last-resort fallback

Если Grafana Cloud free tier исчезнет или урежется ниже наших объёмов:

1. Логи продолжают писаться в `/var/log/api.log`, `/var/log/parser.log` (файловый sink Serilog как backup) с logrotate (см. REVIEW-BACKLOG P1).
2. Sentry заменяется на GlitchTip (self-host, не наш вариант) или просто Sentry → Highlight.io / Bugsnag free.
3. OTel endpoint меняется в env — никаких изменений в коде.

## Последствия

- **Положительные:** инцидент-response 5-15 мин (один UI), постоянная видимость quality-метрик Gemini, alerts на бизнес-метриках, не только инфра.
- **Отрицательные:**
  - Зависимость от free tier — может урезаться
  - +30-40 MB RAM на VPS (укладывается в бюджет)
  - Регистрация в 2 новых сервисах (Grafana Cloud, Sentry)
- **Миграция:** обновить ТЗ §10.6 (раздел мониторинга), добавить env vars `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS`, `SENTRY_DSN` в README.
- **Мониторинг самого мониторинга:** UptimeRobot пингует Grafana Cloud (если нет доступа — алерт), Sentry имеет own status page. Если оба легли — Healthchecks.io продолжает мониторить cron, и `/var/log/*.log` остаются на VPS.
- **Rollback plan:** убрать OTel exporter, оставить только файловые логи. Один день работы.

## Триггеры пересмотра

- Free tier Grafana Cloud меняется → пересмотр backend (Honeycomb даёт 20M events/мес — больше чем Grafana по traces)
- Sentry free 5K errors исчерпывается → платный или замена
- При scale > 100 RPS нужен Prometheus с большим количеством series (>10K) — возможен переход на Honeycomb или платный Grafana

## Связанные ADR

- ADR-0001 (Monolith on single VPS) — определяет RAM бюджет
- ADR-0003 (Server GC with heap limit) — экспортируем heap-метрики

## Дополнения к README

В таблицу env vars добавить:

| `OTEL_EXPORTER_OTLP_ENDPOINT` | yes | — | OTLP HTTP endpoint Grafana Cloud |
| `OTEL_EXPORTER_OTLP_HEADERS_FILE` | yes | `/run/secrets/otlp_headers` | Путь к файлу с `Authorization=Basic <base64(...)>` (см. ниже) |
| `OTEL_SERVICE_NAME` | yes | — | `georgia-places-api` или `georgia-places-parser` |
| `SENTRY_DSN` | yes | `/run/secrets/sentry_dsn` (через `SENTRY_DSN_FILE`) | Sentry DSN — также через secret |

### Почему НЕ env-переменная для credentials

`OTEL_EXPORTER_OTLP_HEADERS` содержит base64 Grafana Cloud token. Если положить его в обычную env-переменную:
- `docker compose config` дампит весь конфиг в stdout — токен попадает в CI логи.
- `docker inspect <container>` показывает env — доступно любому с доступом к Docker socket.
- Crash dump / `dotnet-trace` могут включать env.
- Сам OTel SDK в debug-уровне может залогировать env при старте — credential попадёт в Loki, защищаемый этим же токеном.

**Решение:** Docker secrets (или mount файла с правами 0400).

```yaml
# docker-compose.yml
services:
  api:
    secrets:
      - otlp_headers
      - sentry_dsn
    environment:
      OTEL_EXPORTER_OTLP_HEADERS_FILE: /run/secrets/otlp_headers
      SENTRY_DSN_FILE: /run/secrets/sentry_dsn

secrets:
  otlp_headers:
    file: ./secrets/otlp_headers.txt   # 0400, в .gitignore
  sentry_dsn:
    file: ./secrets/sentry_dsn.txt
```

Файл `secrets/otlp_headers.txt` содержит ровно одну строку:
```
Authorization=Basic <base64(instanceId:token)>
```

OpenTelemetry SDK ≥1.7 читает `*_HEADERS_FILE` нативно. Sentry SDK — через лёгкий `Sentry.Init(o => o.Dsn = File.ReadAllText(Environment.GetEnvironmentVariable("SENTRY_DSN_FILE")).Trim())`.

`.gitignore` уже исключает `secrets/` (см. PR #1).
