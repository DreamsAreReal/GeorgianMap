# Техническое задание: Georgia Places

## 1. Концепция и цели

### 1.1. Идея проекта

Бесплатный веб-сервис «карта крутых мест в Грузии». Пользователь открывает карту по домену, видит интересные места (виды, монастыри, водопады, рестораны, аквапарки и т.д.), может фильтровать по нюансам (дорога, цена, собаки, сезон, парковка), строить маршрут A→B и видеть места по пути. Результат — открытие выбранной точки в Яндекс.Картах или Google Maps по deeplink для навигации.

### 1.2. Ключевые принципы

- **Полностью бесплатный** для пользователя, без регистрации, без рекламы.
- **Без аккаунтов и персональных данных.** Полная анонимность отзывов.
- **Самонаполняющийся.** Парсинг источников по cron, без ручной модерации контента.
- **Самомодерируемый.** Модерация UGC через consensus + LLM-классификацию, без человека в петле.
- **Self-hosted на одном дешёвом VPS** (1 ядро, 1 GB RAM, $5-10/мес).
- **Внешние сервисы — только бесплатные тиры.**

### 1.3. Реалистичный объём эксплуатационной работы

«Полностью автоматический» не означает «нулевой работы вообще». Реалистичный минимум обслуживания владельца:

- **~30 минут/месяц** — мониторинг алертов от Healthchecks/UptimeRobot, проверка что бэкап создаётся, ревью логов парсера на наличие warnings.
- **Ad-hoc починка**: когда меняется HTML-разметка `t.me/s/<channel>`, отключается бесплатная модель Gemini, меняется лимит Google Places — нужно адаптировать код. Реалистично 1-3 раза в год по 1-2 часа.
- **Раз в 6 месяцев** — продление домена, VPS, пересмотр API ключей.
- **DMCA / жалобы по контенту** — обработка по факту, обычно 0-2 случая в год.

Если эту работу не делать, через 6-12 месяцев сервис деградирует до нерабочего состояния.

### 1.4. Целевая нагрузка

- **~50 RPS пиково** (после прогрева Cloudflare кэша), ~5-10 RPS в среднем.
- **Cold cache RPS** (когда контент только запостили в большом канале и кэш ещё не построен): VPS должен выдержать **~10-15 RPS прямых запросов** к origin без падения. Сверх этого срабатывает graceful degradation (см. 12.4).
- Активный кэш на Cloudflare CDN перед бэкендом — 90%+ запросов туда не доходят при прогретом кэше.
- БД: ~50 000 мест, ~500 000 сигналов/отчётов на горизонте 1-2 лет.

### 1.5. Что НЕ входит в систему

