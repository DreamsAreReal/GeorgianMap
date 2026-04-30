# ADR-0008: Consensus override thresholds for high-impact attributes

**Статус:** Accepted
**Дата:** 2026-04-30
**Автор:** owner
**Задача:** REVIEW-BACKLOG P0 (business-logic): одиночный Tier-2 сигнал с `confidence ≥ 0.7` перезаписывает атрибуты без consensus.

## Контекст

ТЗ §5.2.2 описывает применение Tier-2 (Telegram/UGC через Gemini) сигналов:

> Атрибут применяется немедленно если LLM вернул `confidence ≥ 0.7`.

Бизнес-ревьюер: «Канал с 15k подписчиков, где кто-то иронически написал "конечно, вход бесплатный" — Gemini даст confidence 0.73 и атрибут `free=true` запишется как факт».

Проблемные атрибуты — те, чьё неверное значение **обманывает пользователя на месте**:
- `price_gel` — пришёл, а на входе билет 30 лари вместо обещанных 10
- `is_open` — приехал к закрытому
- `dogs` — привёз собаку, не пускают
- `road` — поехал на седане по grunt road, проблемы

Атрибуты с низким impact — `parking`, `wifi`, `family_friendly` — терпят одиночный сигнал.

## Рассмотренные варианты

### Вариант A: Status quo (single Tier-2 ≥0.7 → apply)
- **Минусы:** уязвим к иронии, опечаткам, sarcasm в источниках
- **Сложность:** Low

### Вариант B: Глобальный порог: всегда требуем 2+ Tier-2 сигнала с одинаковым значением
- **Плюсы:** простое правило, безопасно
- **Минусы:** замедляет применение всех атрибутов (даже `parking`), редкие места с малым потоком отзывов вообще ничего не получат
- **Сложность:** Low

### Вариант C: Per-attribute confidence threshold + per-attribute min-signals (выбран)
- **Плюсы:** высокий impact = строгий порог; низкий impact = быстрое применение
- **Минусы:** усложняется конфиг, нужна явная классификация attributes
- **Сложность:** Medium

### Вариант D: Bayesian update — каждый сигнал двигает posterior, переключение при p > 0.95
- **Плюсы:** теоретически корректно
- **Минусы:** сложно объяснить, сложно дебажить, требует prior'ов на каждый attribute
- **Сложность:** High
- **Вердикт:** **отброшено** — overkill

## Решение

Выбран **Вариант C**. Атрибуты классифицируются по impact level. Каждый класс имеет свои пороги.

### Классификация атрибутов

| Impact | Атрибуты | Min agreeing Tier-2 signals | Min confidence | Notes |
|--------|----------|------------------------------|----------------|-------|
| **Critical** | `price_gel`, `entrance_fee`, `is_open`, `is_closed_permanently` | 3 | 0.8 | Пользователь принимает решение «ехать/не ехать» |
| **High** | `dogs`, `road`, `wheelchair_accessible`, `kids_allowed` | 2 | 0.75 | Пользователь готовится физически |
| **Medium** | `parking`, `wifi`, `family_friendly`, `viewpoint_360`, `bathrooms` | 1 | 0.7 | Удобства, не блокеры |
| **Low** | `instagram_friendly`, `quiet`, `crowded`, `aesthetic_score` | 1 | 0.6 | Субъективные оценки |

«Agreeing» означает: одинаковое значение (для bool / enum) или диапазон ±10% (для range_int / range_float).

### Дополнительные правила

1. **Tier-1 сигналы (Google/OSM/Wikidata) применяются немедленно** для всех уровней — это authoritative источники.
2. **Tier-1 + Tier-2 конфликт** для `is_open`: Google Places `currentOpeningHours` побеждает (см. ТЗ §5.2.2 `google_override`). Для `price_gel` Google не отдаёт цены — Tier-2 побеждает (consensus из 3 сигналов).
3. **Single-signal apply разрешён** для Medium/Low уровней — но с пометкой `provisional = true` в `attribute_sources`. Через 7 дней без подтверждения — сбрасывается обратно в `null`.
4. **Time decay для Tier-2**: вес сигнала падает экспоненциально с полупериодом 30 дней (cm. §5.4 ТЗ — уже описано). Сигналы старше 90 дней не считаются для Critical/High порогов.

