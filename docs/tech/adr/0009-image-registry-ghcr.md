# ADR-0009: Image registry on GitHub Container Registry (ghcr.io)

**Статус:** Accepted
**Дата:** 2026-05-02
**Автор:** owner
**Задача:** First-deploy concern: API image previously built on the VPS at deploy time. Build peak RAM ~500 MB conflicts with our 320 MB free-RAM budget (ADR-0001 §RAM бюджет) and risks OOM-kill on the live API during deploy.

## Контекст

Текущий процесс деплоя (по умолчанию compose с `build:` директивой):

```
git pull → docker compose build api → docker compose up -d api
```

Билд внутри VPS занимает ~30-60 сек и съедает ~500 MB RAM (`dotnet sdk` + NuGet restore + публикация). На 1 vCPU / 1 GB VPS это:

1. Создаёт пик RAM, при котором **уже работающий API может быть OOM-killed**.
2. Замедляет деплой до 1-3 мин (включая pull NuGet).
3. Откат — `git revert` + повторный билд (~3 мин даунтайма).

Внешний registry убирает билд с VPS целиком: образы строятся в CI runner-е (4 vCPU / 16 GB бесплатно для public репо), VPS только `pull` + `up -d` (~10 сек).

## Рассмотренные варианты

### Вариант A: Status quo (build на VPS)
- **Плюсы:** ноль внешних зависимостей, работает уже сейчас
- **Минусы:** RAM-пик в момент деплоя, медленный rollout, неудобный rollback
- **Сложность:** Low

### Вариант B: GitHub Container Registry (ghcr.io)
- **Плюсы:** бесплатно для public репо (без лимитов на pull), GITHUB_TOKEN из коробки, тэгирование sha + latest, anonymous pull
- **Минусы:** vendor lock-in (но docker-стандарт — переезд на любой registry в одну строку)
- **Сложность:** Low (~30 строк YAML)

### Вариант C: Docker Hub free
- **Плюсы:** самый известный
- **Минусы:** rate limit 100 pulls/6h на anonymous (нам не критично, но запас минимальный); один публичный repo на free tier
- **Сложность:** Low

### Вариант D: Self-hosted registry на VPS (`registry:2`)
- **Минусы:** ещё один контейнер на нашем 1 GB VPS, нарушает ADR-0001 RAM-бюджет
- **Сложность:** Medium
- **Вердикт:** **отброшено**

## Решение

Выбран **Вариант B** (ghcr.io) потому что:

1. Public-репо → free tier безлимитный, без rate limit на pull.
2. `GITHUB_TOKEN` в Actions имеет `packages: write` через `permissions:` блок — никаких отдельных PAT и секретов.
3. Image namespace `ghcr.io/dreamsarereal/georgiamap-api` совпадает с владением репо — нет рисков squatting.
4. Anonymous pull с VPS — не нужно держать registry-credentials на сервере.

## Реализация

**Тэгирование:**

| Тэг | Когда ставится | Назначение |
|-----|----------------|------------|
| `latest` | каждый push в `main` (после зелёного CI) | Production-rolling — это то, что compose обычно тянет |
| `sha-<7chars>` | каждый push в `main` | Откат на конкретный коммит без необходимости пересобирать |
| `pr-<NNN>` | (опционально, не реализуем сейчас) push в feat-ветку с label | Pre-merge preview |

**Архитектура:** `linux/amd64` only. Дешёвые VPS (Hetzner/Contabo/DigitalOcean) почти всегда x86. ARM добавим если перейдём на Hetzner ARM или Oracle Free Tier.

**Сборка:**

CI workflow добавляет job `publish-image`, который:
- Триггерится только на `push: branches: [main]` (после merge)
- Использует `docker/setup-buildx-action` + `docker/build-push-action`
- `cache-from: type=gha` + `cache-to: type=gha,mode=max` — слои переживают между запусками (бесплатный 10 GB GitHub Actions cache)
- Логин: `docker/login-action@v3` с `password: ${{ secrets.GITHUB_TOKEN }}`
- Push: `ghcr.io/dreamsarereal/georgiamap-api:latest` + `:sha-<short>`

**Compose:** дефолтный `docker-compose.yml` остаётся с `build:` директивой (для dev). Production деплой использует override-файл `docker-compose.prod.yml` где `build:` заменён на `image: ghcr.io/dreamsarereal/georgiamap-api:latest` (или конкретный sha-тэг для отката).

**Workflow на VPS:**

```bash
cd /opt/georgia-places/source/infra
git pull                          # обновить compose + .env.example структуру
docker compose -f docker-compose.yml -f docker-compose.prod.yml pull api
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d api
```

## Последствия

- **Положительные:**
  - Деплой 10-15 сек вместо 1-3 мин
  - Откат `IMAGE_TAG=sha-abcd123 docker compose up -d` — мгновенно
  - VPS RAM не дёргается во время деплоя
  - CI cache ускоряет повторные билды до 1-2 мин (был ~3 мин)
- **Отрицательные:**
  - +1 шаг в CI pipeline; время до полного релиза ~1-2 мин
  - Образы хранятся в ghcr.io — нужно периодически чистить старые `sha-*` тэги (cron-action раз в месяц, оставлять последние 30)
- **Миграция:** существующий `compose up` продолжает работать локально (build:) — никаких breaking changes для dev.
- **Мониторинг:** ghcr.io имеет own status. При его недоступности — fallback на локальный build, документировано в runbook.
- **Rollback plan:** убрать `publish-image` job и override-файл; вернуться к build на VPS. Один PR.

## Триггеры пересмотра

- Если ghcr.io начнёт лимитировать public pulls (сейчас нет лимита) → переезд на Docker Hub или self-hosted
- Если потребуется multi-arch (ARM VPS) → добавить `platforms: linux/amd64,linux/arm64` в `build-push-action` (~3× build time из-за QEMU)

## Связанные ADR

- ADR-0001 (Monolith on single VPS) — RAM-бюджет, который этот выбор защищает
- ADR-0005 (Branch protection) — определяет, что push в main = после squash-merge PR, который уже прошёл CI
