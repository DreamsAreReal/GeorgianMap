# ADR-0005: Branch protection model

**Статус:** Accepted
**Дата:** 2026-04-30
**Автор:** owner
**Задача:** initial repo setup

## Контекст

Команда — один человек. Репо public (выбор сделан осознанно, чтобы получить бесплатный branch protection — branch protection и rulesets платные на GitHub Free для private). Нужна защита от случайных деструктивных действий (force push, прямой push мимо PR, удаление веток) **без** требования внешнего аппрува.

## Рассмотренные варианты

### Вариант A: Никакой защиты (free private)
- **Плюсы:** простота, ничего не настраивать
- **Минусы:** случайный `git push --force` или `git push origin :main` — катастрофа
- **Сложность внедрения:** Low

### Вариант B: Старый branch protection API (классический)
- **Минусы:** платный для private; на public — работает, но менее гибкий чем rulesets
- **Сложность внедрения:** Low

### Вариант C: Rulesets с required PR (0 approvals) + linear history
- **Плюсы:** защита от force push и удаления, обязательный PR (даже для соло-разработчика — даёт CI-окно перед merge), современный API
- **Минусы:** PR overhead — нужно ходить через `develop` → `main` PR
- **Сложность внедрения:** Low

## Решение

Выбран **Вариант C** (rulesets) с конфигурацией:

```json
{
  "name": "main-protection",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["refs/heads/main"], "exclude": [] } },
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    { "type": "required_linear_history" },
    {
      "type": "pull_request",
      "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": true,
        "required_review_thread_resolution": true
      }
    }
  ],
  "bypass_actors": []
}
```

Применённый ruleset: https://github.com/DreamsAreReal/GeorgianMap/rules/15787510

## Workflow

```
1. git checkout develop
2. правки
3. git commit
4. git push origin develop
5. gh pr create --base main --head develop
6. (CI отработает на PR)
7. self-merge через gh pr merge --squash
```

При появлении CI с реальными статусами — добавить `required_status_checks` в ruleset, тогда merge заблокируется до зелёного билда.

## Последствия

- **Положительные:** невозможность force push / deletion даже автору; CI получает окно перед merge.
- **Отрицательные:** PR overhead для каждой правки (1-2 мин).
- **Миграция:** N/A.
- **Мониторинг:** N/A — настройка статическая.
- **Rollback plan:** `gh api -X DELETE repos/.../rulesets/15787510`.

## Триггеры пересмотра

- Если появятся коммиттеры — поднять `required_approving_review_count` до 1.
- Если CI стабилен — добавить required status checks.
- Если репо станет private (Pro tier) — миграция настроек прозрачна.

## Связанные ADR

- N/A