- Маршрутизация и навигация — отдаётся Яндекс/Google Maps через deeplink.
- Бронирование, оплата, билеты — нет.
- Регистрация, профили, личные кабинеты — нет.
- Мобильное приложение — нет, только адаптивный веб.
- Многоязычность v1 — только русский.
- Полноценный routing engine — A→B аппроксимируется ломаной через waypoints, не настоящим маршрутом по дорогам (см. раздел 18, edge case #10).

---

## 2. Архитектура

### 2.1. Схема компонентов

```
┌─────────────────────────────────────────────┐
│ Cloudflare (CDN, кэш, WAF, Turnstile)      │
└──────────┬──────────────────────────────────┘
           │
┌──────────▼─────────────┐  ┌──────────────────────┐
│ Cloudflare Pages       │  │ Cloudflare R2        │
│ (статика фронта)       │  │ (бэкапы БД)          │
└──────────┬─────────────┘  └──────────▲───────────┘
           │ API                          │
           ▼                              │
┌─────────────────────────────────────────┴─────┐
│ VPS (1 vCPU, 1 GB RAM, ~$5-10/мес)            │
│  ┌──────────────────────────────────────┐    │
│  │ Nginx (reverse proxy, SSL termination)│   │
│  └──────────┬───────────────────────────┘    │
│             ▼                                  │
│  ┌──────────────────────────────────────┐    │
│  │ ASP.NET Core 9 API                   │    │
│  │ (read API + UGC endpoints)           │    │
│  └──────────┬───────────────────────────┘    │
│             ▼                                  │
│  ┌──────────────────────────────────────┐    │
│  │ PostgreSQL 16 + PostGIS              │    │
│  └──────────────────────────────────────┘    │
│                                                │
│  ┌──────────────────────────────────────┐    │
│  │ Парсер (Python, по cron 03:00)       │    │
│  │ - запускается, отрабатывает, умирает │    │
│  └──────────────────────────────────────┘    │
└────────────────────────────────────────────────┘
                  │
                  ▼ внешние API (бесплатные тиры)
        ┌────────────────────────┐
        │ Google Places API      │
        │ Gemini 2.5 Flash API   │
        │ Geofabrik OSM dump     │
        │ Wikidata SPARQL        │
        │ t.me/s/<channel>       │
        └────────────────────────┘
```

### 2.2. Технологический стек

**Backend:**
- ASP.NET Core 9 (минимальные API)
- Entity Framework Core 9 (для миграций) + Dapper (для read-запросов)
- PostgreSQL 16 + PostGIS 3.4
- Сервинг через Kestrel за Nginx
- **Server GC + конкурентный + `DOTNET_GCHeapHardLimit=200000000`** (см. [ADR-0003](adr/0003-server-gc-with-heap-limit.md)) — предсказуемая latency на throughput-сервисе, жёсткий ceiling вместо OOM-kill
- **Trimmed publish** без Native AOT (AOT часто конфликтует с EF Core рефлексией)

**Frontend:**
- React 18 + Vite
- MapLibre GL JS (карта, тайлы от MapTiler free / OSM)
- Tailwind CSS + shadcn/ui
- Tanstack Query (кэширование)
- Zustand (состояние)

**Парсер:**
- Python 3.12
- httpx + BeautifulSoup4 (HTTP-парсинг Telegram через `t.me/s/`)
- google-generativeai (Gemini SDK)
- psycopg3 (Postgres)

**Инфраструктура:**
- Docker + Docker Compose
- Cloudflare (CDN, Pages, Turnstile, R2, Email Routing)
- Healthchecks.io (мониторинг cron)

### 2.3. Бюджет памяти на VPS (1 GB RAM)

| Компонент | Лимит | Комментарий |
|-----------|-------|-------------|
| Ubuntu система | ~150 MB | базовый minimal |
| Postgres | ~250 MB | `shared_buffers=128MB`, `max_connections=20` |
| .NET API | ~200 MB | Server GC + concurrent + `GCHeapHardLimit=200MB` (ADR-0003); `mem_limit: 230m` в compose (запас на native + thread stacks) |
| Nginx | ~20 MB | alpine |
| Кэш ОС / запас | ~380 MB | |
| **Парсер** | ~300 MB | **запускается только когда API спит (03:00-06:00)** |

Своп 2 GB обязательно (`/swapfile`, `swappiness=10`).

---

## 3. Источники данных

### 3.1. Tier 1 — Авторитетные (применяются без consensus)

| Источник | Что даёт | Метод | Лимиты |
|----------|----------|-------|--------|
| OpenStreetMap | POI, координаты, базовые теги | Geofabrik daily PBF dump Грузии + osmium tool | Free, ~10 MB файл, парсится локально |
| Wikidata | Описания на ru, фото из Commons | SPARQL | Free, без лимитов |
| Google Places API | Часы работы, рейтинги, фото, отзывы | REST API | Free tier $200/мес кредита |

### 3.2. Tier 2 — Парсинг (применяются по consensus)

| Источник | Что даёт | Метод |
|----------|----------|-------|
| Telegram каналы | Свежие обзоры мест | HTTP GET `t.me/s/<channel>` |
| Telegram чаты (публичные превью) | Реальные отчёты "сегодня закрыто" | То же |
| YouTube субтитры | Описания дорог, нюансы | yt-dlp |

**Список каналов и чатов для парсинга (стартовый):**

- `@gryziya` — Батуми туристы
- `@chat_tbilisi` — Тбилиси туристы
- `@relokaciya_gruziya` — релокация
- `@gruziyaa` — банки/быт
- `@tbilisistilllovesme` — Tbilisi still loves me
- `@geotrip` — Путешествия по Грузии
- `@therefrom` — Оттудова
- Список расширяется через `tgstat.com` поиск по тегам "Грузия", "Тбилиси", "Батуми", "Кахетия".

Парсинг **только публичных каналов** через web-превью `t.me/s/<channel>`. Никаких аккаунтов, сессий Telegram, MTProto. Никаких API-ключей Telegram.

### 3.3. Tier 3 — UGC (самый строгий consensus)

Анонимные отчёты с сайта через форму. См. раздел 7.

---

## 4. Структура данных

### 4.1. Таблица `places` — основная

```sql
CREATE TABLE places (
    id BIGSERIAL PRIMARY KEY,
    name TEXT NOT NULL,
    name_en TEXT,
    name_ka TEXT,
    description TEXT,
    
    -- геометрия
    geom GEOGRAPHY(POINT, 4326) NOT NULL,
    
    -- категоризация
    category TEXT NOT NULL,  -- enum: viewpoint, monastery, waterfall, restaurant, ...
    subcategory TEXT,
    
    -- внешние идентификаторы для дедупа
    osm_id BIGINT,
    osm_type TEXT,  -- 'node', 'way', 'relation'
    google_place_id TEXT,
    wikidata_id TEXT,
    
    -- атрибуты (динамическая часть, фильтрация через JSONB)
    attributes JSONB DEFAULT '{}'::jsonb,
    
    -- метаданные источников и свежести
    attribute_sources JSONB DEFAULT '{}'::jsonb,
    last_verified_at TIMESTAMPTZ,
    data_freshness_score REAL DEFAULT 0.5,
    
    -- агрегаты Google Places
    google_rating REAL,
    google_reviews_count INT,
    google_photos JSONB,
    google_hours JSONB,
    google_updated_at TIMESTAMPTZ,
    
    -- сезонность
    seasonal_pattern JSONB,
    -- {"open_months": [4,5,6,7,8,9,10], "type": "summer_only"}
    
    -- статистика
    reports_count INT DEFAULT 0,

    -- DMCA / ручное скрытие (см. §19.16)
    hidden BOOLEAN NOT NULL DEFAULT false,
    hidden_reason TEXT,
    hidden_at TIMESTAMPTZ,

    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX idx_places_geom ON places USING GIST (geom) WHERE hidden = false;
CREATE INDEX idx_places_attributes ON places USING GIN (attributes) WHERE hidden = false;
CREATE INDEX idx_places_category ON places (category) WHERE hidden = false;
CREATE INDEX idx_places_google_id ON places (google_place_id) WHERE google_place_id IS NOT NULL;
CREATE INDEX idx_places_osm_id ON places (osm_id, osm_type) WHERE osm_id IS NOT NULL;
```

> Все API read-запросы добавляют `WHERE hidden = false` (одна строка в DAL). Partial-индексы исключают скрытые места из spatial и фильтр-запросов — никакого падения performance после DMCA.

### 4.2. Таблица `attributes_dictionary` — словарь атрибутов

Управляющая таблица для динамических фильтров на фронте.

```sql
CREATE TABLE attributes_dictionary (
    key TEXT PRIMARY KEY,
    label_ru TEXT NOT NULL,
    label_en TEXT,
    type TEXT NOT NULL CHECK (type IN ('bool', 'enum', 'range_int', 'range_float')),
    options JSONB,         -- для enum: ["none","friendly","aggressive"]
    unit TEXT,             -- '₾', 'км' и т.д.
    icon TEXT,             -- эмодзи или имя иконки
    categories TEXT[],     -- к каким категориям применяется (NULL = ко всем)
    filterable BOOLEAN DEFAULT true,
    display_order INT DEFAULT 100,
    created_at TIMESTAMPTZ DEFAULT now()
);
```

Примеры записей:

| key | label_ru | type | options | icon |
|-----|----------|------|---------|------|
| free | Бесплатно | bool | — | 🆓 |
| price_gel | Цена | range_int | — | 💰 |
| dogs | Собаки | enum | `["none","friendly","aggressive"]` | 🐕 |
| road | Дорога | enum | `["paved","gravel","4wd_only"]` | 🛣 |
| season | Сезон | enum | `["year_round","summer_only","winter_closed"]` | 🌤 |
| toilet | Туалет | bool | — | 🚻 |
| parking | Парковка | bool | — | 🅿️ |
| crowded | Многолюдность | enum | `["quiet","moderate","crowded"]` | 👥 |
| mobile_signal | Связь | bool | — | 📶 |

### 4.3. Таблица `place_signals` — сырые сигналы для consensus

Каждое утверждение об атрибуте места из любого источника — отдельная запись.

```sql
CREATE TABLE place_signals (
    id BIGSERIAL PRIMARY KEY,
    place_id BIGINT NOT NULL REFERENCES places(id) ON DELETE CASCADE,
    
    -- что утверждается
    attribute_key TEXT NOT NULL REFERENCES attributes_dictionary(key),
    attribute_value JSONB NOT NULL,
    
    -- источник
    source_type TEXT NOT NULL,  -- 'google_places', 'osm', 'tg_channel', 'tg_chat', 'youtube', 'user_report'
    source_id TEXT,             -- внутренний ID источника
    weight REAL NOT NULL,       -- начальный вес сигнала (см. 5.1)
    
    -- для UGC: технические идентификаторы (без персданных, см. ADR-0007 §5.3.3)
    -- ip_hash удалён — сырой IP не хранится вообще
    fingerprint_hash TEXT,                              -- браузерный fingerprint
    subnet_hash TEXT,                                   -- SHA256(salt || /24-subnet); соль ротируется раз в 90 дней
    
    -- агрегация
    excluded_from_consensus BOOLEAN DEFAULT false,  -- помечается при детекции аномалий
    excluded_reason TEXT,
    
    -- сырой текст для аудита и переобработки
    raw_text TEXT,
    
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX idx_signals_place_attr ON place_signals (place_id, attribute_key, created_at DESC);
CREATE INDEX idx_signals_source ON place_signals (source_type, source_id);
CREATE INDEX idx_signals_fingerprint ON place_signals (fingerprint_hash) 
    WHERE fingerprint_hash IS NOT NULL;
```

### 4.4. Таблица `place_reviews` — публичные отзывы

```sql
CREATE TABLE place_reviews (
    id BIGSERIAL PRIMARY KEY,
    place_id BIGINT NOT NULL REFERENCES places(id) ON DELETE CASCADE,
    
    text TEXT NOT NULL,
    visit_date DATE,
    flags TEXT[],  -- структурированные пометки из формы
    
    -- технические (анонимные)
    ip_hash TEXT,
    fingerprint_hash TEXT,
    
    -- модерация
    moderation_status TEXT NOT NULL DEFAULT 'pending',
    -- 'pending' | 'published' | 'hidden'
    moderation_reason TEXT,
    
    -- голосование
    helpful_count INT DEFAULT 0,
    unhelpful_count INT DEFAULT 0,
    flag_count INT DEFAULT 0,
    
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX idx_reviews_place_status ON place_reviews (place_id, moderation_status, created_at DESC);
```

### 4.5. Таблица `review_votes` — голоса под отзывами

```sql
CREATE TABLE review_votes (
    review_id BIGINT NOT NULL REFERENCES place_reviews(id) ON DELETE CASCADE,
    fingerprint_hash TEXT NOT NULL,
    vote SMALLINT NOT NULL,  -- 1 (helpful), -1 (unhelpful), 0 (flag)
    created_at TIMESTAMPTZ DEFAULT now(),
    PRIMARY KEY (review_id, fingerprint_hash)
);
```

### 4.6. Таблица `raw_sources` — сырые данные источников

Хранится для возможной переобработки при изменении промптов LLM.

```sql
CREATE TABLE raw_sources (
    id BIGSERIAL PRIMARY KEY,
    source_type TEXT NOT NULL,
    source_url TEXT,
    source_id TEXT,             -- post_id для TG, place_id для Google
    raw_content TEXT NOT NULL,
    metadata JSONB,
    
    processed BOOLEAN DEFAULT false,
    processed_at TIMESTAMPTZ,
    
    fetched_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX idx_raw_unprocessed ON raw_sources (source_type, processed) 
    WHERE processed = false;
CREATE UNIQUE INDEX idx_raw_dedup ON raw_sources (source_type, source_id) 
    WHERE source_id IS NOT NULL;
```

### 4.7. Таблица `parser_runs` — журнал работы парсера

```sql
CREATE TABLE parser_runs (
    id BIGSERIAL PRIMARY KEY,
    job_name TEXT NOT NULL,         -- 'osm_import', 'tg_parse', 'gemini_extract', etc
    started_at TIMESTAMPTZ NOT NULL,
    finished_at TIMESTAMPTZ,
    status TEXT,                    -- 'running', 'success', 'failed'
    items_processed INT DEFAULT 0,
    items_failed INT DEFAULT 0,
    error_message TEXT,
    metadata JSONB
);

CREATE INDEX idx_parser_runs_job ON parser_runs (job_name, started_at DESC);
```

---

## 5. Логика consensus и автомодерации

### 5.1. Веса сигналов по источникам

| source_type | weight |
|-------------|--------|
| `google_places` | 1.0 |
| `wikidata` | 0.9 |
| `osm` | 0.7 |
| `tg_channel` (после LLM-извлечения) | 0.4 |
| `tg_chat` (после LLM-извлечения) | 0.4 |
| `youtube` (из субтитров) | 0.3 |
| `user_report` (анонимный) | 0.2 |
| `user_report_helpful` (отзыв с >5 helpful) | 0.4 |

### 5.2. Правила автоматического применения факта

Логика **зависит от уровня доверия источника**. Это критично: без этого на старте (когда UGC ещё нет) система **никогда** не применила бы ни одного факта.

#### 5.2.1. Tier 1 — авторитетные источники (применяются без consensus)

Сигналы от `google_places`, `wikidata`, `osm` применяются **немедленно** при поступлении (один источник = один сигнал = достаточно). Логика:

- Если в `places.attributes` уже есть значение от другого Tier-1 источника **с большим весом** — оставляем большее.
- Если значение от Tier-2 или Tier-3 — Tier-1 **перебивает** (с обновлением `attribute_sources`).
- Если значение конфликтует между двумя Tier-1 (Google говорит «работает», Wikidata устарела) — оставляем тот у которого `weight × freshness_factor` выше.

#### 5.2.2. Tier 2 — Telegram / YouTube парсинг (per-impact thresholds)

Полные правила в [ADR-0008](adr/0008-consensus-override-thresholds.md). Применение Tier-2 сигнала зависит от **impact level** атрибута:

| Impact | Атрибуты | Min agreeing signals | Min confidence |
|--------|----------|----------------------|----------------|
| **Critical** | `price_gel`, `entrance_fee`, `is_open`, `is_closed_permanently` | 3 | 0.8 |
| **High** | `dogs`, `road`, `wheelchair_accessible`, `kids_allowed` | 2 | 0.75 |
| **Medium** | `parking`, `wifi`, `family_friendly`, `viewpoint_360`, `bathrooms` | 1 | 0.7 |
| **Low** | `instagram_friendly`, `quiet`, `crowded`, `aesthetic_score` | 1 | 0.6 |

Дополнительные правила:

- Не перебивает Tier-1.
- Если в `places.attributes` уже есть значение от Tier-2 — применяется только если новый сигнал свежее на ≥7 дней **или** confidence выше на ≥0.15 (с учётом порогов выше).
- **Single-signal apply** для Medium/Low помечается `provisional = true` в `attribute_sources`. Через 7 дней без подтверждения — сбрасывается обратно в `null`.
- Сигналы старше 90 дней не считаются для Critical/High уровней.
- «Agreeing» = одинаковое значение для bool/enum, ±10% для range_int/range_float.

> **Почему так:** прежнее правило (single Tier-2 ≥ 0.7 → apply) уязвимо к иронии и опечаткам в источниках. Иронический пост в туристическом канале с 15K подписчиков давал `free=true` как факт. Закрывает P0 из REVIEW-BACKLOG.

#### 5.2.3. Tier 3 — UGC (применяется только при consensus)

Здесь реальный риск троллинга — поэтому строгие пороги:

```python
UGC_CONSENSUS_THRESHOLDS = {
    "boolean_attributes": {
        "min_signals": 3,
        "min_unique_subnets": 2,
        "min_age_spread_hours": 24,    # сигналы должны прийти не за один час
        "max_age_days": 60,
        "min_total_weight": 0.6,
        "min_agreement_pct": 70,       # 70% сигналов должны соглашаться
    },
    "numeric_attributes": {
        "min_signals": 4,
        "min_agreement_pct": 60,        # 60% в пределах ±20% от медианы
        "use_median": True,
        "min_total_weight": 0.8,
    },
    "is_open_status": {
        "min_signals": 5,
        "min_unique_subnets": 3,
        "min_age_spread_hours": 48,
        "google_override": True,        # Google Places hours всегда побеждают
        "min_total_weight": 1.0,
    },
}
```

#### 5.2.4. Mixed: Tier-1 + UGC dispute

Если для атрибута есть Tier-1 значение и накопились UGC-disputes — Tier-1 **не сбрасывается автоматически**. На UI показывается значок «возможно устарело — N посетителей сообщили о расхождении». См. раздел 5.6.

**Правило применения:** факт применяется в `places.attributes` только при выполнении ВСЕХ порогов для своего типа. Иначе остаётся прежнее значение или `null`.

### 5.3. Detection аномалий (анти-абуз)

Полные правила в [ADR-0007](adr/0007-brigading-detection.md). Два временных окна: **24 часа** (быстрые атаки) и **7 дней** (slow brigading).

#### 5.3.1. 24-часовые паттерны (быстрые атаки)

```python
def detect_anomaly_24h(signals):
    if len(signals) > 10:
        return "burst_attack"

    subnets = Counter(s.subnet_hash for s in signals)
    if signals and max(subnets.values()) / len(signals) > 0.8:
        return "single_source"

    fingerprints = Counter(s.fingerprint_hash for s in signals)
    if signals and max(fingerprints.values()) / len(signals) > 0.5:
        return "single_device"

    if check_text_similarity_via_llm(signals) > 0.7:
        return "copypaste"

    if len(signals) >= 5:
        values = [s.attribute_value for s in signals]
        if len(set(json.dumps(v) for v in values)) == 1 and \
           all(s.source_type == 'user_report' for s in signals):
            return "coordinated_unanimous"

    return None
```

#### 5.3.2. 7-дневные паттерны (slow brigading)

Закрывают P0: атака 4 сигнала/день × 5 дней не триггерит ни один из 24-часовых порогов. Запускаются hourly job-ом `anomaly_detect`.

**Паттерн `slow_brigading_subnet_7d`** — ≥4 сигналов с одной /24 subnet за 7 дней на одно (place_id, attribute_key). Сигналы помечаются `excluded_from_consensus = true`.

**Паттерн `slow_brigading_fingerprint_7d`** — ≥3 сигналов с одинаковым fingerprint_hash за 7 дней.

**Паттерн `coordinated_value_flip`** — ≥5 сигналов на одно (place_id, attribute_key, attribute_value), все меняют значение на одно и то же, ≥3 разных subnet, при этом ≥75% сигналов в пределах ±6 часов от медианного времени. Идут в **LLM-арбитраж** (Gemini оценивает органичность). Решение Gemini сохраняется.

При срабатывании любого паттерна — все сигналы периода помечаются `excluded_from_consensus = true` с указанием reason. Они **не удаляются**, но не участвуют в агрегации.

#### 5.3.3. IP-адреса и анонимность

ТЗ §1.2 требует «без персональных данных». IP — PII в EU. Решение:
- Храним **не сам IP, а соленый хэш subnet /24**: `subnet_hash = SHA256(salt || subnet_24)`.
- Соль ротируется раз в 90 дней (через cron) — старые хэши становятся бесполезны для re-identification.
- Сырой IP не пишется в БД, только в logs с маской последнего октета (`1.2.3.0/24`), retention логов — 14 дней.

### 5.4. Time-decay при пересчёте

Каждую ночь cron пересчитывает `places.attributes` с учётом старения сигналов:

```python
def effective_weight(signal, now):
    days_old = (now - signal.created_at).days
    
    # Авторитетные источники не дешевеют
    if signal.source_type in ('google_places', 'wikidata'):
        return signal.weight
    
    # Остальные — экспоненциальный decay с tau = 30 дней
    return signal.weight * exp(-days_old / 30)
```

Через 90 дней `effective_weight` падает до ~5% начального — сигнал практически игнорируется. Через 90 дней он удаляется (cleanup).

### 5.5. LLM-арбитраж пограничных случаев

Когда сигналов достаточно по количеству, но они противоречат друг другу — вызывается арбитраж через Gemini Flash:

```
Промпт:
  Место: {name}, категория: {category}
  Атрибут: {key} (тип: {type})
  
  Сигналы:
  - [google_places]: value=X, дата: ...
  - [user_report]: value=Y, дата: ..., текст: "..."
  - [tg_chat]: value=X, дата: ..., текст: "..."
  - ...
  
  Определи наиболее вероятное значение и confidence (0-1).
  Если confidence < 0.7 — верни "unknown".
  
  JSON: {"value": ..., "confidence": ..., "reasoning": "..."}
```

Применение:
- `confidence >= 0.7` → применить значение
- `confidence < 0.7` → не менять, оставить как есть или `null`

**Никакая ручная модерация не предусмотрена.** Если LLM не уверена — система просто ждёт больше сигналов.

### 5.6. Самовосстановление через "Сообщить о неточности"

На карточке места есть кнопка `🚩 Данные неверные?`. **Логика консервативная** — кнопка не должна быть вектором атаки.

Клик создаёт сигнал типа `user_dispute` с весом 0.3 для каждого указанного атрибута. Эффект на данные:

- **1-2 disputes**: атрибут **не меняется**, но на UI рядом появляется значок ⚠ «1 посетитель сообщил о расхождении».
- **3-4 disputes от ≥2 разных подсетей**: атрибут показывается серым с пометкой «возможно устарело», но значение остаётся.
- **≥5 disputes от ≥3 подсетей за период >7 дней**: атрибут сбрасывается в `null` (показывается «уточняется»). Если источник был Tier-1 (Google) — **не сбрасывается**, только помечается «возможно устарело».
- **Burst-disputes (>5 за час)**: попадают под anomaly detection (5.3), помечаются `excluded_from_consensus=true` и не учитываются.

**Защита от атак:**
- Rate limit: 1 dispute на (place + IP + 24ч), 5 disputes на IP/24ч на разные места.
- Cloudflare Turnstile обязателен.
- Dispute не действует на атрибуты с `source_type = google_places` (только подсветка).
- Если по одному месту >70% disputes от одной /24 подсети — anomaly.

**Логика лучше — пустое поле, чем неправильное.** Сброс в `null` на UI отображается честно: «данные уточняются — будьте первым кто проверит».

---

## 6. Парсер (cron jobs)

### 6.1. Расписание

```
01:00 — backup БД на Cloudflare R2
03:00 — старт ночного парсинга
06:00 — soft-deadline парсинга (если не закончил — логируем warning)
07:00 — hard kill парсера (защита от зависания)
```

### 6.2. Дневные лимиты для соблюдения free tier

**Важно:** лимит Gemini Flash (1500 RPD) — **общий на весь проект**, не на каждый job. Все потребители (UGC классификация, TG extract, арбитраж конфликтов) должны делить общий бюджет.

```python
DAILY_BUDGETS = {
    # Общий бюджет Gemini, делится между всеми потребителями
    "gemini_total_rpd": 1300,           # запас 200 от лимита 1500
    
    # Распределение по приоритету (мягкое)
    "gemini_ugc_classification": 500,    # отзывы юзеров — приоритет 1
    "gemini_tg_extract": 600,            # извлечение из TG — приоритет 2
    "gemini_arbitration": 200,           # арбитраж конфликтов — приоритет 3
    
    # Внешние API
    "google_places_calls": 100,          # Place Details + Reviews
    "telegram_channels_per_run": 15,
    "youtube_videos": 20,
    
    # Технические
    "max_runtime_seconds": 14400,        # 4 часа
}
```

#### 6.2.1. Централизованный rate-limiter Gemini

В Postgres таблица `api_budget_counters`:

```sql
CREATE TABLE api_budget_counters (
    api_name TEXT NOT NULL,
    date DATE NOT NULL,
    counter INT NOT NULL DEFAULT 0,
    PRIMARY KEY (api_name, date)
);
```

Перед каждым вызовом Gemini — **атомарная** операция check+increment:

```python
def try_reserve_gemini(category: str) -> bool:
    """
    Атомарно резервирует один вызов Gemini для категории.
    Возвращает True если вызов разрешён, False — если лимит исчерпан.
    Никаких race conditions: при параллельных воркерах суммарный counter
    не превысит лимит даже на 1.
    """
    today = date.today()
    cat_budget = DAILY_BUDGETS[f"gemini_{category}"]
    total_budget = DAILY_BUDGETS["gemini_total_rpd"]
    is_critical = category == "ugc_classification"

    with db.transaction():
        # Один UPDATE проверяет оба лимита и увеличивает оба счётчика, либо ничего
        result = db.execute("""
            WITH global_check AS (
                UPDATE api_budget_counters
                SET counter = counter + 1, updated_at = now()
                WHERE api_name = 'gemini_total' AND date = %s
                  AND counter < %s
                RETURNING counter
            ),
            category_check AS (
                UPDATE api_budget_counters
                SET counter = counter + 1, updated_at = now()
                WHERE api_name = %s AND date = %s
                  AND (counter < %s OR %s)
                  AND EXISTS (SELECT 1 FROM global_check)
                RETURNING counter
            )
            SELECT
                (SELECT counter FROM global_check) AS gtotal,
                (SELECT counter FROM category_check) AS gcat
        """, (today, total_budget,
              f"gemini_{category}", today, cat_budget, is_critical))

        row = result.fetchone()
        if row is None or row.gtotal is None:
            return False  # глобальный лимит достигнут
        if row.gcat is None and not is_critical:
            # глобальный увеличили, категорийный нет — откатим глобальный
            db.execute("""
                UPDATE api_budget_counters
                SET counter = counter - 1
                WHERE api_name = 'gemini_total' AND date = %s
            """, (today,))
            return False
        return True
```

> **Почему так:** прежняя версия (`get_counter` + `if` + `increment_counter`) имела race condition — два параллельных воркера (hourly + daily при overlap) могли пройти проверку одновременно и суммарно превысить лимит. Закрыто P0 из REVIEW-BACKLOG.

При исчерпании лимита: задача попадает в `processing_queue` с `next_retry_at = tomorrow 00:01 UTC`. Не теряется, обработается завтра.

#### 6.2.2. Soft-deadline и kill-switch

При достижении лимита `max_runtime_seconds` — job **корректно завершается**, сохраняя прогресс в БД. На следующую ночь продолжает с того места. Если процесс висит дольше → `cron` через 4ч 30мин делает `docker kill`.

### 6.3. Список jobs

| Job | Частота | Что делает |
|-----|---------|-----------|
| `osm_import` | раз в неделю (вс 02:00) | Geofabrik daily dump Грузии (несколько MB) → парсинг osmium → новые/обновлённые POI. Overpass live запросы НЕ используются (тяжёлые, нестабильны на больших регионах) |
| `wikidata_enrich` | раз в неделю | SPARQL по координатам Грузии → описания |
| `google_places_enrich` | каждую ночь, 100 мест | Place Details + reviews для мест без актуальных данных |
| `tg_fetch_posts` | каждую ночь | Скачивание новых постов из публичных каналов через `t.me/s/` |
| `gemini_extract_attrs` | каждую ночь | LLM-извлечение атрибутов из `raw_sources.processed=false` |
| `signals_aggregate` | каждый час | Пересчёт consensus, применение фактов |
| `anomaly_detect` | каждый час | Детекция burst/copypaste/single_source |
| `nightly_recompute` | каждую ночь | Полный пересчёт `places.attributes` с decay |
| `cleanup_old_signals` | раз в неделю | Удаление сигналов старше 90 дней |
| `health_ping` | каждый запуск | Пинг Healthchecks.io после успеха |

### 6.4. Идемпотентность

Все jobs **обязаны быть идемпотентными**. Запуск дважды подряд не должен ломать данные. Везде используется `INSERT ... ON CONFLICT DO UPDATE` (для places по `(osm_type, osm_id)` или `google_place_id`; для сигналов — по `(source_type, source_id, attribute_key)`).

### 6.4.1. Изоляция парсера от API через staging-схему

**Проблема:** парсер делает массовые UPDATE/INSERT на 50k записей. Если работает на тех же таблицах что и API — длинные транзакции блокируют чтения, статистика устаревает, индексы пухнут.

**Решение:** парсер пишет в **отдельную staging-схему** Postgres:

```sql
CREATE SCHEMA staging;

CREATE TABLE staging.places (LIKE public.places INCLUDING ALL);
CREATE TABLE staging.place_signals (LIKE public.place_signals INCLUDING ALL);
CREATE TABLE staging.raw_sources (LIKE public.raw_sources INCLUDING ALL);
```

Парсер:
1. Пишет всё в `staging.*`.
2. В конце прогона делает **атомарный merge** в публичные таблицы одним коротким statement-ом:
   ```sql
   BEGIN;
   INSERT INTO public.place_signals SELECT * FROM staging.place_signals
   ON CONFLICT (...) DO UPDATE SET ...;
   TRUNCATE staging.place_signals;
   COMMIT;
   ```
3. Длительные операции (Gemini вызовы) происходят **до** транзакционного merge — поэтому короткая транзакция в конце не блокирует API.

Дополнительно: для парсера задаётся `statement_timeout = 5s` (отбивает медленные запросы), `lock_timeout = 1s` (не висит на блокировках).

### 6.5. Telegram-парсинг через `t.me/s/`

#### 6.5.1. Принцип

Используется только публичная веб-версия канала. Никакого Bot API, MTProto, Telethon, никаких аккаунтов и сессионных файлов. Запросы идут как обычный HTTP с фейковым User-Agent браузера.

#### 6.5.2. Реальные ограничения подхода

- **Только последние ~20 постов на странице.** Пагинация через `?before=<post_id>` иногда обрывается на 100-300 постов назад без ошибки. Полный архив получить нельзя. Для уже существующих каналов делается **разовый исторический заброс** при первом подключении канала, потом только инкрементальные запросы новых постов.
- **Только публичные каналы.** Приватные каналы и закрытые чаты не парсятся вообще.
- **HTML-структура нестабильна.** Telegram меняет вёрстку 1-2 раза в год. Парсер должен иметь sanity-check: если за прогон извлечено 0 постов из канала который раньше отдавал — алерт через Healthchecks с тегом `?fail=structure_changed`.
- **Дедупликация.** Один пост может прийти как: оригинал в канале, репост в другом канале, цитата в посте. Дедуп по SHA-256 от нормализованного текста (без emoji/whitespace) в `raw_sources.source_id`.
- **Скрытые посты.** Посты помеченные «18+» или удалённые автором не отдаются — это OK, не паримся.

#### 6.5.3. Псевдокод

```python
async def fetch_channel_incremental(channel: str):
    # last_seen_id хранится в БД per канал
    last_seen_id = await db.get_last_seen_post_id(channel)
    
    new_posts = []
    cursor = None
    
    for page_num in range(1, 6):  # максимум 5 страниц назад за прогон
        url = f"https://t.me/s/{channel}"
        params = {"before": cursor} if cursor else {}
        
        try:
            html = await httpx_get(url, params=params, timeout=15)
        except Exception as e:
            await alert_structure_change(channel, str(e))
            return []
        
        soup = BeautifulSoup(html, "lxml")
        page_posts = parse_posts(soup)
        
        if not page_posts:
            if page_num == 1:
                await alert_structure_change(channel, "no posts on first page")
            break
        
        # фильтр уже виденных
        fresh = [p for p in page_posts if p.id > last_seen_id]
        new_posts.extend(fresh)
        
        # если на странице все посты уже видели — стоп
        if not fresh:
            break
        
        cursor = min(p.id for p in page_posts)
        await asyncio.sleep(2)  # вежливо
    
    # дедуп по hash содержимого
    deduped = await dedup_by_content_hash(new_posts)
    
    for p in deduped:
        await db.save_raw_source(
            source_type='tg_channel',
            source_id=f"{channel}:{p.id}",
            raw_content=p.text,
            metadata={"date": p.date, "url": p.url}
        )
    
    if new_posts:
        await db.update_last_seen(channel, max(p.id for p in new_posts))
    
    return deduped
```

### 6.6. Google Places photos — кэширование

**Проблема:** ссылки на фото из Google Places API (`photo_reference` → URL) живут ~1 час, после чего перестают работать. Кэшировать сами URL запрещено ToS Google. Каждое открытие карточки = вызов API за фото = быстро исчерпает квоту.

**Решение:** при первом обогащении места парсер скачивает 3-5 фото на VPS / R2:

```python
def fetch_and_cache_photos(place: Place, photo_refs: list[str], max_photos=5):
    cached_urls = []
    
    for i, ref in enumerate(photo_refs[:max_photos]):
        google_url = f"https://maps.googleapis.com/maps/api/place/photo?maxwidth=800&photo_reference={ref}&key={KEY}"
        img_data = httpx.get(google_url, follow_redirects=True).content
        
        # сохраняем на R2
        filename = f"places/{place.id}/photo_{i}.jpg"
        upload_to_r2(filename, img_data)
        
        cached_urls.append(f"https://cdn.{DOMAIN}/{filename}")
    
    place.google_photos = cached_urls
    db.save(place)
```

В `places.google_photos` лежат URL **на свой CDN**, не на Google. ToS Google разрешает короткое кэширование с атрибуцией. На UI рядом с фото — `📷 Photo by Google`. Перезаливка раз в 90 дней.

### 6.7. Промпт для Gemini-извлечения атрибутов

```
Ты извлекаешь информацию о месте в Грузии из текста.

Доступные атрибуты (используй ТОЛЬКО эти ключи):
{attributes_dictionary_json}

Если атрибут в тексте не упомянут — НЕ включай его в результат.
Не выдумывай данные. Если в тексте сказано "вроде бесплатно" — confidence низкий, лучше пропусти.

Текст: """{text}"""
Место (если упомянуто): "{place_hint}"

Верни JSON:
{
  "place_mentions": ["название места 1", "название места 2"],
  "attributes": {
    "key1": {"value": ..., "confidence": 0.0-1.0, "evidence": "цитата"},
    ...
  }
}

Только JSON, без markdown.
```

Извлечения с `confidence < 0.6` отбрасываются.

---

## 7. UGC: форма отзыва на сайте

### 7.1. UX формы

```
┌─────────────────────────────────────────────┐
│ 📍 Монастырь Икалто                         │
│                                              │
│ Был тут? Поделись опытом                    │
│                                              │
│ Когда был: [Сегодня ▾] [Вчера] [На неделе] │
│                                              │
│ Статус сейчас:                              │
│  ( ) ✅ Работает                            │
│  ( ) ❌ Закрыт                              │
│  ( ) ❓ Не знаю                             │
│                                              │
│ Цена входа: [____] ₾                       │
│                                              │
│ Что добавить:                               │
│  ☐ Парковка платная                        │
│  ☐ Дорога убитая                           │
│  ☐ Связь не ловит                          │
│  ☐ Туалет есть                             │
│  ☐ Собаки агрессивные                      │
│  ☐ Сезонно (только летом)                  │
│                                              │
│ Расскажите подробнее (от 20 символов):     │
│ [________________________________________] │
│                                              │
│ [Cloudflare Turnstile widget]              │
│                                              │
│              [Отправить]                    │
└─────────────────────────────────────────────┘
```

**Никакого имени, email, регистрации.** Никаких полей идентификации.

### 7.2. Защита формы

| Защита | Реализация |
|--------|-----------|
| Антибот | Cloudflare Turnstile, валидация на бэке |
| Honeypot | Скрытое поле `website`, заполнено = бот |
| Proof-of-work | Лёгкий PoW на клиенте (1-2 сек CPU): `sha256(challenge + nonce)` с N ведущими нулями. Незаметно для одного юзера, дорого для массовой атаки |
| Rate limit per IP | Cloudflare WAF: **3 отчёта/IP/24ч на одно и то же место**, **10 отчётов/IP/24ч на разные места** (учитывает кейс семьи в одном NAT 4G) |
| Rate limit per fingerprint | На бэке: 5 отчётов/fingerprint/24ч |
| Глобальный rate limit | На уровне приложения: **300 отчётов/час на весь сервис** (защита от DDoS) — при превышении новые попадают в очередь до следующего часа |
| Min text length | 20 символов на фронте и бэке |
| Min form fill time | 5 секунд (засекается на фронте, передаётся в payload, валидируется на бэке) |
| Sanity ranges | `price_gel ∈ [0, 200]` и т.д. |

### 7.3. Fingerprint без библиотек (soft identifier)

**Важно:** fingerprint — это **soft identifier для anti-spam**, а не реальная идентификация пользователя. Меняется при обновлении браузера, OS, расширений; обходится через incognito + другой браузер. Логика доверия НЕ должна основываться на fingerprint напрямую — только на агрегатах `helpful_count` под отзывами и количестве independent subnets.

На фронте генерируется stable hash из:
- canvas fingerprint (`canvas.toDataURL()` хеш)
- WebGL renderer (`gl.getParameter(WebGLRenderingContext.RENDERER)`)
- timezone (`Intl.DateTimeFormat().resolvedOptions().timeZone`)
- language (`navigator.language`)
- screen resolution (`screen.width × screen.height × screen.colorDepth`)
- platform (`navigator.platform`)
- список доступных шрифтов (через canvas-измерение)

Хешируется на клиенте через SHA-256 → передаётся как `X-Fingerprint` header. На сервере дополнительно солится глобальной серверной солью перед сохранением (чтобы нельзя было reverse-engineer fingerprint конкретного человека).

**Ожидаемая стабильность:** ~70-80% юзеров сохраняют fingerprint в течение месяца. Это достаточно для anti-spam (поймать тролля который пишет 50 фейков с одного устройства), но не подходит для long-term reputation.

### 7.4. API endpoint

```
POST /api/places/{id}/reports
Headers:
  CF-Connecting-IP: ...
  CF-Turnstile-Token: ...
  X-Fingerprint: <sha256>

Body:
{
  "is_open": true|false|null,
  "price_gel": 0-200|null,
  "visit_date": "2026-04-29",
  "flags": ["paid_parking", "bad_road", ...],
  "text": "...",
  "form_fill_seconds": 47,
  "website": ""    // honeypot, должен быть пустой
}

Response 200:
{
  "status": "accepted",
  "review_id": 12345
}

Response 400/429:
{
  "error": "...code..."
}
```

### 7.5. Постобработка отзыва (асинхронно, через outbox pattern)

**Не используется LISTEN/NOTIFY.** При падении воркера на 1 GB RAM (OOM возможен) уведомления теряются. Вместо этого — outbox-паттерн через таблицу очереди.

#### 7.5.1. Таблица очереди

```sql
CREATE TABLE processing_queue (
    id BIGSERIAL PRIMARY KEY,
    job_type TEXT NOT NULL,           -- 'classify_review', 'extract_attrs', 'arbitrate_conflict'
    payload JSONB NOT NULL,
    
    status TEXT NOT NULL DEFAULT 'pending',  -- pending | processing | done | failed
    priority INT NOT NULL DEFAULT 100,        -- меньше = выше приоритет
    
    retry_count INT NOT NULL DEFAULT 0,
    max_retries INT NOT NULL DEFAULT 5,
    
    next_retry_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    locked_at TIMESTAMPTZ,
    locked_by TEXT,                   -- worker instance id (на случай нескольких)
    
    created_at TIMESTAMPTZ DEFAULT now(),
    completed_at TIMESTAMPTZ,
    error_message TEXT
);

CREATE INDEX idx_queue_pending ON processing_queue (priority, next_retry_at) 
    WHERE status = 'pending';
CREATE INDEX idx_queue_stuck ON processing_queue (locked_at) 
    WHERE status = 'processing';
```

#### 7.5.2. Алгоритм обработки

После INSERT в `place_reviews` создаётся задача в `processing_queue`:

```sql
INSERT INTO processing_queue (job_type, payload, priority)
VALUES ('classify_review', '{"review_id": 12345}', 10);
```

Воркер (отдельный процесс на VPS, ~50 MB RAM) каждые 30 секунд:

```python
def worker_tick():
    # 1. Освобождение зависших задач (locked > 5 минут назад)
    db.execute("""
        UPDATE processing_queue 
        SET status='pending', locked_at=NULL, locked_by=NULL 
        WHERE status='processing' AND locked_at < now() - interval '5 minutes'
    """)
    
    # 2. Захват пачки задач
    jobs = db.execute("""
        UPDATE processing_queue
        SET status='processing', locked_at=now(), locked_by=%s
        WHERE id IN (
            SELECT id FROM processing_queue
            WHERE status='pending' AND next_retry_at <= now()
            ORDER BY priority, next_retry_at
            LIMIT 10
            FOR UPDATE SKIP LOCKED
        )
        RETURNING id, job_type, payload
    """, (worker_id,))
    
    for job in jobs:
        try:
            handle_job(job)
            db.execute("UPDATE processing_queue SET status='done', completed_at=now() WHERE id=%s", (job.id,))
        except RateLimitedError:
            # Gemini лимит — отложить до завтра
            db.execute("""
                UPDATE processing_queue 
                SET status='pending', next_retry_at=date_trunc('day', now()) + interval '1 day 1 minute',
                    locked_at=NULL, locked_by=NULL
                WHERE id=%s
            """, (job.id,))
        except Exception as e:
            retry = job.retry_count + 1
            if retry >= job.max_retries:
                db.execute("UPDATE processing_queue SET status='failed', error_message=%s WHERE id=%s", (str(e), job.id))
            else:
                # exponential backoff
                delay = 60 * (2 ** retry)
                db.execute("""
                    UPDATE processing_queue 
                    SET status='pending', retry_count=%s, next_retry_at=now() + interval '%s seconds',
                        locked_at=NULL, locked_by=NULL, error_message=%s
                    WHERE id=%s
                """, (retry, delay, str(e), job.id))
```

#### 7.5.3. Этапы обработки отзыва

1. **Job `classify_review`** (priority 10): Gemini классифицирует `{is_spam, is_toxic, is_useful, is_relevant_to_place, language}`.
2. Если spam/toxic → `place_reviews.moderation_status='hidden'`. Запись остаётся для статистики.
3. Если useful → `moderation_status='published'`, отзыв виден на сайте.
4. Создаётся новая задача `extract_attrs` (priority 20): извлечение фактов в `place_signals` с `weight=0.2`.
5. Создаётся задача `recompute_consensus` для каждого затронутого атрибута (priority 30).

### 7.6. Голосование под отзывами

```
POST /api/reviews/{id}/vote
Headers: X-Fingerprint
Body: { "vote": 1 | -1 | 0 }

— vote=1: helpful_count++
— vote=-1: unhelpful_count++
— vote=0: flag_count++

Если flag_count >= 5: moderation_status='hidden' автоматом.
Если helpful_count >= 5: вес сигналов из этого отзыва повышается до 0.4.
```

---

## 8. API спецификация (read)

Все endpoints публичные, без аутентификации. Cloudflare кэширует с разными TTL.

### 8.0. Общие правила (versioning, error format, idempotency)

**Версионирование URL.** Все endpoints живут под префиксом `/api/v1/...` (см. [ADR-0004](adr/0004-api-versioning-v1-prefix.md)). В примерах ниже префикс может быть опущен ради краткости — реальные URL содержат `/api/v1/`. При выпуске v2 версия v1 сохраняется минимум 12 месяцев с header `Deprecation: true` + `Sunset: <RFC 9745 date>`.

**Формат ошибок.** Все 4xx/5xx ответы используют RFC 7807 ProblemDetails:

```json
{
  "type": "https://georgia-places.example/problems/rate-limit",
  "title": "Rate Limit Exceeded",
  "status": 429,
  "detail": "Превышен лимит 10 отчётов в час с одного IP.",
  "instance": "/api/v1/places/123/reports",
  "retryAfter": 3600
}
```

В .NET — `app.UseExceptionHandler()` + `IProblemDetailsService` + `Microsoft.AspNetCore.Http.HttpValidationProblemDetails` для 400.

**Idempotency-Key.** Все мутирующие POST endpoints (UGC submit, dispute, vote, click) принимают опциональный header `Idempotency-Key: <uuid>`. Сервер хранит `(idempotency_key, response_hash, created_at)` 24 часа. При повторе с тем же ключом — возврат сохранённого ответа без побочного эффекта. Без ключа — обычная обработка.

**Курсорная пагинация.** Все list-endpoints используют `?cursor=<opaque>&limit=<int>` вместо `offset`. Max `limit=100`. Response содержит `nextCursor: <string|null>` и `hasMore: bool`. Поле `total` не возвращается (COUNT(*) под нагрузкой дорогой), либо возвращается опционально по `?include=total`.

**Status codes.** Каждый endpoint в этом разделе перечисляет все возможные коды. Стандартный набор: `200` OK для GET, `201 Created` + `Location` для POST, `400` validation, `404` not found, `409` conflict (idempotency mismatch), `429` rate limit, `503` graceful degradation (внешний сервис недоступен).

### 8.1. `GET /api/places`

Список мест с фильтрацией.

```
Query:
  category=monastery,viewpoint
  bbox=lat1,lng1,lat2,lng2     # для viewport карты
  near=lat,lng                  # точка
  radius_km=10
  attrs=free:true,dogs:none,road:paved
  price_max=20
  limit=500
  
Cache-Control: public, max-age=300, s-maxage=3600
```

Response:
```json
{
  "places": [
    {
      "id": 123,
      "name": "Монастырь Икалто",
      "category": "monastery",
      "lat": 41.85,
      "lng": 45.21,
      "rating": 4.6,
      "thumbnail": "https://...",
      "key_attributes": {
        "free": true,
        "parking": true,
        "dogs": "none"
      }
    }
  ],
  "total": 47
}
```

### 8.2. `GET /api/places/{id}`

Детали места.

```
Cache-Control: public, max-age=600, s-maxage=86400
```

Response:
```json
{
  "id": 123,
  "name": "Монастырь Икалто",
  "name_en": "Ikalto Monastery",
  "description": "...",
  "category": "monastery",
  "lat": 41.85,
  "lng": 45.21,
  
  "google": {
    "rating": 4.6,
    "reviews_count": 412,
    "hours": { "monday": "09:00-18:00", ... },
    "photos": ["url1", "url2", ...]
  },
  
  "attributes": [
    {
      "key": "free",
      "value": true,
      "label": "Бесплатно",
      "icon": "🆓",
      "freshness_days": 5,
      "sources_count": 4
    },
    {
      "key": "dogs",
      "value": "none",
      "label": "Собаки",
      "value_label": "Нет",
      "icon": "🐕",
      "freshness_days": 12,
      "sources_count": 6
    }
  ],
  
  "seasonal_info": {
    "currently_likely_open": true,
    "note": null
  },
  
  "navigation_links": {
    "yandex_web": "https://yandex.ru/maps/?pt=45.21,41.85&z=15",
    "yandex_app_ios": "yandexmaps://maps.yandex.ru/?pt=45.21,41.85&z=15",
    "yandex_app_android": "yandexmaps://maps.yandex.ru/?pt=45.21,41.85&z=15",
    "google_web": "https://www.google.com/maps/search/?api=1&query=41.85,45.21",
    "google_app_ios": "comgooglemaps://?q=41.85,45.21",
    "google_app_android": "geo:41.85,45.21?q=41.85,45.21"
  },
  
  "reviews_count": 28,
  "data_freshness_score": 0.78
}
```

### 8.3. `GET /api/places/{id}/reviews`

```
Query:
  sort=helpful|recent
  limit=20
  offset=0

Cache-Control: public, max-age=120, s-maxage=600
```

Response:
```json
{
  "reviews": [
    {
      "id": 1001,
      "text": "Был вчера, всё работает, людей мало",
      "visit_date": "2026-04-28",
      "flags": ["parking_free"],
      "created_at": "2026-04-29T10:00:00Z",
      "helpful_count": 12,
      "freshness_days": 1
    }
  ],
  "total": 28
}
```

### 8.4. `GET /api/filters?category={cat}`

Динамический список фильтров для фронта.

```
Cache-Control: public, max-age=3600, s-maxage=86400
```

Response:
```json
{
  "filters": [
    {
      "key": "free",
      "label": "Бесплатно",
      "type": "bool",
      "icon": "🆓",
      "display_order": 10
    },
    {
      "key": "price_gel",
      "label": "Цена",
      "type": "range_int",
      "unit": "₾",
      "min": 0,
      "max": 50,
      "display_order": 20
    },
    {
      "key": "dogs",
      "label": "Собаки",
      "type": "enum",
      "options": [
        {"value": "none", "label": "Нет"},
        {"value": "friendly", "label": "Дружелюбные"},
        {"value": "aggressive", "label": "Агрессивные"}
      ],
      "icon": "🐕",
      "display_order": 30
    }
  ]
}
```

`min`/`max` для range считается на лету и кэшируется на час.

### 8.5. `GET /api/route/places`

Места вдоль маршрута A→B.

**Подход — упрощённый, без настоящей маршрутизации.** Не используется OSRM или другой routing engine из-за:
- Публичный OSRM имеет лимит 1 RPS и нестабилен.
- Поднимать свой OSRM — RAM не хватит на 1 GB VPS.
- Дороги Грузии (грунтовки, перевалы, паромы) requirем custom-настройки.

Юзер на фронте кликает 2-5 точек на карте (от A до B плюс желаемые промежуточные waypoints) → клиент строит **прямую ломаную** между ними → POST в API → PostGIS считает буфер вокруг ломаной → возвращает POI в этом буфере.

```
GET /api/v1/route/places
Query:
  waypoints=41.7,44.8;42.0,43.0;41.6,41.6   # ;-separated lat,lng pairs
  buffer_km=5
  categories=viewpoint,monastery,waterfall
  attrs=free:true
  cursor=<opaque>
  limit=100

Cache-Control: public, max-age=600
ETag: "<hash(query)>"
```

> Метод изменён с `POST` на `GET`: это read-операция, Cloudflare CDN кэширует только GET по умолчанию (см. ADR-0004 и REVIEW-BACKLOG P1 api-design).

Внутри:

```sql
-- ломаная из waypoints + буфер
SELECT * FROM places
WHERE category = ANY($1)
  AND ST_DWithin(
      geom::geography,
      ST_MakeLine(ARRAY[ST_MakePoint(...) ...])::geography,
      $buffer_meters
  )
  AND attributes @> $2
ORDER BY ST_Distance(...);
```

**На UI:** юзер открыл выбранное место в Я.Картах/Google Maps — пусть **они** строят настоящий маршрут с пробками и навигацией. Это не наша задача.

### 8.6. `POST /api/places/{id}/reports`

См. раздел 7.4.

### 8.7. `POST /api/places/{id}/dispute`

Кнопка "Данные неверные".

```
Headers: X-Fingerprint, CF-Turnstile-Token
Body:
{
  "attribute_keys": ["price_gel", "is_open"],
  "comment": "был сегодня, цена другая"  // optional
}
```

Создаётся сигнал типа `user_dispute` с весом 0.5 для каждого указанного атрибута.

---

## 9. Frontend требования

### 9.1. Структура страниц

| Route | Что показывается |
|-------|------------------|
| `/` | Карта Грузии с маркерами, панель фильтров слева, поиск |
| `/place/{id}` | Карточка места: фото, рейтинг, атрибуты, отзывы, форма UGC, deeplink на Я.Карты/Google |
| `/route?from=...&to=...` | Карта с маршрутом и POI в буфере, фильтры |
| `/about` | О сервисе, как работает, FAQ |

### 9.2. Карта (главная)

- MapLibre GL JS
- Тайлы: MapTiler free tier (100k запросов/мес) или OSM raster
- Кластеризация маркеров для перформанса (>1000 точек на экране)
- Цветные маркеры по категории (монастырь — фиолетовый, водопад — синий и т.д.)
- Клик на маркер → popup с превью (фото + название + рейтинг + 3 ключевых атрибута)
- Клик на "Подробнее" → переход на `/place/{id}`

### 9.3. Панель фильтров

- Загружается из `GET /api/filters`
- Динамически рендерится: bool → toggle, enum → checkboxes, range → slider
- Изменение фильтра → debounced fetch `/api/places` → обновление маркеров
- Кнопка "Сбросить фильтры"
- Состояние фильтров в URL query string (для шеринга)

### 9.4. Карточка места

- Адаптивный двухколоночный layout (на десктопе) / однокольный (мобайл)
- Фото-галерея с lazy load
- Атрибуты с иконками и пометкой свежести ("обновлено N дней назад")
- **Кнопки на Я.Карты и Google.Maps с platform-aware deeplinks:**
  - На iOS: пробует `yandexmaps://` / `comgooglemaps://`, fallback на web через таймаут 1 сек
  - На Android: пробует `geo:` intent / `yandexmaps://`, fallback на web
  - На desktop: всегда web-версия в новой вкладке
- Список отзывов с сортировкой (helpful/recent)
- Голоса под отзывами 👍/👎/🚩
- Кнопка "Был тут? Поделись" → разворачивает форму
- Кнопка "🚩 Данные неверные?" → подтверждение → POST на dispute

### 9.5. Производительность

- Initial JS bundle ≤ 250 KB gzipped
- Lazy-load карты только когда нужно
- Изображения через Cloudflare Images Transformations (бесплатный тариф)
- Lighthouse Performance ≥ 85 на mobile

### 9.6. Адаптивность

Breakpoints:
- Mobile: 320-767px (карта на весь экран, фильтры в bottom-sheet)
- Tablet: 768-1023px
- Desktop: 1024+ (карта + sidebar фильтров)

---

## 10. Инфраструктура и деплой

### 10.1. Docker Compose структура

```yaml
services:
  postgres:
    image: postgis/postgis:16-3.4-alpine
    mem_limit: 350m
    volumes:
      - pgdata:/var/lib/postgresql/data
    environment:
      POSTGRES_PASSWORD: ${DB_PASSWORD}
    command: >
      postgres
      -c shared_buffers=128MB
      -c effective_cache_size=384MB
      -c work_mem=4MB
      -c maintenance_work_mem=32MB
      -c max_connections=20
      -c wal_buffers=4MB
    restart: unless-stopped
  
  api:
    build: ./api
    mem_limit: 250m
    depends_on: [postgres]
    environment:
      ConnectionStrings__Default: "Host=postgres;..."
      Gemini__ApiKey: ${GEMINI_API_KEY}
      Cloudflare__TurnstileSecret: ${TURNSTILE_SECRET}
    restart: unless-stopped
  
  nginx:
    image: nginx:alpine
    mem_limit: 30m
    ports: ["80:80", "443:443"]
    volumes:
      - ./nginx.conf:/etc/nginx/nginx.conf:ro
      - ./certs:/etc/nginx/certs:ro
    depends_on: [api]
    restart: unless-stopped
  
  parser:
    build: ./parser
    mem_limit: 300m
    depends_on: [postgres]
    environment:
      DB_DSN: "..."
      GEMINI_API_KEY: ${GEMINI_API_KEY}
      GOOGLE_PLACES_KEY: ${GOOGLE_PLACES_KEY}
    profiles: ["manual"]    # не запускается с docker-compose up
    restart: "no"

volumes:
  pgdata:
```

### 10.2. Cron на хосте

```cron
# /etc/cron.d/georgia-places
0 1 * * *  root  /opt/scripts/backup.sh >> /var/log/backup.log 2>&1
0 3 * * *  root  cd /opt/app && docker compose run --rm parser python /app/run.py daily >> /var/log/parser.log 2>&1
0 * * * *  root  cd /opt/app && docker compose run --rm parser python /app/run.py hourly >> /var/log/parser-hourly.log 2>&1
0 4 * * 0  root  cd /opt/app && docker compose run --rm parser python /app/run.py weekly >> /var/log/parser-weekly.log 2>&1
```

### 10.3. Cloudflare конфигурация

| Feature | Настройка |
|---------|-----------|
| DNS | `domain.tld → VPS IP`, `api.domain.tld → VPS IP` (proxied) |
| SSL | Full (strict), Let's Encrypt на VPS |
| Caching | Cache Level: Standard; Edge TTL: respect origin |
| Cache Rules | URL path matches `/api/*` → Cache eligible, edge TTL 1h |
| Rate Limiting | `/api/places/*/reports`: 3/24h per IP |
| Turnstile | Site key для домена |
| Pages | Подключённый репозиторий с фронтом, авто-деплой |
| R2 | Bucket `db-backups`, lifecycle: delete >30 days |
| Email Routing | catchall на личный gmail (для регистрации в сервисах) |

### 10.4. Безопасность VPS

```bash
# SSH only by key
PasswordAuthentication no
PubkeyAuthentication yes

# fail2ban для SSH
sudo apt install fail2ban
sudo systemctl enable fail2ban

# UFW
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow 22/tcp
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw enable

# Автоупдейты безопасности
sudo apt install unattended-upgrades
sudo dpkg-reconfigure -plow unattended-upgrades

# Своп 2GB
sudo fallocate -l 2G /swapfile
sudo chmod 600 /swapfile
sudo mkswap /swapfile
sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
sudo sysctl vm.swappiness=10
```

### 10.5. Бэкап БД

**RPO = 24 часа, RTO ≈ 30 минут.** Это осознанное решение — см. [ADR-0002](adr/0002-rpo-24h-no-wal-archiving.md). Используется ежедневный `pg_dump`, без WAL archiving.

```bash
#!/bin/bash
# /opt/scripts/backup.sh
set -euo pipefail
DATE=$(date +%Y-%m-%d)
DUMP=/tmp/db-${DATE}.dump

# --format=custom: восстановление через pg_restore с параллелизмом, метаданные включены
docker compose exec -T -e PGPASSWORD="${DB_PASSWORD}" postgres \
    pg_dump -U "${DB_USER}" -d "${DB_NAME}" --format=custom --compress=9 -f /tmp/dump.bin
docker compose cp postgres:/tmp/dump.bin "${DUMP}"

rclone copy "${DUMP}" r2:db-backups/
rm "${DUMP}"
curl -fsS "https://hc-ping.com/${HEALTHCHECKS_BACKUP_UUID}"  # healthcheck
```

R2 lifecycle удаляет бэкапы старше 30 дней автоматически.

**Restore-runbook** (`/opt/scripts/restore.sh`):

```bash
#!/bin/bash
set -euo pipefail
DATE="${1:?usage: restore.sh YYYY-MM-DD}"
DUMP=/tmp/restore-${DATE}.dump

rclone copy "r2:db-backups/db-${DATE}.dump" "${DUMP}"

docker compose exec -T -e PGPASSWORD="${DB_PASSWORD}" postgres psql -U "${DB_USER}" \
    -c "DROP DATABASE IF EXISTS ${DB_NAME}_restore;" \
    -c "CREATE DATABASE ${DB_NAME}_restore;" \
    -c "\\c ${DB_NAME}_restore" \
    -c "CREATE EXTENSION IF NOT EXISTS postgis;"

docker compose cp "${DUMP}" postgres:/tmp/restore.dump
docker compose exec -T -e PGPASSWORD="${DB_PASSWORD}" postgres \
    pg_restore -U "${DB_USER}" -d "${DB_NAME}_restore" --jobs=2 /tmp/restore.dump
```

**Verify-restore** (`/opt/scripts/verify-restore.sh`, cron раз в 30 дней): берёт **последний** бэкап, восстанавливает в `${DB_NAME}_verify`, проверяет `SELECT count(*) FROM places > 0` и `SELECT count(*) FROM place_signals > 0`, дропает базу. Пингует Healthchecks. Закрывает P0 из ревью «backup без restore-протокола».

### 10.6. Observability stack

Полный стек описан в [ADR-0006](adr/0006-observability-stack.md). Краткая сводка:

| Слой | Сервис | Free tier | RAM на VPS |
|------|--------|-----------|------------|
| Logs (structured) | **Grafana Cloud Loki** | 50 GB/мес, 14 дней | 0 (push через OTLP) |
| Metrics | **Grafana Cloud Prometheus** | 10K series, 14 дней | 0 |
| Traces | **Grafana Cloud Tempo** | 50 GB/мес, 14 дней | 0 |
| Errors | **Sentry Free** | 5K errors + 50K perf events/мес, 30 дней | 0 (HTTP) |
| Cron pings | Healthchecks.io | 20 чеков | 0 |
| Synthetic | UptimeRobot | 50 мониторов, 5-мин | 0 |

**Транспорт:** OpenTelemetry SDK → OTLP HTTP push (никаких агентов на VPS).

**.NET:** `Serilog` + `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Extensions.Hosting`, `Sentry.AspNetCore`.

**Python parser:** `structlog`, `opentelemetry-sdk`, `sentry-sdk`.

**Correlation ID:** ASP.NET middleware пробрасывает `X-Correlation-Id` (либо генерирует), Serilog enriches all logs. Python parser генерирует UUID на старте каждого job-run, кладёт в `structlog.contextvars`.

**Алерты P0 (через Grafana Cloud Alerting → email + Telegram webhook):**
1. `up{job="api"} == 0 for 2m` — API down
2. `histogram_quantile(0.95, http_request_duration_seconds) > 0.5 for 5m` — p95 деградация
3. `rate(http_requests_total{status=~"5.."}[5m]) > 0.05` — 5xx > 5%
4. `gemini_low_confidence_discarded_total / gemini_calls_total > 0.4 for 1h` — Gemini тихо деградирует
5. `time() - max(parser_last_success_timestamp) > 90000` — парсер не успешен >25h
6. `processing_queue_size{status="failed"} > 100` — очередь failed копится

**Last-resort fallback:** при отказе Grafana Cloud — файловые логи на VPS (Serilog файловый sink с logrotate) остаются как backup. OTel endpoint меняется в env, ноль изменений в коде.

---

## 11. Лимиты внешних сервисов и fallback

| Сервис | Лимит free tier | Использование | Fallback при превышении |
|--------|-----------------|---------------|-------------------------|
| Gemini 2.5 Flash | 15 RPM, 1500 RPD | Классификация, извлечение, арбитраж | Groq (Llama 3.x), затем local Ollama, затем — отложить до завтра |
| Google Places | $200/мес кредита | Place Details, Reviews, Photos | Отложить, использовать только сырые рейтинги из OSM/Wikidata |
| MapTiler | 100k запросов/мес | Тайлы карты | OSM raster tiles |
| Cloudflare Pages | Безлимит | Хостинг фронта | — |
| Cloudflare CDN | Безлимит | Кэш API | — |
| Cloudflare Turnstile | Безлимит | Антибот | — |
| Cloudflare R2 | 10 GB storage, 1M Class A operations | Бэкапы | Локальные бэкапы на VPS |
| Healthchecks.io | 20 чеков free | Cron мониторинг | — |
| UptimeRobot | 50 мониторов free | HTTP пинг | — |

---

## 12. Производительность

### 12.1. Целевые метрики

| Метрика | Цель |
|---------|------|
| RPS пиковый | 50 |
| RPS средний | 5-10 |
| API response time p50 | < 100 ms |
| API response time p95 | < 500 ms |
| Cache hit ratio (CF) | > 85% |
| Lighthouse Performance (mobile) | ≥ 85 |
| Initial JS bundle | ≤ 250 KB gz |

### 12.2. Оптимизации БД

- Индексы: GIN на `places.attributes`, GIST на `places.geom`, btree на `category`, `osm_id`, `google_place_id` (все partial — `WHERE hidden = false`, см. §4.1)
- **Materialized view `places_summary`** — горячая выдача карты, обновляется раз в час с `REFRESH ... CONCURRENTLY` (не блокирует читателей)
- Connection pool: 10 в EF Core, 5 в Dapper (см. REVIEW-BACKLOG: при росте до production вынести в PgBouncer transaction mode)
- Подготовленные запросы для частых паттернов фильтрации

#### Materialized view DDL

```sql
CREATE MATERIALIZED VIEW places_summary AS
SELECT
    p.id,
    p.name,
    p.category,
    p.geom,
    p.lat AS lat,           -- денормализуем координаты для быстрых JSON-ответов
    p.lng AS lng,
    p.google_rating,
    p.google_reviews_count,
    -- денормализуем горячие фильтр-атрибуты в отдельные колонки, JSONB остаётся для долгого хвоста
    (p.attributes->>'free')::boolean AS attr_free,
    (p.attributes->>'parking')::boolean AS attr_parking,
    p.attributes->>'dogs' AS attr_dogs,
    p.attributes->>'road' AS attr_road,
    NULLIF(p.attributes->>'price_gel', '')::int AS attr_price_gel,
    -- thumbnail из Google photos
    p.google_photos->0 AS thumbnail,
    p.attributes,           -- полный JSONB для остальных фильтров
    p.last_verified_at,
    p.data_freshness_score
FROM places p
WHERE p.hidden = false;

-- УНИКАЛЬНЫЙ индекс ОБЯЗАТЕЛЕН для REFRESH CONCURRENTLY
CREATE UNIQUE INDEX idx_places_summary_id ON places_summary (id);

-- индексы для типовых запросов
CREATE INDEX idx_summary_geom ON places_summary USING GIST (geom);
CREATE INDEX idx_summary_attributes ON places_summary USING GIN (attributes);
CREATE INDEX idx_summary_category ON places_summary (category);
CREATE INDEX idx_summary_freshness ON places_summary (data_freshness_score DESC) WHERE data_freshness_score > 0.5;
```

#### Hourly refresh

Cron в 02:25, 03:25, ..., 23:25 (между парсер-job-ами):

```bash
docker compose exec -T postgres psql -U "${DB_USER}" -d "${DB_NAME}" \
    -c "REFRESH MATERIALIZED VIEW CONCURRENTLY places_summary;"
```

**Почему CONCURRENTLY:** обычный `REFRESH MATERIALIZED VIEW` берёт `ACCESS EXCLUSIVE LOCK` на view — все читающие API-запросы блокируются на время refresh (3-10 сек при 50K мест). `CONCURRENTLY` создаёт временную копию и атомарно подменяет — читатели видят либо старые данные, либо новые, никогда не блокируются. Стоимость: ~2× места на диске на время refresh, +20% времени refresh. На 50K мест укладывается в секунды, диск с запасом.

**Защита от наложения:** обёрнуть в `pg_try_advisory_lock(<hash>)` чтобы при overlap (например, ручной refresh + cron) не запустилось два параллельных refresh:

```sql
SELECT pg_try_advisory_lock(7235812);  -- константа для places_summary
-- если returned true:
REFRESH MATERIALIZED VIEW CONCURRENTLY places_summary;
SELECT pg_advisory_unlock(7235812);
```

#### Stampede protection для IMemoryCache

При истечении TTL `attributes_dictionary` несколько параллельных запросов пробивают кэш в Postgres одновременно. Решение — `SemaphoreSlim` lock в `GetOrCreateAsync`:

```csharp
private static readonly SemaphoreSlim _attrDictLock = new(1, 1);

public async Task<Dictionary<string, AttrDef>> GetAttributesDictionaryAsync(CancellationToken ct)
{
    if (_cache.TryGetValue("attr_dict", out Dictionary<string, AttrDef>? cached))
        return cached!;

    await _attrDictLock.WaitAsync(ct);
    try
    {
        // double-check после lock
        if (_cache.TryGetValue("attr_dict", out cached))
            return cached!;

        var fresh = await LoadFromDbAsync(ct);
        _cache.Set("attr_dict", fresh, TimeSpan.FromHours(1));
        return fresh;
    }
    finally { _attrDictLock.Release(); }
}
```

При 10-15 RPS на cold cache — 1 запрос идёт в БД, остальные ждут ~50ms на семафоре и читают свежий кэш.

### 12.3. Кэширование на уровне API

- `IMemoryCache` в .NET для:
  - Словарь `attributes_dictionary` (TTL 1 час)
  - min/max для range-фильтров (TTL 1 час)
  - Категории (TTL 24 часа)
- Заголовки `Cache-Control` с `s-maxage` для Cloudflare
- ETags для условных запросов

### 12.4. Деградация при перегрузке

#### 12.4.1. Cold cache scenario

Если кто-то постит сервис в большой канал (10к+ подписчиков), Cloudflare кэш ещё не прогрет — на VPS прилетают реальные 50 RPS. Один vCPU + Postgres + .NET легко не выдержит. Особенно тяжёлые `bbox`-запросы по карте.

**Защитные слои:**

1. **Cloudflare Rate Limiting (бесплатно):** 100 req/IP/min на `/api/*`. Срабатывает в первую очередь.
2. **Жёсткий Cache-Control от origin:** все GET-эндпоинты ставят `s-maxage=3600` минимум, чтобы CF держал кэш минимум час. Одинаковые bbox от тысячи юзеров → один реальный запрос на origin.
3. **Materialized view `places_summary`:** обновляется раз в час, основная выдача читается из неё, не из live `places`. Запросы быстрые даже под нагрузкой.
4. **Statement timeout 5 сек:** Postgres отбивает медленные запросы.
5. **Circuit breaker в API:** если БД отвечает >2 сек на простой ping — следующие N секунд API возвращает 503 с `Retry-After: 30` без обращения к БД. Cloudflare видит 503 → отдаёт `stale-while-revalidate` если есть.
6. **Always Online (Cloudflare free):** при полном падении origin показывает закэшированную HTML-страницу.

#### 12.4.2. Что НЕ деградирует

Даже под перегрузкой:
- UGC-форма должна работать (отзывы важны)
- Карточки уже-кэшированных мест должны открываться (CF держит)

Деградирует первым:
- Запись новых отзывов в БД (буферизуется в очередь, обрабатывается с задержкой)
- Поиск по фильтрам (можно временно показывать «попробуйте позже»)
- Обогащение через Google Places (ночной job просто пропустит итерацию)

---

## 13. Этапы разработки (MVP → полный сервис)

### Этап 1 — Базовая карта (1-2 недели)

- Postgres + PostGIS, миграции схемы
- Импорт OSM Грузии через Overpass
- .NET API: `GET /api/places`, `GET /api/places/{id}`, `GET /api/filters`
- React + MapLibre фронт с базовой фильтрацией
- Деплой Docker Compose на VPS
- Cloudflare CDN перед бэкендом
- Cloudflare Pages для фронта

**Результат:** карта Грузии с тысячами POI из OSM, простые фильтры по категории, deeplinks в Я.Карты/Google.

### Этап 2 — Обогащение данных (1 неделя)

- Wikidata SPARQL → описания и фото
- Google Places API → рейтинги, часы, отзывы (топ-1000 мест)
- Парсер на Python с cron
- Healthchecks.io мониторинг

**Результат:** карточки мест с описаниями, рейтингами, фото, часами работы.

### Этап 3 — Telegram парсинг (1-2 недели)

- HTTP-парсинг `t.me/s/<channel>` (без аккаунтов)
- Сохранение в `raw_sources`
- Gemini Flash интеграция: извлечение атрибутов
- Создание `place_signals`
- Базовая агрегация consensus

**Результат:** в карточках мест появляются те самые "нюансы" из тегов чатов.

### Этап 4 — UGC форма (1 неделя)

- Cloudflare Turnstile интеграция
- Форма отзыва на сайте, fingerprint hash
- API `POST /api/places/{id}/reports`
- LLM-классификация отзывов
- Голосование 👍/👎/🚩
- Кнопка "Данные неверные?"

**Результат:** пользователи могут анонимно дополнять данные.

### Этап 5 — Маршруты A→B (1 неделя)

- API `GET /api/v1/route/places`
- **Никакого OSRM** — см. §1.5 и §8.5: настоящий routing не используется. Юзер кликает waypoints на карте, фронт строит ломаную, бэк считает PostGIS-буфер вокруг ломаной.
- PostGIS `ST_DWithin` + `ST_MakeLine` буфер вокруг ломаной
- UI выбора 2-5 точек на карте
- На UI явный disclaimer: «это не настоящий маршрут — для навигации используйте Yandex/Google Maps по deeplink»

**Результат:** «еду из Тбилиси в Батуми, покажи интересные места по пути».

### Этап 6 — Полировка автономии (постоянно)

- Anomaly detection (burst, copypaste, single source)
- LLM-арбитраж конфликтов
- Time-decay nightly recompute
- Self-healing через dispute

**Результат:** система работает без вмешательства месяцами.

---

## 14. Что НЕ делается (явные исключения)

Чтобы не разрастаться:

- ❌ Регистрация, логин, OAuth, профили
- ❌ Email, SMS, push-уведомления
- ❌ Telegram-бот (отложено, может появиться позже)
- ❌ Платные функции, премиум, подписки
- ❌ Баннерная реклама (AdSense, Yandex.Direct и т.д.)
- ❌ Мобильное приложение
- ❌ Многоязычность (только русский в v1)
- ❌ Собственная маршрутизация и навигация
- ❌ Собственная система бронирования / билетов / оплат
- ❌ Социальные функции (фоллоу, чаты)
- ❌ Ручная модерация, дашборд админа
- ❌ Метрики/аналитика поведения юзеров
- ❌ A/B тесты
- ❌ Свой tile server для карт
- ❌ Парсинг через Telegram MTProto / Telethon

---

## 15. Юридические аспекты

### 15.1. Персональные данные

Сервис не собирает PII (Personally Identifiable Information):
- Нет регистрации, имён, email
- IP и fingerprint хешируются с солью, оригиналы не хранятся
- Все хеши — анонимные технические идентификаторы

В footer достаточно: «Сервис не собирает персональных данных. Отзывы публикуются анонимно».

### 15.2. Авторские права на данные

| Источник | Лицензия | Атрибуция |
|----------|----------|-----------|
| OpenStreetMap | ODbL | "© OpenStreetMap contributors" в footer |
| Wikidata | CC0 | По желанию |
| Google Places | Google ToS | Логотип "Powered by Google" рядом с фото/рейтингом |
| MapTiler | Free tier ToS | Атрибуция MapTiler на карте |

### 15.3. UGC и DMCA

Любой пользователь может пожаловаться на отзыв через `🚩 flag` или email указанный в footer. Через автомеханизм (5+ flags) отзыв скрывается. Owner оставляет за собой право удалить любой контент по жалобе правообладателя.

---

## 16. Открытые вопросы (на момент старта)

- [ ] Точный домен и хостинг VPS (выбор провайдера)
- [ ] Список финальных категорий мест (ограничить ~15-20 базовых)
- [ ] Финальный список Telegram-каналов для парсинга (стартовый — 10-15 шт)
- [ ] Логотип, бренд, копирайт на сайте
- [ ] Email для DMCA-жалоб (можно через Cloudflare Routing на личный)
- [ ] Промо-стратегия: где анонсировать (TG-каналы про Грузию, форумы, Reddit)

---

## 17. Монетизация

### 17.1. Принципы

- **Цель:** окупать инфраструктуру (~$10/мес) и иметь небольшой бонус сверху.
- **Не цель:** превратить сервис в источник дохода уровня зарплаты — это другой продукт.
- **Без подписок, регистраций, биллинга** — ничего что усложнит систему.
- **Без баннерной рекламы** (AdSense / Yandex.Direct) — портит UX, мало приносит, замедляет фронт.
- **Партнёрские ссылки + донаты** — как органичная часть UX, не как реклама.

### 17.2. Реалистичные ожидания

При 2000 уникальных юзеров/мес (достижимо за 3-6 мес активного промо):

| Источник | Ожидаемо $/мес |
|----------|----------------|
| Booking.com (отели) | $2-5 |
| GetYourGuide / Tripster (туры) | $5-15 |
| LocalRent (аренда авто) | $5-15 |
| Donations (BMaC / Boosty) | $0-15 |
| **Итого** | **$15-50** |

При 10 000 юзеров/мес — ориентир $100-300. Это подработка, не основной доход.

### 17.3. Affiliate-партнёрки

**Агрегатор:** [Travelpayouts](https://travelpayouts.com) — закрывает большинство тревел-партнёрок одним кабинетом, выплаты на карту/PayPal/USDT.

| Партнёр | Категория | Комиссия | Cookie |
|---------|-----------|----------|--------|
| Booking.com | Отели | 25% от их комиссии (~4% от брони) | 30 дней |
| GetYourGuide | Туры, экскурсии | 8% | 30 дней |
| Viator | Туры | 8% | 30 дней |
| Tripster | Экскурсии в Грузии (русскоязычные) | ~10% | 30 дней |
| LocalRent | Аренда авто (специализация по Грузии) | ~30-50₾ за бронь | 90 дней |
| GetRentACar | Аренда авто | аналогично | — |
| Cherehapa / EKTA | Туристическая страховка | $2-5 фикс | 30 дней |

### 17.4. UI размещение партнёрских ссылок

**Карточка места — блок "Полезное рядом":**

```
┌─────────────────────────────────────┐
│ 🏛️ Монастырь Икалто                 │
│ ⭐ 4.6 ...                           │
│ [атрибуты, отзывы]                  │
├─────────────────────────────────────┤
│ 💡 Полезное рядом:                  │
│   🏨 Отели в Кахетии — Booking      │
│   🍷 Винные туры по Кахетии — GYG   │
│   🚗 Аренда авто в Грузии — LocalRent│
└─────────────────────────────────────┘
```

**Страница маршрута — блок над/под картой:**

```
┌─────────────────────────────────────┐
│ Маршрут Тбилиси → Батуми            │
│ [карта с POI вдоль маршрута]        │
├─────────────────────────────────────┤
│ 🚗 Аренда авто для роуд-трипа       │
│ 🏨 Отели на маршруте                │
│ 🛡 Страховка на поездку             │
└─────────────────────────────────────┘
```

**Footer:**

```
💛 Сервис бесплатный. Поддержать проект → [Buy Me a Coffee]
```

### 17.5. Автогенерация ссылок

Никакой ручной работы по добавлению партнёрских ссылок к каждому месту:

```python
def affiliate_links_for_place(place):
    region = detect_region(place.lat, place.lng)
    # 'kakheti', 'adjara', 'tbilisi', 'svaneti', 'samtskhe-javakheti', ...
    
    links = []
    
    # Booking — всегда
    links.append({
        "label": f"Отели в регионе {region.name_ru}",
        "url": booking_aff_url(region=region.booking_id, lang='ru'),
        "icon": "🏨",
        "partner": "booking"
    })
    
    # GetYourGuide — для туристических категорий
    if place.category in ['monastery', 'viewpoint', 'museum', 'fortress', 'wine_region']:
        links.append({
            "label": f"Туры и экскурсии — {region.name_ru}",
            "url": gyg_aff_url(query=region.name_en),
            "icon": "🎫",
            "partner": "gyg"
        })
    
    # LocalRent — для удалённых мест (где нужна машина)
    if place.requires_car or region.remote:
        links.append({
            "label": "Аренда авто в Грузии",
            "url": localrent_aff_url(country='georgia'),
            "icon": "🚗",
            "partner": "localrent"
        })
    
    return links
```

Регионы Грузии и их соответствие ID в системах партнёров — в конфиге, обновляется раз в год.

### 17.6. Отслеживание (опционально)

Для понимания что работает а что нет. **Без аналитики поведения юзеров**, только агрегированные клики:

```sql
CREATE TABLE affiliate_clicks (
    id BIGSERIAL PRIMARY KEY,
    place_id BIGINT,
    partner TEXT,
    region TEXT,
    fingerprint_hash TEXT,  -- для уникальности, не для tracking
    clicked_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX ON affiliate_clicks (partner, clicked_at);
```

Endpoint:
```
GET /api/aff/redirect?p=booking&place_id=123
→ записывает клик
→ 302 редирект на партнёрскую ссылку с aff_id
```

Конверсии и заработок отслеживаются в кабинете Travelpayouts, не своими силами. Своя таблица — только для того чтобы понять "из каких мест чаще кликают".

### 17.7. Donations — Buy Me a Coffee / Boosty

| Платформа | Аудитория | Комиссия | Сложность |
|-----------|-----------|----------|-----------|
| Buy Me a Coffee | Международная | ~5% | 5 минут настройки |
| Boosty | Русскоязычная (РФ) | 8% | 10 минут |
| Ko-fi | Международная | 0% (premium $6/мес для меньшей комиссии) | 5 минут |
| TON / crypto | Все | 0% | 15 минут |

**Размещение:**
- Тихая ссылка в footer всех страниц
- Маленький блок на странице `/about`
- Никаких pop-up'ов, прерываний, "поддержи нас!" в навигации

### 17.8. Платные размещения от локальных бизнесов (отложено)

**Не делается на старте.** Возможно в будущем когда трафик стабилизируется и появятся запросы:

- "Спонсорский маркер" на карте — выделенное место + расширенная карточка
- Цена ориентир: $10-30/мес за место, $50 разово за добавление с фото

Требует:
- Регистрации как ИП / самозанятый
- Выставления счетов
- Поддержки клиентов
- Чёткой маркировки `sponsored` в UI

Это уже работа, не пассив. На горизонте 6-12 месяцев работы сервиса, не раньше.

### 17.9. Юридические нюансы

- **Партнёрские ссылки и донаты не требуют регистрации ИП** на старте — это просто личный доход, налоги по своей юрисдикции
- В footer честно указать: «Сервис содержит партнёрские ссылки. По ним мы получаем небольшую комиссию, для вас цена не меняется.»
- Партнёры (Travelpayouts, Booking) сами выдают чеки/документы для налогов

### 17.10. Что НЕ делать в монетизации

- ❌ Pop-up'ы с просьбой задонатить
- ❌ Закрытие части функционала за пейволлом
- ❌ Sponsored content без чёткой маркировки
- ❌ Sticky-баннеры, видео-реклама, intermission рекламы
- ❌ Email-рассылки (требуют подписки = персданные = ломает концепцию)
- ❌ "Премиум-аккаунт" (требует регистрации = ломает концепцию)
- ❌ Продажа данных юзеров (нечего продавать, и репутация важнее)

### 17.11. Этапы внедрения монетизации

**Этап 1 — сразу с MVP:**
- Регистрация в Travelpayouts
- Регистрация в Buy Me a Coffee / Boosty
- Footer с donation-ссылкой
- Блок "Полезное рядом" на карточке места (Booking + 1-2 партнёра)

**Этап 2 — после первой 1000 юзеров:**
- Расширение списка партнёров (туры, страховки)
- Endpoint `/aff/redirect` для отслеживания популярности

**Этап 3 — после 10k юзеров (если случится):**
- Возможно — спонсорские размещения для локальных бизнесов
- Возможно — премиум-фичи через одноразовые платежи (без регистрации)

---

## 18. Метрики успеха MVP

Через 3 месяца после запуска:

- 10 000+ POI в БД с базовыми атрибутами
- 1 000+ мест с обогащением через Google/Wikidata
- 100+ UGC отзывов
- Стабильная работа парсера (≥ 95% успешных запусков по Healthchecks)
- Uptime API ≥ 99% по UptimeRobot
- Месячная стоимость инфры ≤ $10 (только VPS)
- Время ручной работы владельца ≤ 30 минут / месяц
- Доход от монетизации ≥ $15/мес (покрывает инфру)

---

## 19. Пограничные случаи и их обработка

Реальные сценарии которые могут сломать систему. Каждый кейс — описание + поведение системы + ручная процедура (если применимо).

### 19.1. Cold start — пустая БД, нет UGC

**Сценарий:** сервис только запустился. UGC-сигналов нет. Если применять consensus с порогом «3+ сигнала» — ни один факт не попадёт в `places.attributes`.

**Поведение:**
- Сигналы Tier-1 (Google Places, Wikidata, OSM) применяются по одному, без consensus.
- Сигналы Tier-2 (Telegram extract с confidence ≥ 0.7) применяются по одному.
- Consensus с min_signals ≥ 3 действует **только для Tier-3 (UGC)**.
- На UI: атрибуты с одним источником помечаются «по данным Google» / «упомянуто в Telegram», без претензии на «подтверждено сообществом».

### 19.2. Cold cache — сервис только что упомянули в большом канале

**Сценарий:** пост в канале с 50к подписчиков. За 10 минут — 5000 уникальных юзеров заходят на сайт. Cloudflare кэш ещё не построен. На VPS прилетают реальные ~50 RPS.

**Поведение:**
- Cloudflare Rate Limit ловит 95% (100 req/IP/min — для одинаковых юзеров).
- Statement timeout в Postgres отбивает медленные запросы.
- Если БД отвечает >2 сек — API возвращает 503 с `Retry-After`.
- Cloudflare кэширует первые ответы (даже 503) → отдаёт их следующим юзерам.
- Через 5-10 минут кэш полностью прогрет, нагрузка возвращается в норму.
- В худшем случае — первые юзеры видят «попробуйте через минуту», остальные нормальную страницу.

**Ручная процедура:** не требуется. Если упало навсегда — посмотреть логи, нет ли OOM.

### 19.3. Парсер сломался (изменилась HTML-разметка t.me/s/)

**Сценарий:** Telegram обновил вёрстку. Парсер извлекает 0 постов из всех каналов.

**Поведение:**
- При 0 постов на первой странице любого канала — алерт через Healthchecks: `?fail=structure_changed`.
- Sigma-канал в Healthchecks → email-алерт на личный email.
- Парсер продолжает работать с другими источниками (OSM, Google, Wikidata) — данные продолжают пополняться, просто без свежих TG-нюансов.

**Ручная процедура:** ~2 часа. Открыть `t.me/s/durov`, посмотреть новый HTML, обновить CSS-селекторы в `parser/sources/telegram.py`, протестировать локально, задеплоить.

### 19.4. Gemini Flash отключили / превысили дневной лимит

**Сценарий:** Google задеприкейтил Gemini 2.5 Flash, или сервис превысил 1500 RPD.

**Поведение:**
- Centralized rate-limiter не пускает запросы → возвращает `RateLimitedError`.
- Worker откладывает задачу в `processing_queue` с `next_retry_at = tomorrow 00:01 UTC`.
- Если ошибка не «лимит», а «модель отключена» — fallback chain:
  1. Groq Llama 3.x (бесплатный тариф, ~30 RPM)
  2. OpenRouter free models (DeepSeek, Llama)
  3. Локальный Ollama на машине разработчика (только для batch-обработки)
- Очередь задач не теряется. После починки fallback — обрабатывается с накопленным.

**Ручная процедура:** ~30 минут. В `appsettings.json` поменять `Gemini.Model` на новый или включить fallback. Задеплоить.

### 19.5. DDoS на UGC endpoint

**Сценарий:** кто-то целенаправленно валит 1000 фейковых отчётов в час с разных IP через VPN-сеть.

**Поведение:**
- Cloudflare WAF блокирует по rate limit (3/IP/24ч на одно место).
- Глобальный лимит 300 reports/час не даёт переполнить очередь.
- При срабатывании burst-detection (5.3) — все сигналы периода помечаются `excluded_from_consensus`.
- Атрибуты места **не меняются**, потому что при аномалии consensus не пересчитывается.
- Тролль увидит «отзыв принят», но данные на UI не дрогнут.
- В фоне Gemini классифицирует отзывы как `is_spam=true` → `moderation_status='hidden'`, отзывы не показываются.

**Ручная процедура:** не требуется в норме. Если атака продолжается долго — можно временно поднять Turnstile difficulty в Cloudflare dashboard.

### 19.6. Координированная атака на одно место

**Сценарий:** конкурент аквапарка нанял 20 человек написать «закрыто навсегда», все настоящие, разные IP, разные fingerprint.

**Поведение:**
- Сигналы прошли все защиты (LLM не классифицирует как спам — текст осмысленный).
- Anomaly detection ловит burst (>10 сигналов на одно место за час) → `excluded_from_consensus`.
- Если атака размазана по дням (5/день) — anomaly не срабатывает, сигналы накапливаются.
- Через несколько дней consensus достигнут → атрибут `is_open` сбрасывается в `false`.
- НО: Google Places `currentOpeningHours` всё ещё показывает «работает» → `google_override` побеждает UGC, на UI показывается «работает» с пометкой «несколько посетителей сообщили о закрытии — уточняйте».
- Реальные посетители аквапарка через несколько дней пишут «работаем» → consensus переворачивается обратно через decay.

**Ручная процедура:** не требуется. Самовосстановление работает. Если важное место (туристический хайлайт) — можно вручную пометить `attribute_sources.is_open.locked=true` через прямой SQL чтобы UGC не мог это менять.

### 19.7. Конкурент массово даёт dispute («данные неверные»)

**Сценарий:** 50 disputes за неделю на цены конкретного ресторана (хочет утопить).

**Поведение:**
- Burst-detection ловит >5 disputes/час → `excluded_from_consensus`.
- Размазанные disputes (7/день) — попадают под consensus.
- Атрибут не сбрасывается сразу: только при ≥5 dispute от ≥3 подсетей за >7 дней.
- UI показывает «несколько посетителей сообщили о расхождении», но цена видна.
- При сбросе в `null` — на UI «уточняется», что не вредит сильно.

**Ручная процедура:** не требуется. Если стало совсем плохо — `attribute_sources.locked=true`.

### 19.8. Семья из 4 человек хочет оставить 4 отзыва с одного NAT IP

**Сценарий:** отдыхающие на одном Wi-Fi, все хотят написать.

**Поведение:**
- Cloudflare лимит **3/IP/24ч на одно место** + **10/IP/24ч на разные места**.
- Если все четверо пишут про одно место — четвёртый получит «попробуйте завтра». Не идеально, но приемлемо.
- Если про разные места — все 4 пройдут.
- Fingerprint у разных устройств разные → fingerprint-лимит не мешает.

**Ручная процедура:** не требуется. Если прилетит много жалоб «не даёт оставить отзыв» — поднять лимит до 5/IP/место.

### 19.9. Спорные территории (Абхазия, Южная Осетия)

**Сценарий:** на карте по данным OSM эти территории показаны как часть Грузии. Пользователи из РФ или Осетии могут жаловаться.

**Поведение:**
- В footer пометка: «Карта строится на данных OpenStreetMap. Сервис не делает политических утверждений о статусе территорий».
- POI оттуда **попадают в выдачу**, потому что это просто данные OSM.
- При жалобах от юзеров — отвечать стандартным текстом про OSM, не вступать в дискуссию.

**Ручная процедура:** ad-hoc, по факту жалобы. Удалять POI **не нужно**.

### 19.10. Запрос маршрута A→B через грузинский перевал зимой

**Сценарий:** юзер построил ломаную Тбилиси → Степанцминда → Кутаиси через Крестовый перевал. Перевал зимой закрыт.

**Поведение:**
- Сервис **не строит** настоящий маршрут — только показывает POI вдоль ломаной.
- На UI рекомендация: «откройте в Google Maps для актуального маршрута».
- Google Maps сам учтёт закрытие.

**Ручная процедура:** не требуется. Это сознательное упрощение.

### 19.11. Сезонное место отображается как «открыто» в неправильное время

**Сценарий:** Гудаури в апреле — статус неоднозначный. Сезонный паттерн `[12,1,2,3]` устарел (потепление).

**Поведение:**
- Сезонный паттерн — это **подсказка**, не факт. Не используется для жёсткого фильтра «открыто сейчас».
- На UI: «обычно работает в декабре-марте, проверяйте перед поездкой».
- Если есть свежий UGC сигнал «работает в апреле» с consensus → перебивает паттерн.
- Если Google Places показывает «закрыто» — побеждает Google.

**Ручная процедура:** не требуется. Худшее что может быть — юзер прочитает «обычно открыто» и поедет проверять.

### 19.12. Парсер работает дольше ожидаемого, пересекается с пиковым трафиком

**Сценарий:** парсер запустился в 03:00, но из-за тормозов Gemini API дотянул до 09:00 — а в 09:00 у тебя пиковая аудитория.

**Поведение:**
- Парсер пишет в **отдельную staging-схему** (`places_staging`, `signals_staging`).
- Транзакционный merge в основные таблицы происходит атомарно в самом конце.
- API всё это время читает с прежней схемы — нагрузка на БД от парсера минимальна (только INSERT в staging).
- Hard kill через `cron` в 07:00 если процесс ещё жив (сохранив прогресс в staging).
- На следующий запуск merge докатывается.

**Ручная процедура:** не требуется. Если регулярно не успевает — уменьшить `max_items_per_run` в DAILY_BUDGETS.

### 19.13. Postgres OOM на VPS 1 GB

**Сценарий:** случился запрос который сожрал больше памяти чем доступно. OOM killer убил Postgres.

**Поведение:**
- Docker `restart: unless-stopped` поднимает контейнер обратно через 10 секунд.
- Свопа 2 GB защищает от OOM в первую очередь — система притормозит, не упадёт.
- Healthchecks замечает что бэкап не сделался → email.
- UptimeRobot пингует `/health` — увидит что вернулось.

**Ручная процедура:** при повторении — посмотреть `pg_log` на тяжёлый запрос, добавить индекс или `LIMIT`.

### 19.14. Cloudflare заблокировал страну где живёт ключевой пользователь

**Сценарий:** маловероятно, но возможно — например внезапные санкции.

**Поведение:**
- Cloudflare имеет gating по странам, но free tier обычно не блокирует.
- Если случилось — VPS остаётся доступен напрямую через `vps-ip:443` (с тем же SSL сертификатом).
- На фронте можно добавить fallback URL в коде.

**Ручная процедура:** ad-hoc. Скорее всего ничего делать не нужно.

### 19.15. Юзер пишет отзыв по-английски / на грузинском

**Сценарий:** русскоязычный сервис, но пишут на других языках.

**Поведение:**
- Gemini классифицирует `language` в результате.
- Сейчас (v1) русские отзывы показываются всегда, нерусские помечаются `moderation_status='hidden'` (полностью или показываются в отдельной секции).
- Извлечение фактов работает с любого языка (Gemini multilingual), факты применяются.

**Ручная процедура:** не требуется.

### 19.16. DMCA-жалоба от владельца места

**Сценарий:** ресторан требует удалить негативные отзывы / убрать само место с карты.

**Поведение:**
- Email указанный в footer (DMCA contact) идёт через Cloudflare Email Routing на личный gmail.
- При получении жалобы — посмотреть, удалить контент через прямой SQL (`UPDATE place_reviews SET moderation_status='hidden' WHERE id=...`) или скрыть место (`UPDATE places SET hidden=true WHERE id=...`).

**Ручная процедура:** ~30 минут на жалобу.

### 19.17. Бесплатный тариф Cloudflare обрезали / ограничили

**Сценарий:** Cloudflare убрал что-то из free tier (Pages, Turnstile, R2 — гипотетически).

**Поведение:**
- Pages → можно мигрировать на Vercel / Netlify / GitHub Pages.
- Turnstile → hCaptcha free / Google reCAPTCHA v3 free.
- R2 → backblaze B2 (10 GB free), или локальные бэкапы на VPS.

**Ручная процедура:** ad-hoc, миграция занимает несколько часов на компонент.

### 19.18. Купленный домен истекает

**Сценарий:** забыл продлить.

**Поведение:**
- Cloudflare emails о приближении истечения за 30, 14, 7, 1 день.
- При истечении — сайт лежит, юзеры не видят ничего.

**Ручная процедура:** включить auto-renew на этапе настройки. Если всё же забыл — продлить как можно быстрее, домен можно вернуть в течение 30 дней grace period.

### 19.19. Огромный запрос на /api/places (bbox = вся Земля)

**Сценарий:** юзер или бот делает запрос с `bbox` покрывающим все 50 000 мест.

**Поведение:**
- На API стоит ограничение `LIMIT 500` в SQL, отдаётся 500 ближайших к центру bbox.
- На фронте кластеризация маркеров — даже 500 точек рендерятся быстро.
- Cloudflare кэширует по `bbox` параметрам — повторный запрос идёт из кэша.

**Ручная процедура:** не требуется.

### 19.20. Конфликт между Google hours и UGC «работает 24/7»

**Сценарий:** Google Places говорит часы 09:00-18:00, юзеры пишут «открыто 24 часа».

**Поведение:**
- LLM-арбитраж вызывается при конфликте, видит оба сигнала.
- Авторитет Google Places выше → побеждает 09:00-18:00.
- На UI: рядом с часами помета «возможно изменилось — N посетителей сообщили о других часах».

**Ручная процедура:** не требуется. Если место реально работает 24/7 а Google не обновился — это проблема Google, не наша.

### 19.21. Парсер успешно завершается, но импортирует 0 записей (тихий отказ)

**Сценарий:** парсер запустился, отработал 5 секунд, отчитался успехом в Healthchecks. Но фактически из всех каналов 0 постов извлечено (изменилась разметка / каналы умерли / API сломался). Через месяц БД не пополняется, ты не замечаешь.

**Поведение:**
- Каждый job парсера записывает в `parser_runs` поля `items_processed`, `items_failed`.
- После завершения ночного run'а — отдельная health-check метрика «продуктивность за 7 дней».
- Если за 7 дней `SUM(items_processed) < THRESHOLD` (например <100 для tg_fetch) — посылается **отдельный** алерт в Healthchecks с UUID типа `parser-productivity-low`.
- Алерт не «парсер упал» (он не упал), а **«парсер живой но бесплодный»**.

**Ручная процедура:** ~1-2 часа. Зайти в `parser_runs`, посмотреть какой job плохой, починить.

### 19.22. Длительное отсутствие владельца (3+ месяцев)

**Сценарий:** ты ушёл в отпуск / не можешь следить / просто забил. Что происходит с сервисом?

**Поведение в порядке деградации:**
- **0-30 дней:** всё работает идеально. Auto-renew продлевает домен и VPS.
- **30-90 дней:** парсер может тихо отвалиться, никто не починит → данные перестают свежеть. На UI всё ещё показываются старые данные с пометкой «обновлено N дней назад» — юзеры видят что данные старые, но сервис работает.
- **90-180 дней:** Gemini может deprecate модель → парсер падает с ошибкой → перестаёт извлекать атрибуты из новых отзывов. Старые данные decay → атрибуты переходят в `null` (`unknown`). UI показывает «данные уточняются».
- **180+ дней:** Telegram-парсинг может быть мёртв давно. UGC через сайт ещё может работать. Сервис превращается в «статическую карту POI» — всё ещё полезный, но без актуализации нюансов.
- **Год без владельца:** карта работает на накопленных данных + Google Places (через автообновление если ключ ещё активен). Юзеры начинают жаловаться на устаревшие данные. Сервис **деградирует, но не падает**.

**Архитектурное решение для этого:**
- Все ключи API хранятся в `.env` на VPS, **не в облаке** — VPS-провайдер не может их отозвать
- Auto-renew **везде где есть** (домен, VPS, SSL через Let's Encrypt, Cloudflare)
- При невозможности обновления данных — UI явно показывает «возможно устарело»
- **Никаких функций которые ломаются если не обновлять** (например, никакого «токена авторизации» который протухнет)

**Ручная процедура:** через 1-3 года при возвращении — починить парсер за полдня, данные начнут поступать снова.

### 19.23. Внезапный рост популярности (2000% за неделю)

**Сценарий:** какой-то блогер с 500к подписчиков сделал обзор, пиковая нагрузка прыгнула в 50 раз.

**Поведение:**
- Cloudflare на free tier всё равно работает на безлимитном трафике в кэше.
- Если 90% запросов кэшируются — VPS получает только 10% реальных hit'ов.
- При перегрузке VPS → 503 → Cloudflare показывает закэшированную версию через Always Online.
- **Бесплатные тиры внешних API** могут стать узким горлом:
  - Google Places — упрётся в $200 кредит → автоматический fallback на «без обогащения» (только OSM данные)
  - Gemini — упрётся в RPD → задачи откладываются на завтра, новые UGC проходят без LLM-классификации (записываются с `moderation_status='pending'`)
  - MapTiler — упрётся в 100k → fallback на OSM raster
- Партнёрские клики (affiliate) **взлетают** → доходы покрывают возможный апгрейд VPS

**Ручная процедура:** в идеале — посмотреть на VPS-нагрузку, опционально апгрейдить до 2 GB RAM ($5-10 → $10-20). Не критично.

### 19.24. Email-инбокс DMCA-жалоб переполнен или ты не читаешь почту

**Сценарий:** прилетела DMCA, ты не отвечаешь 30 дней — могут эскалировать жалобу хостингу, домен заблокируют.

**Поведение:**
- Email на `dmca@домен` через Cloudflare Email Routing → твой gmail
- **Автоответ-бот в Cloudflare Email Workers** (бесплатно): при получении письма с темой содержащей `[DMCA]` или `complaint` → автоответ «Получили, рассмотрим в течение 7 дней. Для срочных случаев укажите URL контента в `body`»
- Содержимое письма + sender пишутся в БД (через простой Cloudflare Worker → API endpoint) → ты видишь все жалобы на дашборде
- В UI на странице `/about` — публичный flag-механизм: «🚩 Сообщить о нарушении» с автоматическим скрытием при 5+ жалобах от разных IP, **не требующий твоего вмешательства**

**Ручная процедура:** ~10 минут раз в 2 недели на проверку списка жалоб. Если уезжаешь надолго — поставить статичную страницу `/dmca` с инструкцией flag-кнопок и автоматическим скрытием.

### 19.25. SSH-взлом VPS

**Сценарий:** кто-то получил доступ к серверу.

**Поведение:**
- Ключи API в `.env` могут быть украдены → нужно их ротировать
- БД может быть изменена (вставлен мусор / удалены записи)
- Trip-wire: **раз в час cron'ом** считается контрольная сумма ключевых таблиц (`places.count()`, `place_reviews.count()`) и пишется в `system_health`. При резком падении → email-алерт.
- Бэкапы на R2 шифруются клиентским ключом (gpg) — взломщик не может их подменить
- На VPS только service-аккаунты с минимальными правами; root доступ только по ssh-key, fail2ban банит после 3 попыток
- НЕТ доступа к продакшну с windows/обычной машины — только с зашифрованного диска
- Никаких credentials в git-репе (даже приватной)

**Ручная процедура:** при подозрении — пересоздать VPS с нуля (15 минут на новый инстанс), накатить из git+бэкапа, ротировать все API-ключи.

### 19.26. Кто-то forkнул сервис и поднял клон

**Сценарий:** проект open-source / код увидели → кто-то поднял свой `georgia-places-2.com` с теми же данными OSM.

**Поведение:**
- Это **нормально** для open-source — лицензия позволяет.
- Главное чтобы атрибуция OSM/Google была сохранена.
- Конкуренция на основе данных малозначима — данные всё равно свободные.
- Конкурентное преимущество — **UGC сообщество и доверие**, его нельзя скопировать.

**Ручная процедура:** не требуется. Можно отметить в footer «оригинальный сервис на домене X».

### 19.27. Cloudflare заблокировал/удалил аккаунт

**Сценарий:** Cloudflare решил что сервис нарушает их ToS (например из-за UGC-контента).

**Поведение:**
- VPS остаётся доступным напрямую по IP
- Можно мигрировать на bunny.net CDN (~$1/мес pay-as-you-go) или поднять свой Nginx-кэш
- Turnstile → hCaptcha
- Pages → Vercel / Netlify
- R2 → Backblaze B2

**Ручная процедура:** ~1 день на миграцию всех компонентов. Шанс события низкий, но риск концентрации в одном провайдере существует.

### 19.28. Юзер заявляет «я хочу удалить свои отзывы» (хотя их нет персональных данных)

**Сценарий:** письмо «удалите все мои отзывы» — но ты не знаешь какие отзывы его, потому что нет аккаунтов.

**Поведение:**
- В UI footer'е честно: «Сервис не идентифицирует пользователей. Для удаления конкретного отзыва — укажите URL и используйте кнопку 🚩».
- 5+ flag'ов на отзыв → автоскрытие.
- Если юзер прислал URL конкретного отзыва — можно скрыть руками SQL (`UPDATE place_reviews SET moderation_status='hidden' WHERE id=...`).
- Юзер не может «удалить все свои отзывы» технически — нет привязки. Это документированное ограничение анонимности.

**Ручная процедура:** ~2 минуты на конкретный запрос с URL. Без URL — невозможно, отвечаем стандартным текстом.

---

## 20. Архитектура автономии: «оно само живёт»

Раздел собирает всё что относится к минимизации ручной работы владельца. Цель: **владелец не должен делать ничего месяцами**, кроме оплаты домена/VPS (через auto-renew). Сервис должен работать сам.

### 20.1. Принципы автономии

1. **Деградация лучше остановки.** Если что-то не работает — система продолжает работать с деградацией, не падает.
2. **При сомнении — не делать ничего.** Лучше пустое поле в данных чем неправильное.
3. **Никаких ручных подтверждений.** Никаких очередей «на ручную модерацию».
4. **Самовосстановление времени.** Старые ошибки сами устаревают и исчезают.
5. **Прозрачность вместо точности.** На UI явно показывается свежесть и источник данных, юзер сам решает доверять или нет.

### 20.2. Уровни деградации

При отказе любой части системы — следующий уровень включается автоматически:

| Что отказало | Уровень деградации | Что видит юзер |
|--------------|---------------------|----------------|
| UGC форма | LLM-классификация недоступна | Отзывы публикуются с `pending` без классификации, через 24ч авто-публикуются если не было flag'ов |
| Gemini API | Парсинг новых отзывов | Старые данные продолжают показываться, новые UGC пишутся в pending |
| Google Places | Нет обновления часов работы | Часы работы помечены как «возможно устарело», UGC данные в приоритете |
| Telegram парсинг | Нет свежих нюансов | Существующие нюансы остаются, помечены свежестью |
| OSM Overpass | Нет новых POI | Существующие 50 000+ мест продолжают работать |
| Postgres | API возвращает 503 | Cloudflare Always Online показывает закэшированную версию |
| VPS целиком | Сервис недоступен | Cloudflare Always Online показывает кэш до 30 дней |
| Cloudflare | Прямой доступ к VPS по IP | Юзеры могут зайти, но без кэша/защиты |

### 20.3. Self-healing мониторинг

Метрики которые система **сама о себе** считает и корректирует:

```sql
CREATE TABLE system_health (
    metric_key TEXT PRIMARY KEY,
    value JSONB,
    updated_at TIMESTAMPTZ DEFAULT now()
);
```

Каждый час cron записывает:

| Метрика | Значение | Action при отклонении |
|---------|----------|----------------------|
| `places_count` | total POI в БД | -10% за час → trip-wire алерт |
| `signals_per_day_avg` | сигналов в среднем за неделю | <50% от baseline → email |
| `parser_last_success` | timestamp последнего успешного run | >48ч → email |
| `gemini_quota_used` | % дневного лимита | >80% → притормозить low-priority задачи |
| `google_quota_used` | $ от $200 кредита | >70% → переключить на economy mode |
| `cloudflare_cache_hit_ratio` | % кэш-хитов | <70% → возможно проблема с заголовками |
| `db_size_mb` | размер БД | резкий рост → возможен спам |
| `ugc_per_day` | отчётов в день | x10 от среднего → возможна атака |

**Все алерты идут в один email**, не больше 1 в сутки (digest), кроме критических (БД/VPS down).

### 20.4. Auto-renew и продление

| Что | Как продлевается |
|-----|------------------|
| Домен | Auto-renew через регистратора (карта привязана) |
| VPS | Auto-renew через провайдера (карта привязана) |
| SSL сертификат | Let's Encrypt + certbot cron `0 3 1 * *` |
| Cloudflare | Free tier, без продлений |
| Healthchecks.io | Free tier, без продлений |
| API keys (Gemini, Google) | Не истекают, пока аккаунт активен |
| Бэкапы R2 | Auto-cleanup старше 30 дней |

**Один раз настроил, забыл на годы.**

### 20.5. Что владелец делает в норме

| Регулярность | Действие | Время |
|--------------|----------|-------|
| Раз в 6 месяцев | Проверить email алерты, убедиться что нет накопившихся | 5 минут |
| Раз в 6 месяцев | Глянуть `system_health` метрики на дашборде | 5 минут |
| Раз в год | Обновить версию .NET / Postgres / зависимостей если есть security patches | 1-2 часа |
| Раз в год | Проверить что промпты Gemini всё ещё работают хорошо | 30 минут |
| По факту | DMCA-жалобы (редко) | 10 минут на штуку |

**Итого: ~3-4 часа в год на поддержку.** Это и есть «оно само живёт».

### 20.6. Дашборд автономии

Простая страница `/internal/health` (доступ по basic-auth, ключ в `.env`) показывает:

```
Georgia Places — System Health
Last updated: 2026-04-30 14:00 UTC

✅ API uptime (24h):              99.97%
✅ Parser last success:           6 hours ago
✅ Gemini quota:                  340 / 1500 calls today (22%)
✅ Google Places budget:          $42 / $200 this month (21%)
✅ Cache hit ratio (24h):         87%
⚠️ Items processed (7d avg):      2,400 (was 3,100, -23%)
✅ DB size:                        450 MB
✅ Latest UGC:                     12 minutes ago
✅ Last DMCA complaint:            none in last 90 days

Recent alerts (last 7 days):
  [2026-04-28 03:15] tg_fetch low items count (12 / 100 expected) — investigated, channel @example moved
```

Этого хватает чтобы за 30 секунд понять состояние сервиса. Без этого не понять что что-то идёт не так до того как начнут жаловаться юзеры.

### 20.7. Снижение human-in-the-loop через избыточность

Несколько источников для каждой важной функции, чтобы отказ одного не валил всё:

| Функция | Primary | Fallback 1 | Fallback 2 |
|---------|---------|------------|------------|
| LLM | Gemini 2.5 Flash | Groq Llama | Local Ollama |
| Карта тайлы | MapTiler | OSM raster | — |
| Affiliate агрегатор | Travelpayouts | Прямые партнёрки | — |
| Бэкапы | R2 | Backblaze B2 | Локально на VPS |
| CDN | Cloudflare | bunny.net | прямо с VPS |
| Email (DMCA, алерты) | Cloudflare Email Routing | Forwardemail.net | прямой mx |
| Donations | BMaC | Boosty | TON wallet |

Fallback'ы **не настраиваются заранее**, но в `appsettings` есть переменные `Use{Fallback}Mode=true` и в коде есть ветки. Если основной отказал — меняешь одну строку и заработало.

### 20.8. Анти-bus-factor (что если владелец совсем пропадёт)

Сервис продолжает работать **без вмешательства** до года. Если владелец возвращается — оживляет за день. Если не возвращается:

- Через ~1 год: VPS / домен auto-renew исчерпает баланс карты → сервис умирает тихо
- Никаких юридических обязательств перед юзерами (нет аккаунтов, нет персданных, нет платных подписок)
- Юзеры уже привыкли что данные могут быть устаревшими — постепенный закат не катастрофа

Это **приемлемый failure mode** для бесплатного хобби-проекта. Не нужно строить план «что если я умру» — пусть умирает сервис тоже.

### 20.9. Что НЕЛЬЗЯ автоматизировать (честно)

Признаём ограничения:

- **DMCA-жалобы** — юридически требуют ответа человека. Минимум 10 минут раз в N месяцев.
- **Major упгрейды** (.NET 9 → .NET 10, Postgres 16 → 17) — раз в 1-2 года, требуют тестирования.
- **Изменения партнёрских API** (Booking, GetYourGuide) — раз в год могут менять схемы.
- **Обновление промптов Gemini** при появлении новых моделей — раз в полгода стоит переоценить.

Эти задачи **выпадают на владельца неизбежно**. Цель ТЗ — свести к ~5 часам в год, не к нулю.

### 20.10. Метрики автономии

Проверка что "оно само живёт" работает:

- **MTBF (Mean Time Between Failures)** ≥ 90 дней
- **Email-алерты от системы** ≤ 1 в месяц в среднем
- **Время реакции на алерт** допустимо до 7 дней без последствий
- **Деградация сервиса** при отказе любого внешнего API ≤ 30% функций
- **Полный отказ сервиса** при отказе **двух одновременно** внешних сервисов — допустимо
- **Время восстановления** после downtime ≤ 1 час (через docker restart / Cloudflare)

---
