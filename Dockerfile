FROM mcr.microsoft.com/dotnet/sdk:10.0.401-resolute-aot@sha256:2fa5621bb22034ea82107f1e9811e86f38c140cceac3cfafcdd59cb4a6db329c AS build
ARG TARGETARCH
ARG VERSION=0.7.2
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
FROM ghcr.io/institut-du-numerique-responsable/perf-sentinel:0.31.3@sha256:74f285c2610d0c6856837e7d152f37dcefc4fee176288a2c1f2401ab0ed22c76 AS engine

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.12-resolute-chiseled-extra@sha256:2b0f7a348127985b5accc794e1304b0f3f9ab9c8db652096514db4b831dc3ce4
ARG SOURCE_COMMIT=unknown
LABEL org.opencontainers.image.version="0.7.2" \
      org.opencontainers.image.revision="$SOURCE_COMMIT" \
      org.opencontainers.image.source="https://github.com/Institut-du-Numerique-Responsable/PerfSentinelHub"
WORKDIR /app
# Root owns what it runs, so the service account cannot rewrite its own binary.
COPY --from=build /out/PerfSentinelHub /app/PerfSentinelHub
COPY --from=build /out/libe_sqlite3.so /app/libe_sqlite3.so
# The launcher is static files beside the binary, and without them every page answers 404.
COPY --from=build /out/wwwroot /app/wwwroot
COPY --from=engine /perf-sentinel /app/perf-sentinel
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER 1654:1654
ENTRYPOINT ["/app/PerfSentinelHub"]
