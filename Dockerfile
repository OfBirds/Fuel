# syntax=docker/dockerfile:1

# Version/build metadata, supplied by CI (falls back to dev values locally).
ARG APP_VERSION=dev
ARG GIT_COMMIT=unknown
ARG BUILD_TIME=unknown

# ---- Stage 1: build the frontend (Vite/React -> static files) ----
FROM node:20-alpine AS frontend
ARG APP_VERSION
WORKDIR /src/frontend
# Install deps first for better layer caching.
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
# APP_VERSION is read by vite.config.ts and baked into the bundle.
RUN APP_VERSION="$APP_VERSION" npm run build
# Output is /src/frontend/dist

# ---- Stage 2: build & publish the backend, bundling the SPA into wwwroot ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
ARG APP_VERSION
WORKDIR /src
# Restore first for better layer caching.
COPY backend/Api/Api.csproj backend/Api/
RUN dotnet restore backend/Api/Api.csproj
COPY backend/Api/ backend/Api/
# Drop the built SPA into wwwroot so ASP.NET serves it (UseStaticFiles + fallback).
COPY --from=frontend /src/frontend/dist backend/Api/wwwroot
# InformationalVersion accepts any string (unlike Version), so the full
# MAJOR.MINOR.run_number lands in the assembly metadata / logs.
RUN dotnet publish backend/Api/Api.csproj -c Release -o /app/publish \
    -p:InformationalVersion="$APP_VERSION"

# ---- Stage 3: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG APP_VERSION
ARG GIT_COMMIT
ARG BUILD_TIME
WORKDIR /app
# Npgsql probes for Kerberos/GSSAPI at startup; without this lib it logs a noisy
# (harmless) error before falling back to password auth. Install it to keep
# startup logs clean.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=backend /app/publish ./
# Release notes baked into the image, read by ReleaseNotifier for the deploy email.
COPY RELEASE_NOTES.md /app/RELEASE_NOTES.md
# Expose build metadata to the app (read by GET /api/version).
ENV APP_VERSION=$APP_VERSION \
    GIT_COMMIT=$GIT_COMMIT \
    BUILD_TIME=$BUILD_TIME
# Run as the non-root user the aspnet image predefines (APP_UID=1654) so a code-exec bug
# can't act as root on the host. Pre-create the two dirs the app writes to — Serilog's file
# sink (logs/) and BackupService (backups/, unless BACKUP_DIR points elsewhere) — and hand
# them to that user, since /app itself stays root-owned/read-only.
# NOTE for deploy: if BACKUP_DIR is set to a mounted volume, that volume must be writable by UID 1654.
RUN mkdir -p /app/logs /app/backups && chown -R $APP_UID:$APP_UID /app/logs /app/backups
USER $APP_UID
# The aspnet image listens on 8080 by default (ASPNETCORE_HTTP_PORTS=8080).
EXPOSE 8080
ENTRYPOINT ["dotnet", "Api.dll"]
