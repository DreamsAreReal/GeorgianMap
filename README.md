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

```bash
git clone https://github.com/DreamsAreReal/GeorgianMap.git
cd GeorgianMap/source/infra
cp .env.example .env
# отредактировать .env (см. таблицу ниже) + создать secrets/ (см. SECRETS.md)
docker compose up -d            # билд API локально, удобно для dev
curl http://localhost/api/v1/health
```

## Production deploy (на VPS)

Используем prebuilt-образ из ghcr.io вместо локального билда (см. [ADR-0009](docs/tech/adr/0009-image-registry-ghcr.md)) — VPS не тратит RAM на компиляцию.

```bash
# на VPS, в /opt/georgia-places/source/infra
git pull
docker compose \
  -f docker-compose.yml \
  -f docker-compose.prod.yml \
  pull api
docker compose \
  -f docker-compose.yml \
  -f docker-compose.prod.yml \
  up -d
```

Откат на конкретный коммит:

```bash
IMAGE_TAG=sha-abc1234 docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d api
```

Образы: `ghcr.io/dreamsarereal/georgianmap-api:latest` (rolling) и `:sha-<7chars>` (для отката). Публикуются автоматически из CI на каждый push в `main`.

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
| `OTEL_EXPORTER_OTLP_ENDPOINT` | yes | — | OTLP HTTP endpoint Grafana Cloud (см. ADR-0006) |
| `OTEL_EXPORTER_OTLP_HEADERS_FILE` | yes | `/run/secrets/otlp_headers` | Путь к файлу с auth header — **через Docker secret, НЕ в env-переменной** (см. ADR-0006) |
| `OTEL_SERVICE_NAME` | yes | — | `georgia-places-api` или `georgia-places-parser` |
| `SENTRY_DSN_FILE` | yes | `/run/secrets/sentry_dsn` | Путь к файлу с Sentry DSN — также через Docker secret |

## Ветки и workflow

Trunk-based с feature branches (см. [ADR-0005](docs/tech/adr/0005-branch-protection-model.md)):

- `main` — protected. Direct push запрещён. Требуется PR + linear history + resolved threads. Force push и удаление невозможны (ruleset).
- `feat/<тема>`, `fix/<тема>` — рабочие feature branches от `main`. После squash-merge удаляются.

Стандартный flow:

```bash
git checkout main && git pull
git checkout -b feat/my-feature
# правки + коммиты
git push origin feat/my-feature
gh pr create --base main --head feat/my-feature
# CI отработает на PR
gh pr merge --squash --delete-branch
git checkout main && git pull
```

Ветка `develop` существует исторически, но **не используется** — следующие правки идут в feature-ветки.

## Лицензия

TBD — будет определено перед публикацией кода.

## Контакты

GitHub Issues: https://github.com/DreamsAreReal/GeorgianMap/issues
