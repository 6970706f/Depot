# syntax=docker/dockerfile:1

# Build: restores first, so code changes don't download the packages again.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/Depot.Domain/Depot.Domain.csproj backend/Depot.Domain/
COPY backend/Depot.Application/Depot.Application.csproj backend/Depot.Application/
COPY backend/Depot.Infrastructure/Depot.Infrastructure.csproj backend/Depot.Infrastructure/
COPY backend/Depot.Api/Depot.Api.csproj backend/Depot.Api/
RUN dotnet restore backend/Depot.Api/Depot.Api.csproj
COPY backend/ backend/
RUN dotnet publish backend/Depot.Api/Depot.Api.csproj --configuration Release --no-restore --output /out

# Runtime. Debian-based on purpose: hardlinks call link(2) in glibc's libc.so.6, which Alpine lacks.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
COPY frontend/ frontend/

# /config holds Depot's own data (database, keys, access token); /data is the media folder that
# qBittorrent and Jellyfin also mount, so downloads and library are on one filesystem and
# imports can use hardlinks. The Organization values are only the first defaults: what is saved
# in Settings wins.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Storage__DataDirectory=/config \
    Frontend__Path=/app/frontend \
    Organization__MoviesPath=/data/movies \
    Organization__SeriesPath=/data/series \
    Organization__PathMappings__0__QbittorrentPath=/data \
    Organization__PathMappings__0__DepotPath=/data

RUN mkdir /config && chown "$APP_UID" /config
USER $APP_UID
VOLUME /config
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 \
    CMD ["dotnet", "Depot.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Depot.Api.dll"]
