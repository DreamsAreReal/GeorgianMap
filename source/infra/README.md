# Infra

Деплой, конфигурация, скрипты эксплуатации.

## Содержимое (план)

```
infra/
├── docker-compose.yml          # postgres + api + nginx + parser (profile=manual)
├── .env.example                # шаблон переменных (см. корневой README)
├── nginx/
│   └── nginx.conf              # reverse proxy + SSL termination
├── postgres/
│   └── init.sql                # CREATE EXTENSION postgis, базовые роли
├── scripts/
│   ├── backup.sh               # pg_dump → R2 через rclone
│   ├── restore.sh              # restore из R2
│   ├── verify-restore.sh       # тест-restore раз в 30 дней (см. P0 review)
│   └── deploy.sh               # rsync + docker compose up
├── cron/
│   └── crontab.txt             # daily parser, hourly aggregate, daily backup
└── monitoring/
    └── healthchecks.md         # ссылки на Healthchecks.io UUIDs
```

## Деплой (план)

VPS: 1 vCPU, 1 GB RAM, $5-10/мес (Hetzner/Contabo/etc).

```bash
ssh user@vps
cd /opt/georgia-places
git pull
docker compose pull
docker compose up -d
```

См. [ТЗ](../../docs/tech/georgia_places_tz.md) §10 (deploy), §12 (performance), §20 (graceful degradation).

## Бэкап и DR

- RPO=24h, RTO≈30 мин (см. ADR-0002)
- Бэкап: `pg_dump --format=custom` → R2 ежедневно
- Verify-restore: раз в 30 дней (закрывает P0 из ревью)