### Реализация (псевдокод)

```python
ATTRIBUTE_IMPACT = {
    'price_gel': 'critical',
    'entrance_fee': 'critical',
    'is_open': 'critical',
    'is_closed_permanently': 'critical',
    'dogs': 'high',
    'road': 'high',
    'wheelchair_accessible': 'high',
    'kids_allowed': 'high',
    'parking': 'medium',
    'wifi': 'medium',
    'family_friendly': 'medium',
    'viewpoint_360': 'medium',
    'bathrooms': 'medium',
    'instagram_friendly': 'low',
    'quiet': 'low',
    'crowded': 'low',
    'aesthetic_score': 'low',
}

THRESHOLDS = {
    'critical': {'min_signals': 3, 'min_confidence': 0.8},
    'high':     {'min_signals': 2, 'min_confidence': 0.75},
    'medium':   {'min_signals': 1, 'min_confidence': 0.7},
    'low':      {'min_signals': 1, 'min_confidence': 0.6},
}

def can_apply_tier2(place_id: int, attr_key: str, candidate_value: Any) -> tuple[bool, str]:
    impact = ATTRIBUTE_IMPACT.get(attr_key, 'medium')
    cfg = THRESHOLDS[impact]

    agreeing_signals = db.execute("""
        SELECT count(*), avg(confidence)
        FROM place_signals
        WHERE place_id = %s
          AND attribute_key = %s
          AND attribute_value::text = %s::text  -- normalized comparison
          AND confidence >= %s
          AND tier = 2
          AND created_at > now() - interval '90 days'
          AND excluded_from_consensus = false
    """, (place_id, attr_key, candidate_value, cfg['min_confidence'])).fetchone()

    count, avg_conf = agreeing_signals
    if count < cfg['min_signals']:
        return False, f'need {cfg["min_signals"]} agreeing signals (have {count})'
    return True, f'{count} agreeing signals, avg confidence {avg_conf:.2f}'
```

## Последствия

- **Положительные:**
  - Иронический пост в Telegram больше не превращается в `free=true`
  - Critical атрибуты требуют community consensus, не одного автора
  - Low-impact атрибуты обновляются быстро — UX не страдает
- **Отрицательные:**
  - Свежесозданные места без потока отзывов будут долго иметь `null` для Critical атрибутов
  - Для Critical нужно минимум 3 сигналов = реалистично 2-4 недели для непопулярных мест
  - Mitigation: Tier-1 (Google Places) даёт `is_open` и часть `price` (рестораны) — основная масса покрывается
- **Миграция:**
  - ТЗ §5.2.2: переписать раздел «Применение Tier-2 сигналов»
  - ТЗ §5.6: добавить таблицу ATTRIBUTE_IMPACT в раздел weights
  - Backfill: для уже применённых атрибутов с count<min_signals — снять с `attribute_sources`, отметить `pending_consensus = true`
- **Мониторинг:**
  - Метрика `attribute_apply_blocked_total{impact_level=...,reason=...}` — видно, сколько применений блокируется новой логикой
  - Алерт при `apply_blocked / apply_total > 0.7 for 24h` — возможно, пороги слишком жёсткие, нужна корректировка
- **Rollback plan:** вернуть пороги на `min_signals: 1, min_confidence: 0.7` для всех — одна правка в `THRESHOLDS`. Backfill отметок `pending_consensus = true` через UPDATE.

## Триггеры пересмотра

- Если 50%+ Critical атрибутов остаются `null` через 60 дней после запуска — снизить порог до 2 сигналов
- Если выявится систематический false-positive для одного атрибута (например, `dogs` всегда занижается) — переклассифицировать impact

## Связанные ADR

- ADR-0007 (Brigading detection) — фильтрует подозрительные сигналы до того, как они засчитываются в consensus
