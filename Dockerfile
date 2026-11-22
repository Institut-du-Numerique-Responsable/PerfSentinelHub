FROM mcr.microsoft.com/dotnet/sdk:10.0.404-resolute-aot@sha256:23f95eefa095a0137d9288a37922e31066c2dcb4a5903a805ed4f180d2c0f3d6 AS build
ARG TARGETARCH
ARG VERSION=0.20.0
ARG SOURCE_DATE_EPOCH
WORKDIR /src
COPY . .
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && dotnet restore PerfSentinelHub.sln --locked-mode \
    && dotnet publish PerfSentinelHub/PerfSentinelHub.csproj -c Release -r "$rid" \
       --self-contained true -p:PublishAot=true -p:Version="$VERSION" --no-restore -o /out

# The engine the Hub runs for an analysis. Pinned by digest like every other
# image here, and copied rather than downloaded so the build reaches no host
# outside the registry.
FROM ghcr.io/institut-du-numerique-responsable/perf-sentinel:0.47.0@sha256:dd626510cb972d7f6bf32d825ac792ac9b4d9fd6ffe92840466c2b7e4b7f1ea7 AS engine

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.15-resolute-chiseled-extra@sha256:21f636dfa1e34282a96032c76d39d7e82fb0f34a4f2441a1f5fe1e1a1301b91f
ARG SOURCE_COMMIT=unknown
LABEL org.opencontainers.image.version="0.20.0" \
      org.opencontainers.image.revision="$SOURCE_COMMIT" \
      org.opencontainers.image.source="https://github.com/Institut-du-Numerique-Responsable/PerfSentinelHub"
WORKDIR /app
# Root owns what it runs, so the service account cannot rewrite its own binary.
COPY --from=build /out/PerfSentinelHub /app/PerfSentinelHub
COPY --from=build /out/libe_sqlite3.so /app/libe_sqlite3.so
# Base settings (log levels); the chart's appsettings.Production.json only layers on top.
COPY --from=build /out/appsettings.json /app/appsettings.json
# The launcher is static files beside the binary, and without them every page answers 404.
COPY --from=build /out/wwwroot /app/wwwroot
COPY --from=engine /perf-sentinel /app/perf-sentinel
ENV ASPNETCORE_HTTP_PORTS=8080
# SQLite's own temporary files (a sort past its cache, an index build) go beside the database, not
# into /tmp, which the chart caps at 32 MiB: past it the kubelet evicts the pod. Without a /data,
# SQLite falls back to /tmp.
ENV SQLITE_TMPDIR=/data
EXPOSE 8080
USER 1654:1654
ENTRYPOINT ["/app/PerfSentinelHub"]
