# Parser

Python cron-парсер. Запускается раз в сутки в 03:00 (см. §6 ТЗ), отрабатывает, умирает.

## Стек

- Python 3.12+
- httpx (HTTP клиент)
- beautifulsoup4 (Telegram t.me/s/<channel> scraping)
- psycopg3 (PostgreSQL клиент)
- google-generativeai (Gemini 2.5 Flash для извлечения атрибутов)

## Источники

| Источник | Tier | Частота | Описание |
|----------|------|---------|----------|
| Google Places API | 1 (authoritative) | Daily | Базовые сведения о местах |
| Geofabrik OSM dump | 1 | Weekly | OSM POI extract по Грузии |
| Wikidata SPARQL | 1 | Weekly | Метаданные (категории, описания) |
| `t.me/s/<channel>` | 2 (UGC) | Daily | Туристические Telegram-каналы → LLM extract |

## Структура (план)

```
parser/
├── src/
│   ├── jobs/             # daily_parse.py, hourly_aggregate.py
│   ├── sources/          # google_places.py, osm.py, wikidata.py, telegram.py
│   ├── extractors/       # gemini_extract.py
│   ├── db/               # staging schema migrations
│   └── settings.py
├── tests/
└── pyproject.toml
```

## Запуск

> TBD — после Этапа 1.

См. [ТЗ](../../docs/tech/georgia_places_tz.md) §6 (parsing).
