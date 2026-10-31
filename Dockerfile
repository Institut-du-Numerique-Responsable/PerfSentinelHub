FROM mcr.microsoft.com/dotnet/sdk:10.0.403-resolute-aot@sha256:a66703b1c786b77dad068cd0de51b5538b406f3febb16e8920ec9e73aee181ae AS build
ARG TARGETARCH
ARG VERSION=0.10.0
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
FROM ghcr.io/institut-du-numerique-responsable/perf-sentinel:0.40.0@sha256:a4f69a3c7fd32ff676d6c8a529579074d6c62ab911a6a331ec722b73f062a1f6 AS engine

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.14-resolute-chiseled-extra@sha256:9e6ce44af9c4fef28620453d51e9b431f83b9889c6a6b0ce749b2fc958438aa6
ARG SOURCE_COMMIT=unknown
LABEL org.opencontainers.image.version="0.10.0" \
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
EXPOSE 8080
USER 1654:1654
ENTRYPOINT ["/app/PerfSentinelHub"]
