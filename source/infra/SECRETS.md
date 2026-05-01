# Docker secrets layout

`source/infra/secrets/` is **not** under git (see root `.gitignore`). On the VPS create the directory manually with mode `0700`:

```bash
cd /opt/georgia-places/source/infra
mkdir -p secrets
chmod 700 secrets
```

Inside, create two files referenced by `docker-compose.yml`:

| File | Mode | Content |
|------|------|---------|
| `secrets/otlp_headers` | 0400 | One line: `Authorization=Basic <base64(instanceId:token)>` (Grafana Cloud) |
| `secrets/sentry_dsn` | 0400 | One line: `https://<key>@oNNN.ingest.sentry.io/PPP` |

Permissions:

```bash
chmod 400 secrets/otlp_headers secrets/sentry_dsn
```

These never leave the VPS, never appear in env, never leak via `docker compose config`. See [ADR-0006](../../docs/tech/adr/0006-observability-stack.md).
