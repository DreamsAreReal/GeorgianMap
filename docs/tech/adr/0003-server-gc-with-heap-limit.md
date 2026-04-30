# ADR-0003: Server GC with hard heap limit

**Статус:** Accepted
**Дата:** 2026-04-30
**Автор:** owner
**Задача:** review finding P0 (performance): "Workstation GC на throughput-сервисе"

## Контекст

ТЗ §2.2 указывает Workstation GC «для экономии памяти» на 1 GB VPS, который шарится между Postgres + .NET API + Nginx + редко-cron-парсером. Performance-ревью отмечает: Workstation GC использует одну кучу и блокирует приложение во время GC pause (50-100ms на пиках), Server GC работает конкурентно.

API ожидает 10-15 RPS cold cache на origin — длительные GC pauses напрямую бьют по p95 latency.

Бюджет RAM по ТЗ §2.3:
- Postgres: 350 MB (`mem_limit: 350m`)
- .NET API: 200 MB
- Парсер (only при `manual` profile): 300 MB
- Nginx + ОС: 150 MB
- Запас: ~50 MB

## Рассмотренные варианты

### Вариант A: Workstation GC, без явного heap limit
- **Плюсы:** меньше начальный RSS (~20-40 MB), один thread на GC
- **Минусы:** stop-the-world паузы, неконкурентный sweep, удар по p95
- **Поведение под нагрузкой:** при 15 RPS пиках — видимые паузы 50-100ms раз в несколько секунд
- **Сложность внедрения:** Low (текущий вариант ТЗ)

### Вариант B: Server GC с `DOTNET_GCHeapHardLimit` (200 MB)
- **Плюсы:** конкурентный mark/sweep (фоновый thread), меньше pauses в hot path; жёсткий ceiling вместо неожиданного OOM-kill контейнера
- **Минусы:** +20-40 MB на старте; требует тюнинга `GCHeapHardLimit` под бюджет 200 MB
- **Поведение под нагрузкой:** p95 latency предсказуемее, GC паузы короче (но чаще)
- **Сложность внедрения:** Low (одна env var, один runtimeconfig)

### Вариант C: Server GC без heap limit, полагаемся на `mem_limit: 200m` Docker
- **Плюсы:** простота
- **Минусы:** при превышении лимита Docker делает OOM-kill — рестарт API без graceful shutdown, потеря in-flight запросов
- **Сложность внедрения:** Low, но опасно

## Решение

Выбран **Вариант B** (Server GC с `DOTNET_GCHeapHardLimit=200000000`) потому что:

1. Throughput-приложение с целью p95 < 500ms (см. ТЗ §12.1) не может позволить себе 50-100ms блокирующих пауз.
2. Жёсткий heap-limit даёт **предсказуемое** поведение под памятью: GC начнёт чистить агрессивнее **до** OOM-kill.
3. Накладные расходы (~30 MB) укладываются в запас 50 MB бюджета.

## Реализация

В `runtimeconfig.json` или env:

```json
{
  "runtimeOptions": {
    "configProperties": {
      "System.GC.Server": true,
      "System.GC.Concurrent": true,
      "System.GC.HeapHardLimit": 200000000
    }
  }
}
```

Или через env (предпочтительно для Docker):

```yaml
environment:
  DOTNET_gcServer: "1"
  DOTNET_GCHeapHardLimit: "200000000"
```

В docker-compose `mem_limit: 230m` (запас 30 MB поверх heap limit на native + thread stacks).

## Последствия

- **Положительные:** предсказуемая latency, нет OOM-kill контейнера.
- **Отрицательные:** +30-40 MB начального RSS — съедает запас бюджета.
- **Миграция:** обновить ТЗ §2.2 и §2.3.
- **Мониторинг:**
  - Метрика `GC.GetGCMemoryInfo().HeapSizeBytes` в `/health/ready`.
  - Алерт если heap > 180 MB устойчиво (близко к лимиту).
  - Прометей-метрика `dotnet_gc_pause_seconds` (через `OpenTelemetry.Instrumentation.Runtime`).
- **Rollback plan:** убрать env `DOTNET_gcServer`, вернуться на Workstation. ТЗ переписать.

## Связанные ADR

- ADR-0001 (Monolith on single VPS) — определяет общий бюджет RAM.
