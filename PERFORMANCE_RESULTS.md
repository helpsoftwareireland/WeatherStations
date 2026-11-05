# Local performance evidence

Measured on 4 November 2026 in Release configuration using the included end-to-end performance runner. These are development-machine observations, not a service-level guarantee.

These baseline measurements predate the Swagger, console-progress, request-correlation, and metrics additions described in [EXTRA_FEATURES.md](EXTRA_FEATURES.md).

## Setup

- Windows 10.0.26300, x64, 16 logical processors.
- .NET SDK 10.0.403; runtime .NET 10.0.14.
- Random seed 42, 1000 distinct entries per batch, 8192 KiB SQLite cache target.
- A fresh Kestrel process for each workload; no request pre-warmup.
- Real loopback HTTP upload and response streaming to a sink; generator and server share the machine.
- Default service disk allowance and concurrency settings.

Commands, after the Release solution build:

```text
dotnet run --project tests/WeatherAggregation.Performance -c Release --no-build -- 100000
dotnet run --project tests/WeatherAggregation.Performance -c Release --no-build -- 1000000
```

## Observations

| Measurements | Workload | Station range | Input MB | Total seconds | Rows/second | Server peak MiB | Sampled temp MiB |
|---:|---|---:|---:|---:|---:|---:|---:|
| 10,000 | Uniform | 10 | 0.564 | 0.290 | 34,482 | 63.6 | 0.024 |
| 100,000 | Uniform | 10 | 5.640 | 0.578 | 173,015 | 66.3 | 0.012 |
| 100,000 | Mixed | 10,000 | 5.640 | 1.091 | 91,692 | 71.3 | 1.333 |
| 100,000 | Unique | 100,000 | 5.640 | 2.545 | 39,290 | 83.7 | 10.211 |
| 100,000 | Uniform, second run | 10 | 5.640 | 0.564 | 177,460 | 63.0 | 0.016 |
| 1,000,000 | Uniform | 10 | 56.400 | 2.291 | 436,397 | 76.8 | 0.012 |
| 1,000,000 | Mixed | 100,000 | 56.400 | 6.876 | 145,423 | 85.5 | 9.400 |
| 1,000,000 | Unique | 1,000,000 | 56.400 | 21.184 | 47,206 | 84.6 | 101.977 |

MB means 1,000,000 bytes; MiB means 1,048,576 bytes. “Station range” is the generator configuration, not a separately counted distinct-output total. Mixed traffic concentrates about 80% of readings in the first ten stations. Uniform cycles through the given range; unique uses a new name per reading.

For the million-unique-station run, response headers arrived after 16.284 seconds and copying the response completed in another 4.900 seconds. Client process lifetime peak working set reached about 57.6 MiB during the million-row matrix. The client does not retain generated measurements or returned results.

The million-unique-station workload exceeded the 1000-entry batch by a factor of 1000 while server memory remained around 85 MiB. It used about 102 MiB of sampled temporary files. This supports the intended bounded-batch behavior at the tested scale. It does not establish a hard memory cap or prove capacity at one billion distinct stations.

Raw measurements, including timings and byte counts:

- [100,000-row matrix](performance-results/100k.jsonl)
- [1,000,000-row matrix](performance-results/1m.jsonl)

## Limits and larger-run planning

The peak working set is the process lifetime peak reported by the OS. Temporary file sizes are sampled every 20 ms and may miss short-lived journal peaks. Client memory peaks are cumulative across a matrix. Measurements include startup/JIT effects during the first request, HTTP overhead, the shared machine's load, and response serialization. There is no statistical confidence interval from these few runs, no isolated server CPU timing, and no physical-disk specification captured.

For rough planning only, linear extrapolation of the million-row observations gives about 38 minutes for a billion rows with a small station set, about 115 minutes for the mixed workload, and about 5.9 hours for a billion unique stations. Cache and index effects can change these substantially. The unique-station temporary-storage extrapolation is roughly 107 GB, far beyond the default database allowance; provision storage and raise both service/client timeouts before attempting such a workload. These are estimates, not measured billion-row results.

The largest completed performance workload contains one million measurements. Neither a full billion-row run nor a larger-than-physical-memory run was performed. The automated real-Kestrel integration test separately verifies an upload above the usual 30 MB request-body limit.
