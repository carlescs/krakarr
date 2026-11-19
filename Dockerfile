# syntax=docker/dockerfile:1

# ---- 1. Angular frontend ------------------------------------------------------------------
FROM node:22-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npx ng build --configuration production

# ---- 2. .NET backend ----------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/Krakarr.Host/Krakarr.Host.csproj -a $TARGETARCH
RUN dotnet publish src/Krakarr.Host/Krakarr.Host.csproj -c Release -a $TARGETARCH --no-restore -o /app \
    /p:UseAppHost=false /p:DebugType=none
COPY --from=web /src/web/dist/krakarr/browser /app/wwwroot

# ---- 3. Runtime ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:11.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_HTTP_PORTS=8787 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=0 \
    Krakarr__DownloadDirectory=/data/downloads \
    PUID=1000 \
    PGID=1000 \
    UMASK=002

WORKDIR /app
COPY --from=build /app ./
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh && mkdir -p /config /data

# /data holds both downloads/ and the media library so imports can hardlink (one mount = one filesystem).
VOLUME ["/config", "/data"]
EXPOSE 8787 6881/tcp 6881/udp

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS "http://127.0.0.1:8787${KRAKARR__URLBASE:-}/api/v1/health" || exit 1

ENTRYPOINT ["/entrypoint.sh"]
