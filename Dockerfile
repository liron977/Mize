FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Mize.Reservations.sln ./
COPY src/Mize.Reservations.Core/*.csproj src/Mize.Reservations.Core/
COPY src/Mize.Reservations.Storage.Sqlite/*.csproj src/Mize.Reservations.Storage.Sqlite/
COPY src/Mize.Reservations.RateLimiting/*.csproj src/Mize.Reservations.RateLimiting/
COPY src/Mize.Reservations.RateLimiting.Redis/*.csproj src/Mize.Reservations.RateLimiting.Redis/
COPY src/Mize.Reservations.Archive.FileSystem/*.csproj src/Mize.Reservations.Archive.FileSystem/
COPY src/Mize.Reservations.Archive.S3/*.csproj src/Mize.Reservations.Archive.S3/
COPY src/Mize.Reservations.Api/*.csproj src/Mize.Reservations.Api/
COPY src/Mize.Reservations.Retention.Worker/*.csproj src/Mize.Reservations.Retention.Worker/
RUN dotnet restore src/Mize.Reservations.Api/Mize.Reservations.Api.csproj \
 && dotnet restore src/Mize.Reservations.Retention.Worker/Mize.Reservations.Retention.Worker.csproj
COPY src/ src/
RUN dotnet publish src/Mize.Reservations.Api/Mize.Reservations.Api.csproj -c Release -o /app/api --no-restore \
 && dotnet publish src/Mize.Reservations.Retention.Worker/Mize.Reservations.Retention.Worker.csproj -c Release -o /app/worker --no-restore

# ---- retention worker: archives and purges old reservations (one run and exit, or on an interval). Not the web API.
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS worker
WORKDIR /app
COPY --from=build /app/worker .
# /data is the shared SQLite volume, /archive the directory archive; the non-root app user must be able to write to both
RUN mkdir -p /data /archive && chown -R $APP_UID /data /archive
USER $APP_UID
ENV Sqlite__Path=/data/reservations.db \
    Archive__FileSystem__Path=/archive
ENTRYPOINT ["dotnet", "Mize.Reservations.Retention.Worker.dll"]

# ---- web API (the LAST stage, so a plain `docker build .` produces the API image)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS api
WORKDIR /app
COPY --from=build /app/api .
# /data is the shared SQLite volume; the non-root app user must be able to write to it
RUN mkdir -p /data && chown -R $APP_UID /data
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    Sqlite__Path=/data/reservations.db
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mize.Reservations.Api.dll"]
