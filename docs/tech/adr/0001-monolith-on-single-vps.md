# ADR-0001: Monolith on single VPS

**Статус:** Accepted
**Дата:** 2026-04-30
**Автор:** owner
**Задача:** initial architecture review (review of commit 96ae41c)

## Контекст

Georgia Places — бесплатный сервис без аккаунтов, бюджет $5-10/мес на инфраструктуру (см. §1.2 ТЗ). Целевая нагрузка: ~50 RPS peak, ~10-15 RPS cold cache (см. §1.4). Объём данных: ~50K мест, ~500K UGC-сигналов.

Команда — один человек, ~30 минут/мес обслуживания. Микросервисы или multi-VPS добавляют операционную сложность, не дающую выигрыша при таком scale.

## Рассмотренные варианты

### Вариант A: Микросервисная архитектура (API + parser + worker как отдельные деплои)
- **Плюсы:** независимый scale, изоляция отказов
- **Минусы:** 3-4× стоимость VPS, distributed tracing обязателен, координация деплоев
- **Поведение под нагрузкой:** избыточно для 50 RPS
- **Отказоустойчивость:** выше, но за счёт сложности
- **Сложность внедрения:** High

### Вариант B: Monolith на одном VPS, разные процессы (API, cron-parser, hourly worker)
- **Плюсы:** минимум операций, одна точка деплоя, всё в одном compose
- **Минусы:** SPOF на VPS, конкуренция за CPU/RAM при overlap parser+API
- **Поведение под нагрузкой:** Cloudflare кэширует 90%+, origin держит 10-15 RPS cold cache на 1 vCPU
- **Отказоустойчивость:** RPO=24h через R2-бэкапы (см. ADR-0002), Cloudflare Always Online как DR-уровень для статики
- **Сложность внедрения:** Low

### Вариант C: Multi-VPS (API на одном, БД на другом, parser на третьем)
- **Плюсы:** изоляция БД, parser не конкурирует за RAM
- **Минусы:** 3× стоимость, сетевая latency между сервисами, managed БД ещё дороже
- **Сложность внедрения:** Medium

## Решение

Выбран **Вариант B** (monolith на одном VPS) потому что:

1. Целевая нагрузка 50 RPS peak с 90% Cloudflare-cache → origin реально получает <5 RPS — 1 vCPU справляется.
2. Бюджет $5-10/мес исключает варианты A и C.
3. Команда из одного человека: каждый дополнительный сервис = +5-10 мин/мес обслуживания.
4. Граница декомпозиции внутри monolith уже есть: parser — отдельный процесс с profile=manual в compose, hourly worker — отдельный cron, API — long-running. Будущий вынос в отдельные хосты не блокируется.

## Последствия

- **Положительные:** низкие затраты, простой деплой (`docker compose up -d`), один runbook.
- **Отрицательные:** SPOF на VPS, cron-parser и hourly worker конкурируют за CPU с API в моменты пересечения (см. P1 в REVIEW-BACKLOG).
- **Миграция:** N/A — стартовый выбор.
- **Мониторинг:** UptimeRobot пингует `/health`, Healthchecks.io ловит cron-failures. Алерт на CPU >80% и RAM >90% постоянно — повод смотреть в сторону upgrade VPS.
- **Rollback plan:** N/A.

## Триггер пересмотра

Пересмотреть, если:
- Cloudflare-кэш hit ratio < 80% при peak трафике
- p95 latency на origin > 500ms при upgrade VPS до 2 vCPU / 2 GB
- RAM usage > 85% устойчиво в течение недели

## RAM бюджет

> **Update 2026-05-01:** пересчитано после ADR-0003 (Server GC overhead) и ADR-0006 (OpenTelemetry SDK overhead). Это **единый источник правды** по RAM-распределению — все docker-compose `mem_limit` должны соответствовать таблице ниже.

| Компонент | mem_limit | Из чего складывается |
|-----------|-----------|----------------------|
| Ubuntu система | ~150 MB | базовый minimal Ubuntu Server |
| Postgres | 250 MB | `shared_buffers=128MB`, `work_mem=4MB`, `max_connections=20` |
| .NET API | **260 MB** | 200 MB `GCHeapHardLimit` (ADR-0003) + ~30 MB Server GC overhead + ~30 MB OTel SDK + Sentry SDK (ADR-0006) |
| Nginx | 20 MB | alpine reverse proxy |
| **Парсер** (профиль `manual`) | **320 MB** | ~300 MB Python runtime + httpx + bs4 + psycopg3 + ~10 MB Python OTel SDK (ADR-0006) |

**Pessimistic-сумма** (ядро без парсера, обычное окно): `150 + 250 + 260 + 20 = 680 MB`. Запас ~320 MB — поглощается page cache Postgres-а (что улучшает производительность read-запросов).

**Pessimistic-сумма** в окне overlap (03:00-06:00, парсер активен, API в idle ~30 MB): `150 + 250 + 30 + 20 + 320 = 770 MB`. Запас ~230 MB.

**Своп** 2 GB обязательно (`/swapfile`, `swappiness=10`) — страховка для GC-пиков и pg autovacuum.

**Disk бюджет** (типичный VPS 20-25 GB): Postgres data ~3-5 GB при 50K places + 500K signals, WAL ~500 MB, materialized view temp при `REFRESH CONCURRENTLY` ~100-200 MB peak, логи (с logrotate) ~1 GB, backups локальные не храним (R2). Итого ~5-7 GB. Запас ~15 GB.

**Триггеры пересмотра RAM-map:**
- Любой новый ADR, добавляющий процесс/SDK на VPS, обязан обновить эту таблицу
- При устойчивом RSS API > 240 MB или Postgres > 270 MB — алерт, пересмотр

## Связанные ADR

- ADR-0002 (RPO=24h, no WAL archiving) — следствие single-VPS выбора
- ADR-0003 (Server GC with heap limit) — настройка под shared 1 GB RAM
- ADR-0006 (Observability stack) — добавляет OTel SDK overhead в RAM-map
