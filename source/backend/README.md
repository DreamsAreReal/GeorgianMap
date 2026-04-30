# Backend

ASP.NET Core 9 минимальные API. Read API + UGC endpoints.

## Стек

- .NET 9 SDK
- ASP.NET Core 9 (minimal APIs)
- EF Core 9 (миграции) + Dapper (read)
- Npgsql + NetTopologySuite (PostGIS)
- Polly (retry / circuit breaker)
- Serilog (structured logging)
- Swashbuckle (OpenAPI)

## Структура (план)

```
backend/
├── src/
│   ├── GeorgiaPlaces.Api/           # entry point, middleware, endpoints
│   ├── GeorgiaPlaces.Application/   # use cases, validators
│   ├── GeorgiaPlaces.Domain/        # entities, value objects, events
│   └── GeorgiaPlaces.Infrastructure/# EF Core, Dapper, external clients
└── tests/
    ├── GeorgiaPlaces.Tests.Unit/
    └── GeorgiaPlaces.Tests.Integration/
```

## Запуск

> TBD — появится после Этапа 1 (см. §13 ТЗ).

См. корневой [README](../../README.md) и [ТЗ](../../docs/tech/georgia_places_tz.md) §8 (API endpoints).
