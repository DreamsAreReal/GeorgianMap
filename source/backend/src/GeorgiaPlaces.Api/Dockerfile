# syntax=docker/dockerfile:1.7
# Multi-stage build for GeorgiaPlaces.Api.
# Build context = source/backend (see docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# Copy props/json first for layer cache.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/GeorgiaPlaces.Domain/GeorgiaPlaces.Domain.csproj         src/GeorgiaPlaces.Domain/
COPY src/GeorgiaPlaces.Application/GeorgiaPlaces.Application.csproj src/GeorgiaPlaces.Application/
COPY src/GeorgiaPlaces.Infrastructure/GeorgiaPlaces.Infrastructure.csproj src/GeorgiaPlaces.Infrastructure/
COPY src/GeorgiaPlaces.Api/GeorgiaPlaces.Api.csproj               src/GeorgiaPlaces.Api/
RUN dotnet restore src/GeorgiaPlaces.Api/GeorgiaPlaces.Api.csproj

# Now copy the actual sources and publish.
COPY src/ src/
RUN dotnet publish src/GeorgiaPlaces.Api/GeorgiaPlaces.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false \
    /p:PublishSingleFile=false \
    /p:DebugType=embedded

# ─── runtime ───────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime
WORKDIR /app

# tini for proper SIGTERM handling. The base aspnet image already ships
# an `app` user/group — reuse it.
RUN apk add --no-cache tini wget \
    && chown -R app:app /app

USER app

COPY --from=build --chown=app:app /app/publish .

# Server GC + GCHeapHardLimit per ADR-0003.
ENV DOTNET_gcServer=1 \
    DOTNET_gcConcurrent=1 \
    DOTNET_GCHeapHardLimit=200000000 \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["/sbin/tini", "--"]
CMD ["dotnet", "GeorgiaPlaces.Api.dll"]
