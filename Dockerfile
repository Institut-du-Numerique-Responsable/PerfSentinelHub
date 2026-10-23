FROM mcr.microsoft.com/dotnet/sdk:10.0.402-resolute-aot@sha256:cb1c2b2844192553017d1e492705f1c5aa75b056109488b4677705dac3b20f81 AS build
ARG TARGETARCH
ARG VERSION=0.8.1
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
FROM ghcr.io/institut-du-numerique-responsable/perf-sentinel:0.33.0@sha256:4def9a4f671afd3e706e38c5fc5beea8a466a7f000bce4f26f6d15baaba615cc AS engine

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.13-resolute-chiseled-extra@sha256:8d3382bf11c0da9d087b1852dc2859c5ff113348cbab20fa1282babf36b2386a
ARG SOURCE_COMMIT=unknown
LABEL org.opencontainers.image.version="0.8.1" \
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
