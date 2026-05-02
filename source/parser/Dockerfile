# syntax=docker/dockerfile:1.7
FROM python:3.12-alpine AS runtime

# psycopg[binary] ships compiled libpq; need libffi for some pure-py wheels.
RUN apk add --no-cache libffi tini ca-certificates \
    && addgroup -S app && adduser -S app -G app

WORKDIR /app

# Install deps before copying source (better layer cache).
COPY pyproject.toml ./
RUN pip install --no-cache-dir --upgrade pip \
    && pip install --no-cache-dir \
        "httpx>=0.27,<0.30" \
        "psycopg[binary]>=3.2,<3.3" \
        "structlog>=24.4,<26"

COPY src/ ./src/

USER app

ENTRYPOINT ["/sbin/tini", "--"]
CMD ["python", "-m", "src.parser"]
