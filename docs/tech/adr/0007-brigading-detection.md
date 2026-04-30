# ADR-0007: Brigading detection — sliding 7-day window

**Статус:** Accepted
**Дата:** 2026-04-30
**Автор:** owner
**Задача:** REVIEW-BACKLOG P0 (business-logic): brigading 4 сигнала/день × 5 дней не детектируется текущими 24-часовыми порогами.

## Контекст

ТЗ §5.3 описывает `detect_anomaly` с тремя паттернами:
- `burst_attack`: >10 сигналов на одно место за 24h
- `single_source`: >5 сигналов с одной подсети /24 за 24h
- `coordinated_unanimous`: >3 сигналов с разных IP, но одинаковый текст / fingerprint

Ревью business-logic-reviewer указал: координированная атака **4 сигнала/день в течение 5 дней** (всего 20) **не триггерит ни один из паттернов**, потому что окно — 24 часа. На 5-й день консенсус из спорных сигналов уже сформирован (`min_signals=3, min_age_spread_hours=24` — выполнено), атрибут (например, `is_open=false` для конкурентного бизнеса) переключается. Защита `google_override` работает только для `is_open` — для `price_gel`, `road`, `dogs` её нет.

Угроза реальная: туристические места в Грузии — частая мишень рекламных манипуляций (накрутка рейтинга своего хинкальни, понижение конкурента).

## Рассмотренные варианты

### Вариант A: Status quo (24h окна)
- **Минусы:** не ловит slow brigading
- **Сложность:** Low (текущий код)

### Вариант B: Скользящее окно 7 дней с лимитом per-subnet
- **Плюсы:** ловит slow brigading, простая реализация (один SQL запрос за окно)
- **Минусы:** false positive при легитимных частых отчётах от тур.агентства из одного офиса (та же /24)
- **Сложность:** Low

### Вариант C: ML-based detection (графовый анализ, кластеризация)
- **Плюсы:** ловит сложные паттерны
- **Минусы:** требует ML stack (sklearn/torch), training data, ресурсы 1GB VPS не позволяют
- **Сложность:** High
- **Вердикт:** **отброшено**

### Вариант D: LLM-arbitrage по подозрительным цепочкам
- **Плюсы:** гибкость
- **Минусы:** жжёт Gemini лимит (1500 RPD), дорого по бюджету для каждой сигнальной серии
- **Сложность:** Medium
- **Вердикт:** **возможно как вторая линия** — но не как первичная защита

## Решение

Выбран **Вариант B**: добавить три новых паттерна в `detect_anomaly`, работающих на скользящем окне 7 дней.

### Новые паттерны

**Паттерн 4 — `slow_brigading_subnet_7d`**

```sql
WITH subnet_signals AS (
    SELECT
        place_id,
        attribute_key,
        attribute_value,
        substring(ip_address::text from '^(\d+\.\d+\.\d+)\.') AS subnet_24,
        count(*) AS sig_count
    FROM place_signals
    WHERE source_type = 'ugc'
      AND created_at > now() - interval '7 days'
      AND ip_address IS NOT NULL
    GROUP BY 1, 2, 3, 4
)
SELECT * FROM subnet_signals WHERE sig_count >= 4;
```

Найденные сигналы помечаются `excluded_from_consensus = true` с reason `'slow_brigading_subnet'`. Не удаляются — для аудита.

**Паттерн 5 — `slow_brigading_fingerprint_7d`**

Аналогично, но группировка по `fingerprint_hash` вместо subnet. Лимит: ≥3 сигналов на одно место с одинаковым fingerprint за 7 дней.

**Паттерн 6 — `coordinated_value_flip`**

Если за 7 дней пришло ≥5 сигналов на одно место и одну attribute_key, и **все** они меняют значение на одно и то же (например, все говорят `is_open=false`), при этом источники географически разнесены (>3 разных subnet) — но в коротком временном окне (75% сигналов в пределах 12 часов любого момента в 7 днях) — это ≥75% probability координированной атаки.

