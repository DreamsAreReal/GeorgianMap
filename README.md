# Georgia Places

Бесплатная карта интересных мест в Грузии. Self-hosted, без аккаунтов, без рекламы. Самонаполняющийся каталог через парсинг открытых источников + анонимный UGC с автомодерацией через LLM.

> Status: **планирование**. Кода ещё нет — есть техзадание и каркас репо.

## Структура репозитория

| Путь | Что лежит |
|------|-----------|
| `docs/tech/` | Техдокументация: ТЗ, архитектура, ADR, схема БД, runbooks |
| `docs/business/` | Бизнес-документация: концепция продукта, KPI, политики (DMCA, модерация), affiliate-программы |
| `docs/user/` | Пользовательская дока: FAQ, гайды, описание UI, changelog для конечного пользователя |
| `source/backend/` | ASP.NET Core 9 API (read API + UGC endpoints) |
| `source/parser/` | Python cron-парсер (Google Places, OSM, Wikidata, Telegram) |
| `source/frontend/` | Статический фронт (планируется на Cloudflare Pages) |
| `source/infra/` | Docker Compose, Nginx, скрипты деплоя/бэкапа |

## Документы

- **Техническое задание (полное):** [docs/tech/georgia_places_tz.md](docs/tech/georgia_places_tz.md)
- **ADR (архитектурные решения):** [docs/tech/adr/](docs/tech/adr/)
- **Бэклог ревью:** [docs/tech/REVIEW-BACKLOG.md](docs/tech/REVIEW-BACKLOG.md)

## Prerequisites

Для локальной разработки (после появления кода):

| Tool | Version | Назначение |
|------|---------|------------|
| .NET SDK | 9.x | Backend API |
| Python | 3.12+ | Парсер |
| Docker + Compose | 24+ | Контейнеры (postgres, api, parser) |
| PostgreSQL + PostGIS | 16 + 3.4 | БД (поднимается через compose) |
| rclone | 1.66+ | Бэкапы на Cloudflare R2 |
| gh CLI | 2.40+ | Работа с PR |

## Quick Start

> Будет дополнен по мере появления кода. Пока — клонируем и читаем ТЗ:

```bash
git clone https://github.com/DreamsAreReal/GeorgianMap.git
cd GeorgianMap
open docs/tech/georgia_places_tz.md   # macOS
```

После Этапа 1 (см. §13 ТЗ) появится:

```bash
cp source/infra/.env.example .env
# отредактировать .env (см. таблицу ниже)
docker compose -f source/infra/docker-compose.yml up -d
# API на http://localhost:8080
# Swagger на http://localhost:8080/swagger
```

## Переменные окружения

Все секреты — через `.env` (НЕ коммитится). Шаблон будет в `source/infra/.env.example`.

| Имя | Required | Default | Описание |
|-----|----------|---------|----------|
| `DB_HOST` | yes | `postgres` | Хост PostgreSQL |
| `DB_PORT` | no | `5432` | Порт PostgreSQL |
| `DB_NAME` | yes | `georgia_places` | Имя БД |
| `DB_USER` | yes | — | Пользователь БД |
| `DB_PASSWORD` | yes | — | Пароль БД (генерировать `openssl rand -base64 32`) |
| `GOOGLE_PLACES_API_KEY` | yes | — | Google Places API (бесплатный тир, см. §6.5 ТЗ) |
| `GEMINI_API_KEY` | yes | — | Google Gemini 2.5 Flash для модерации/извлечения |
| `MAPTILER_KEY` | yes | — | MapTiler для тайлов карты (бесплатный тир) |
| `CF_TURNSTILE_SECRET` | yes | — | Cloudflare Turnstile server-side secret для UGC anti-bot |
| `CF_TURNSTILE_SITEKEY` | yes | — | Cloudflare Turnstile site key для фронта |
| `R2_ACCESS_KEY_ID` | yes | — | Cloudflare R2 для бэкапов |
| `R2_SECRET_ACCESS_KEY` | yes | — | — |
| `R2_BUCKET` | yes | — | Имя bucket в R2 |
| `HEALTHCHECKS_PARSER_UUID` | yes | — | UUID для пинга Healthchecks.io после парсера |
| `HEALTHCHECKS_BACKUP_UUID` | yes | — | UUID для пинга Healthchecks.io после бэкапа |
| `ASPNETCORE_ENVIRONMENT` | no | `Production` | `Development` / `Production` |
| `DOTNET_GCHeapHardLimit` | no | — | Лимит .NET GC heap в байтах (см. ADR-0003) |

## Ветки и protection

- `main` — protected. Direct push запрещён, требуется PR + linear history + resolved threads.
- `develop` — рабочая ветка. Все правки сюда, затем PR в `main`.

## Лицензия

TBD — будет определено перед публикацией кода.

## Контакты

GitHub Issues: https://github.com/DreamsAreReal/GeorgianMap/issues
