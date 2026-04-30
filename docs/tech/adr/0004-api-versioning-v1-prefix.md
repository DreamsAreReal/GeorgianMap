# ADR-0004: API versioning policy: /api/v1/ prefix

**Статус:** Accepted
**Дата:** 2026-04-30
**Автор:** owner
**Задача:** review finding P0 (api-design): "Нет версионирования URL"

## Контекст

ТЗ §8 описывает endpoints как `/api/places`, `/api/filters`, `/api/route/places`, `/api/aff/redirect` — без префикса версии. API consumed:
- собственный фронт (Cloudflare Pages, обновляется одновременно с бэком)
- Cloudflare Workers (если будут)
- кэш в браузерах пользователей (через CDN, TTL до 24 часов)
- потенциально сторонние интеграции (если кто-то начнёт скрейпить публичный API)

Любое breaking change ломает кэшированных клиентов на стороне браузера, плюс не оставляет окна для миграции если появятся внешние интеграторы.

## Рассмотренные варианты

### Вариант A: Без версии (текущий ТЗ)
- **Плюсы:** короткие URL
- **Минусы:** breaking change = немедленный 404/ошибка для всех клиентов; нет окна на миграцию
- **Сложность внедрения:** Low (ничего)

### Вариант B: URL-префикс `/api/v1/`
- **Плюсы:** явная версия, можно держать v1 и v2 параллельно во время миграции, очевидно для клиентов
- **Минусы:** длиннее URL
- **Сложность внедрения:** Low

### Вариант C: Header-based versioning (`Accept: application/vnd.gp.v1+json`)
- **Плюсы:** URL стабилен
- **Минусы:** труднее тестить через curl, сложнее кэшировать на CDN (Vary: Accept), большинство публичных API не делают так
- **Сложность внедрения:** Medium

## Решение

Выбран **Вариант B** (`/api/v1/...`) потому что:

1. Стандарт де-факто для public REST API.
2. CDN (Cloudflare) кэширует по URL без `Vary` — Вариант C сломает кэш.
3. Стоимость внедрения = одна строка в каждом маршруте: `app.MapGroup("/api/v1")`.
4. При breaking change запускаем `/api/v2/`, держим `/api/v1/` ещё 6-12 месяцев с депрекейшен-хедером.

## Маршруты после правки

| Старое в ТЗ | Новое |
|-------------|-------|
| `GET /api/places` | `GET /api/v1/places` |
| `GET /api/places/{id}` | `GET /api/v1/places/{id}` |
| `GET /api/filters` | `GET /api/v1/filters` |
| `POST /api/route/places` | `GET /api/v1/route/places` (см. также API design review P1-2) |
| `POST /api/places/{id}/reports` | `POST /api/v1/places/{id}/reports` |
| `POST /api/places/{id}/dispute` | `POST /api/v1/places/{id}/dispute` |
| `POST /api/reviews/{id}/vote` | `PUT /api/v1/reviews/{id}/vote` (см. API design review P1-3) |
| `GET /api/aff/redirect` | `POST /api/v1/aff/click` (см. API design review P1-5) |

## Политика deprecation

При выпуске v2:
1. v1 остаётся работать **минимум 12 месяцев**.
2. Все ответы v1 получают header `Deprecation: true` и `Sunset: <RFC 9745 date>`.
3. В Swagger v1 помечается `deprecated: true`.
4. За 90 дней до отключения — баннер на фронте.

## Последствия

- **Положительные:** возможность безопасной эволюции API.
- **Отрицательные:** небольшая дополнительная сложность роутинга в коде.
- **Миграция:** обновить §8 ТЗ — все примеры запросов должны использовать `/api/v1/`. Обновить Swagger config: `c.SwaggerDoc("v1", ...)`.
- **Мониторинг:** метрика `http_requests_total{api_version="v1"}` для контроля доли legacy-трафика после релиза v2.
- **Rollback plan:** убрать префикс при необходимости. Но это сам breaking change — лучше не делать.

## Связанные ADR

- N/A