```sql
WITH value_flips AS (
    SELECT
        place_id,
        attribute_key,
        attribute_value,
        count(*) AS sig_count,
        count(DISTINCT substring(ip_address::text from '^(\d+\.\d+\.\d+)\.')) AS distinct_subnets,
        max(created_at) - min(created_at) AS span,
        percentile_cont(0.5) WITHIN GROUP (ORDER BY created_at) AS median_time
    FROM place_signals
    WHERE source_type = 'ugc'
      AND created_at > now() - interval '7 days'
    GROUP BY 1, 2, 3
)
SELECT * FROM value_flips
WHERE sig_count >= 5
  AND distinct_subnets >= 3
  AND span < interval '7 days'
  AND (SELECT count(*) FROM place_signals s
       WHERE s.place_id = value_flips.place_id
         AND s.attribute_key = value_flips.attribute_key
         AND s.attribute_value = value_flips.attribute_value
         AND s.created_at BETWEEN median_time - interval '6 hours' AND median_time + interval '6 hours'
      ) * 1.0 / sig_count >= 0.75;
```

Эти сигналы **не помечаются как excluded автоматически** — они идут в очередь на **LLM-арбитраж** (Вариант D как вторичный): Gemini получает анонимизированные тексты сигналов и оценивает, выглядят ли они как органичный отзыв или как координированная кампания. Решение Gemini сохраняется.

### Производительность

Запрос за 7 дней по `place_signals` при 500K rows на горизонте 1-2 лет:

- Партиционирование по месяцам (P1 в REVIEW-BACKLOG) → запрос затрагивает 1-2 партиции, ~100K rows
- Индекс `idx_signals_created_at` (есть в ТЗ §4.3)
- Group by + aggregation на 100K rows на 1 vCPU — ~100-300 ms
- Запускается раз в час в `anomaly_detect` — приемлемо

Если перформанс деградирует — поднять окно с 7 дней до 3 дней (ловим самые свежие атаки) и добавить отдельный nightly job на 7-day deep scan.

### IP-адреса и анонимность

ТЗ §1.2: «без аккаунтов и персональных данных». IP-адрес — PII в EU. Решение:
- Храним **не сам IP, а хэш `subnet_/24` с солью** (`SHA256(salt || subnet)`) → используется только для anti-abuse
- Соль ротируется раз в 90 дней — старые хэши становятся бесполезны
- Сырой IP не пишется в БД, только в logs (с masking последнего октета `1.2.3.0/24`)

ТЗ §4.3 (`place_signals`) уже использует `subnet_hash TEXT` — ADR уточняет: `subnet_hash = SHA256(salt || subnet_24)` с ротацией соли раз в 90 дней. Поле `ip_hash` удалено (сырой IP не хранится).

## Последствия

- **Положительные:** покрытие slow-brigading; LLM-арбитраж только для подозрительных кейсов (не жжёт лимит); хеширование IP — лучше под GDPR/анонимность.
- **Отрицательные:**
  - False positives от тур.агентств / гостиниц из одного офиса (mitigation: исключение по фингерпринту + субнету одновременно — должно быть ≥4 сигналов с разными fingerprint **И** одной subnet, что для офиса нехарактерно)
  - +1 hourly job в `anomaly_detect` → +5-10 сек CPU/час (overlap с parser-ом — см. P1 advisory_lock)
  - LLM-арбитраж по `coordinated_value_flip` жжёт ~10-50 Gemini вызовов/мес дополнительно
- **Миграция:**
  - ТЗ §4.5: `ip_address INET` → `subnet_hash TEXT NOT NULL`
  - ТЗ §5.3: добавить 3 новых паттерна
  - ТЗ §6.3: hourly `anomaly_detect` запускается на 02:30 (не пересекается с daily parser в 03:00)
  - Backfill: для уже существующих сигналов (если есть) — null subnet_hash, не участвуют в детекции
- **Мониторинг:** метрика `brigading_signals_excluded_total{pattern="..."}`, алерт при росте >50/день (либо реальная атака, либо false-positive bug)
- **Rollback plan:** убрать новые паттерны из `anomaly_detect`, вернуться к 24h-only.

## Связанные ADR

- ADR-0006 (Observability) — метрики brigading
- ADR-0008 (Consensus override) — дополнительная защита для high-impact attributes
