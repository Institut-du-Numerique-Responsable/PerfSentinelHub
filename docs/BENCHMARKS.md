# Measurements

What the published image weighs, how fast it answers after a cold start, and
how much memory it holds at rest. Every figure below comes from the released
`ghcr.io/institut-du-numerique-responsable/perf-sentinel-hub:0.3.3` image, not from a local build, and
the commands to reproduce them close the page. The one exception, the start on a
filled database, says how it was measured.

The Hub is published with NativeAOT: C# 14 on .NET 10 compiled ahead of time
into one native executable, with no .NET runtime and no JIT in the image. The
base is `runtime-deps` chiseled, which ships the system libraries and nothing
else, and the process runs as the non-root user `1654`.

## Summary

| Figure                       | Value                                                                   |
|------------------------------|-------------------------------------------------------------------------|
| Cold start to first response | 130 ms median, 120 to 138 ms over 10 runs                               |
| Memory at rest               | 20.3 to 20.8 MiB (21.3 to 21.8 MB), 60 s after start, over 3 containers |
| Compressed image, amd64      | 39.8 MB: base 21.4, Hub 11.1, Perf Sentinel 7.4                         |
| Compressed image, arm64      | 37.7 MB: base 20.3, Hub 10.6, Perf Sentinel 6.8                         |

## What the image carries

The image embeds the Perf Sentinel engine binary, which the launcher runs as a
subprocess. The split below separates what belongs to the Hub from what
belongs to the engine and to the base.

| Layer                 | amd64, compressed | arm64, compressed | arm64, on disk |
|-----------------------|-------------------|-------------------|----------------|
| Chiseled base         | 21.4 MB           | 20.3 MB           |                |
| Hub executable        | 9.6 MB            | 9.1 MB            | 21.1 MB        |
| SQLite native library | 0.7 MB            | 0.8 MB            | 1.5 MB         |
| Browser interface     | 0.8 MB            | 0.8 MB            |                |
| Perf Sentinel 0.25.2  | 7.4 MB            | 6.8 MB            | 15.2 MB        |

On amd64 the executables take 20.4 MB for the Hub and 19.1 MB for the engine
once unpacked.

## Conditions

- Machine: Apple M4 Pro, Docker 29.8.0, the arm64 variant of the image.
- One configured daemon source pointing at a closed port, so every poll fails
  fast and nothing is ever imported. The database stays empty.
- `/data` is a tmpfs owned by UID `1654`.
- Cold start is timed from `docker run` to the first `200` on `/`, so it
  includes the container start as well as the Hub's own boot.
- Memory is the container figure reported by `docker stats`, which leaves out
  the page cache.
- The image sizes are the layer sizes read from the registry manifest for each
  architecture.

## What these figures do not say

The memory figure is a floor. A Hub that imports findings, keeps observation
days, serves the launcher and renders reports holds more, and this page carries
no figure for that until one is measured on a real fleet. The start and memory
figures are arm64 only. The amd64 image was sized, not run.

## Start on a filled database

Every start fills in what an older build left out: from each source's own
envelope, the service and the grouping attributes of a finding copy, and the
pairs every frozen finding of an incident copy shares, see
[ARCHITECTURE.md](ARCHITECTURE.md#what-the-database-holds). The first start
after the upgrade finds all of it to do. Measured on two seeded databases,
through `HubDatabase.InitializeAsync` in a Release build of the tests rather
than the native image, with the bundled SQLite 3.53.4, on the machine above with
the database on its internal SSD:

| Database                                                | Size     | First start after the upgrade | Every later start |
|---------------------------------------------------------|----------|-------------------------------|-------------------|
| 100,000 finding copies, 10,000 incidents of 20 findings | 312 MiB  | 2.3 s                         | 0.14 s            |
| 100,000 finding copies, 1,000 incidents of 1,000        | 1034 MiB | 6.5 s                         | 0.11 s            |

Each copy's envelope is about 900 bytes with two grouping attributes, and each
frozen finding about 1 KB with two, the same two across one incident. The first
start writes 200,000 attribute rows and 100,000 services, and derives the pairs
of every incident, which grows with the frozen findings held within
`Hub:Retention`: 200,000 in the first database, 1,000,000 in the second, where
every incident holds the daemon's cap of 1,000. A later start finds nothing to
write and only reads which copies need it.

That first start runs before the Hub listens, in one transaction. The chart's
startup probe gives it 60 s and its default limit 500m of CPU, so a database
holding several million frozen findings on slow nodes can fail the probe, and a
killed start begins again from nothing. Raise `resources.limits.cpu` for the
upgrade if so. The first start also holds every envelope it fills in, about
90 MB here, in SQLite's temporary storage, which writes to `/tmp` past its 2 MB
cache, and the chart mounts `/tmp` as a 32 MiB `emptyDir`. Whether the kubelet
counts that file, which SQLite deletes as it opens it, against the limit was not
checked.

## Access scope over 100,000 copies

The `visible` CTE that access control puts in front of a restricted findings
read, see [ARCHITECTURE.md](ARCHITECTURE.md#access-control),
selects 24,500 of 100,000 finding copies through 12 patterns, six service
prefixes and six attribute values, in about 0.2 s, three runs of 202 to 203 ms.
Measured by the explicit test `The_visible_cte_over_100k_copies_and_12_patterns`
in a Release build, on the machine above, the database on its internal SSD, with
one attribute row per copy:

```bash
./PerfSentinelHub.Tests/bin/Release/net10.0/PerfSentinelHub.Tests \
  -method '*.The_visible_cte_over_100k_copies_and_12_patterns' -explicit only -showLiveOutput
```

A copy no pattern reads costs every pattern's probe, so a narrow scope costs more
than a wide one: the same 12 patterns written to select 90,500 copies take
0.1 s. Nothing runs this CTE yet.

## Reproduce

```bash
IMG=ghcr.io/institut-du-numerique-responsable/perf-sentinel-hub:0.3.3
docker pull "$IMG"

docker run -d --name hub -p 18080:8080 \
  --tmpfs /data:uid=1654,gid=1654 \
  -e Hub__Sources__0__Id=bench \
  -e Hub__Sources__0__Name=Bench \
  -e Hub__Sources__0__Environment=test \
  -e Hub__Sources__0__BaseUrl=http://127.0.0.1:9 \
  -e Hub__Sources__0__ImportApiKey="$(openssl rand -hex 16)" \
  "$IMG"

until [ "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18080/)" = 200 ]; do sleep 0.005; done

sleep 60
docker stats --no-stream --format '{{.MemUsage}}' hub
docker rm -f hub
```

Time the `docker run` and the wait loop together to get the cold start. The
layer sizes come from the manifest of each platform entry in the image index.
