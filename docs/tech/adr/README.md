# Architecture Decision Records

Каждый значимый выбор фиксируется отдельным ADR. Шаблон: [`0000-template.md`](0000-template.md).

## Правила

1. Сквозная нумерация: `0001`, `0002`, ... Не переиспользовать.
2. ADR иммутабелен после `Accepted`. Передумали → новый ADR со статусом `Superseded by ADR-NNNN`.
3. Каждый вариант оценивается под целевую нагрузку (50 RPS peak, 10-15 RPS cold cache на 1 vCPU / 1 GB RAM).
4. ADR создаётся **до** реализации (шаг 2 пайплайна).

## Каталог

| № | Название | Статус | Дата |
|---|----------|--------|------|
| 0001 | [Monolith on single VPS](0001-monolith-on-single-vps.md) | Accepted | 2026-04-30 |
| 0002 | [RPO=24h, no WAL archiving](0002-rpo-24h-no-wal-archiving.md) | Accepted | 2026-04-30 |
| 0003 | [Server GC with hard heap limit](0003-server-gc-with-heap-limit.md) | Accepted | 2026-04-30 |
| 0004 | [API versioning policy: /api/v1/](0004-api-versioning-v1-prefix.md) | Accepted | 2026-04-30 |
| 0005 | [Branch protection model](0005-branch-protection-model.md) | Accepted | 2026-04-30 |
