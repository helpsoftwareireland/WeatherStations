# Weather Station Aggregation

An ASP.NET Core 10 service and streaming .NET console client for the Broadridge weather-station assessment. The service accepts JSONL and returns minimum, mean, and maximum temperatures per station, sorted by normalized station name.

For the one-command launcher, protected Swagger, client progress, request IDs, and operational metrics, see [EXTRA_FEATURES.md](EXTRA_FEATURES.md).

The request is never loaded in full. A bounded dictionary combines measurements before merging aggregates into a request-specific SQLite database. A disk index supplies sorted output without collecting all results in memory. Each upload is independent.

## Prerequisites and build

- .NET 10 SDK, with a current supported patch. `global.json` accepts installed .NET 10 feature bands.
- Windows or Linux and writable temporary storage. No database server or Docker is required.
- Internet access to NuGet on the first restore.

From the repository root:

```text
dotnet restore Broadridge.sln
dotnet build Broadridge.sln --configuration Release --no-restore
dotnet test Broadridge.sln --configuration Release --no-build --logger trx --results-directory TestResults
```

The tests include a real Kestrel upload larger than 30 MB, in addition to unit and in-process integration tests. GitHub Actions runs the build and tests on Windows and Linux for pushes and pull requests. The performance runner is built but its larger workloads are not run by normal CI.

Local verification on 4 November 2026: the original application and the additional features were validated in isolated source copies. The latest Release build produced zero warnings/errors, and all 66 tests passed (45 unit and 21 integration tests). The launcher also passed its Windows PowerShell smoke test. Startup cleanup covers a crash before a temporary-store lease was created; repeated disposal is safe. Hosted GitHub runs are pending while the project remains local.

## Run the service

Set a strong API key in the environment. There is no default key and the service refuses to start without one. Use a dedicated local test key while developing; do not commit real keys.

PowerShell:

```powershell
$env:Authentication__ApiKey = 'replace-with-your-local-key'
dotnet run --project src/WeatherAggregation.Api --configuration Release --no-build -- --urls http://localhost:5080
```

Bash:

```bash
export Authentication__ApiKey='replace-with-your-local-key'
dotnet run --project src/WeatherAggregation.Api --configuration Release --no-build -- --urls http://localhost:5080
```

The API key is configurable through standard ASP.NET configuration as `Authentication:ApiKey`. Environment variables use double underscores. All requests, including `GET /health`, unknown routes, and invalid methods, pass through the API-key check. Supply exactly one `X-Api-Key` header. Key comparisons use fixed-size SHA-256 hashes and constant-time comparison.

Protected browser documentation at `/docs/` also accepts Basic login with username `apikey` and the same API key as the password. This alternative is limited to documentation; API and metrics requests still require `X-Api-Key`. See [EXTRA_FEATURES.md](EXTRA_FEATURES.md) for the browser workflow.

Use HTTPS when sending a key outside a trusted local development environment. TLS termination, certificate provisioning, and deployment infrastructure are not included.

## Run the client

In another terminal, set the same `Authentication__ApiKey` environment variable, then:

```text
dotnet run --project src/WeatherAggregation.Client --configuration Release --no-build -- --url http://localhost:5080 --measurements 100000 --stations 1000 --seed 42 --timeout-seconds 7200
```

Options:

| Option | Default | Meaning |
|---|---|---|
| `--url` | `http://localhost:5080` | Service origin; client posts to `/measurements/aggregate`. |
| `--measurements` | `100000` | Number of generated rows, from 0 to 1 billion. |
| `--stations` | `1000` | Station-name range, from 1 to 1 billion. |
| `--seed` | `42` | Random seed for temperatures and mixed workloads. |
| `--timeout-seconds` | `7200` | Total request timeout, including upload and response. Maximum 604800. |
| `--workload` | `uniform` | `uniform` cycles through stations; `mixed` sends roughly 80% to the first ten; `unique` assigns a new station to every row and overrides the station range. |
| `--fixture` | `false` | `true` sends the three-line example below instead of generated rows. |
| `--progress` | `true` | Report generation/upload progress to stderr about once per second and at completion. |
| `--help` | — | Print usage. |

Names alternate capitalization to exercise case-insensitive matching. Temperatures are generated as integer tenths. The client writes JSONL results to stdout and diagnostics to stderr, exits nonzero on failure, and supports Ctrl+C. Redirect stdout if results are large. There are no automatic retries and no complete request file or collection in memory.

## API and verified example

`POST /measurements/aggregate`

Headers:

```text
X-Api-Key: <configured key>
Content-Type: application/x-ndjson
```

Input:

```jsonl
{"stationName":"Hamburg","temperature":12.0}
{"stationName":"Bulawayo","temperature":8.9}
{"stationName":"HAMBURG","temperature":34.2}
```

Output (`application/x-ndjson; charset=utf-8`):

```jsonl
{"stationName":"Bulawayo","min":8.9,"mean":8.9,"max":8.9}
{"stationName":"Hamburg","min":12.0,"mean":23.1,"max":34.2}
```

Run this fixture through the console client:

```text
dotnet run --project src/WeatherAggregation.Client --configuration Release --no-build -- --fixture true
```

Or save the example as `input.jsonl` and send it with curl (use `curl.exe` in Windows PowerShell):

```bash
curl --fail-with-body -H "X-Api-Key: $Authentication__ApiKey" -H 'Content-Type: application/x-ndjson' --data-binary @input.jsonl http://localhost:5080/measurements/aggregate
```

The console fixture is tested against exact expected output. The original PDF's input and output examples are separate format illustrations; its output includes readings absent from its input example.

## Validation and naming decisions

- Strict UTF-8, no byte-order mark; LF and CRLF accepted. A final newline is optional. Empty input returns an empty successful body. Blank lines are invalid.
- Each line must be one JSON object containing exactly one `stationName` string and one `temperature` number. Unknown or duplicate properties, nulls, comments, and trailing commas are rejected.
- Decoded station names must occupy 1–100 UTF-8 bytes. Spaces are preserved, including leading, trailing, repeated, and whitespace-only names. Escaped JSON characters count by decoded byte length.
- A temperature must be between -99.9 and 99.9 with exactly one fractional digit in ordinary decimal notation. `12.0` is valid; `12`, `12.00`, `1.2e1`, and `"12.0"` are rejected. This is an explicit strict interpretation of the brief.
- Names match using .NET ordinal, case-insensitive comparison in both the batch dictionary and SQLite custom collation. Accents are preserved. Canonically equivalent Unicode sequences are not combined. SQLite's ASCII-only built-in `NOCASE` is not used.
- Output names use invariant Unicode casing, capitalizing words after spaces and punctuation while retaining all separators. Apostrophes between letters do not start a new word: `ST. JOHN'S` becomes `St. John's`; hyphens do: `ÉVRY-SUR-SEINE` becomes `Évry-Sur-Seine`. Unicode code points are handled without splitting surrogate pairs.
- If equivalent identity names produce different display spellings, choose the ordinal-smallest normalized spelling, independent of input order.
- Output ordering compares normalized names with ordinal-ignore-case comparison and an ordinal tie-breaker. This is deterministic across host cultures, rather than a locale-specific dictionary order.
- Minimum and maximum are exact. Mean is rounded once to one decimal, with midpoint rounding away from zero. All three values render exactly one decimal digit; negative zero renders as `0.0`. JSON strings may contain standard Unicode escapes.

## Errors and request lifecycle

The first invalid line stops processing. No successful station output is sent until the entire input has validated. Errors use `application/problem+json`, with `code` and a 1-based `lineNumber` when applicable, for example:

```json
{"title":"Expected a stationName of 1–100 UTF-8 bytes and a temperature from -99.9 to 99.9 with exactly one fractional digit.","status":400,"code":"invalid_measurement","lineNumber":3}
```

Problem responses may include framework fields in addition to the example.

| Status | Codes / meaning |
|---|---|
| 400 | `invalid_measurement`: invalid encoding, schema, name, or temperature. |
| 401 | `unauthorized`: missing, wrong, or duplicate API key. |
| 408 | `processing_timeout`: configured processing time exceeded. |
| 413 | `line_too_long` or `measurement_limit`. |
| 415 | `unsupported_media_type`. |
| 503 | `capacity_exceeded`; `Retry-After: 5`, with no unbounded upload queue. |
| 507 | `storage_limit` for SQLite capacity exhaustion, or `storage_or_io_failure`. |
| 500 | `storage_failure` for other SQLite failures. |

Temporary files are disposed on successful completion, invalid input, cancellation, timeout, and storage failure. Cleanup failures are logged. On startup, the service removes recognized, flat request directories older than 24 hours only when their exclusive lease is not held. Unknown directories and symbolic links are left alone. Use a service-owned temporary root with filesystem permissions restricted to the service account.

Client disconnects abort processing. A failure after output begins aborts the HTTP response; an already-started stream cannot be replaced with an error document. The console client treats broken transport responses as failures. Partial output redirected to a file should be discarded after a nonzero exit.

## Resource and hosting configuration

Override these with `Aggregation__<Name>` environment variables or the `Aggregation` section in application configuration:

| Setting | Default | Limits / effect |
|---|---|---|
| `MaxLineBytes` | 16384 | 128–1048576; raw record bytes excluding LF or CRLF. Excessive JSON whitespace can exceed this even with a valid short name. |
| `BatchEntries` | 10000 | 1–1000000 distinct identities per in-memory batch. Names have a fixed 100-byte maximum, bounding each entry's size. |
| `CacheKiB` | 8192 | 64–1048576 SQLite cache target per request; a cache target is not a hard process-memory limit. |
| `MaxMeasurements` | 1000000000 | 1–1000000000 per request. |
| `MaxConcurrentRequests` | 2 | 1–128; includes ingestion and response streaming. |
| `TimeoutSeconds` | 7200 | 1–604800; ingestion, aggregation, and output. SQLite calls are synchronous, so checks occur between operations. |
| `MaxDiskBytes` | 32212254720 (30 GiB) | At least 1 MiB; conservative per-request storage allocation described below. |
| `TempRoot` | OS temp directory + `broadridge-weather` | Dedicated writable directory; generated request directories have user-only permissions on Unix and inherit Windows ACLs. |

SQLite's maximum database page count is set to roughly one third of `MaxDiskBytes`, reserving the rest for its rollback journal and overhead. An incrementally maintained output-order index avoids a final external sort. This is a conservative database/journal allowance, not a filesystem quota or reservation. Multiple uploads share free disk space; use an OS volume quota for a hard bound and provision enough free space for concurrent requests. The default therefore permits roughly 10 GiB of database data including both indexes, not 30 GiB of distinct station records. Actual station capacity depends on name lengths and index overhead. Disk exhaustion returns a failure; unlimited unique stations are not promised.

Kestrel's usual request-body size cap is disabled only inside the aggregation endpoint, before reading the body. Other routes retain their normal limits. Slow-upload protection remains at Kestrel defaults. If deploying behind a reverse proxy, explicitly configure its body-size limit, request buffering, idle timeout, and total request timeout for multi-gigabyte streams. Align the client timeout with the service and proxy. The two-hour default may be too short for a billion-row run: measure first and raise it deliberately.

## Performance runner

Build in Release, then run from the checkout:

```text
dotnet run --project tests/WeatherAggregation.Performance --configuration Release --no-build -- 100000
```

The runner starts a fresh real Kestrel process per workload and sends four seeded workloads: 10% of the requested count with ten stations; the full count with ten stations; a mixed popularity distribution; and unique stations. It streams response data to a sink and prints JSONL metrics. Each server uses a 1000-entry batch and the default 8 MiB cache target. It records runtime, OS, processor count, input bytes, total time, response-header time, response-copy time, throughput, server peak working set, client lifetime peak working set, and sampled peak temporary bytes. Samples occur every 20 ms and can miss brief disk peaks; client peak memory is cumulative across workloads. Header time includes upload and processing and is not an isolated server CPU measurement.

Optional billion-measurement matrix, including a billion unique stations:

```text
dotnet run --project tests/WeatherAggregation.Performance --configuration Release --no-build -- 1000000000
```

That command is intentionally demanding and can exceed default disk/time limits. Use smaller runs to estimate capacity before starting it. For a billion readings across only 10000 stations, use the console client instead:

```text
dotnet run --project src/WeatherAggregation.Client --configuration Release --no-build -- --measurements 1000000000 --stations 10000 --timeout-seconds 86400
```

Set `Aggregation__TimeoutSeconds=86400` before starting the service for this example. Redirect output if appropriate. No full billion-row capacity or throughput claim is made from smaller tests. Results from the development machine are recorded in [PERFORMANCE_RESULTS.md](PERFORMANCE_RESULTS.md).

## Design tradeoffs and scope

Integer tenths avoid floating-point accumulation error. The largest supported sum is 999,000,000,000 tenths, which requires 64-bit arithmetic; mean is calculated using decimal division. Batch merges combine sums and counts, never partial means.

SQLite provides indexed disk aggregation without an external database service. Reused parameterized UPSERT commands and explicit transactions reduce per-row overhead. Memory is governed by line/buffer size, bounded batch entries, SQLite cache behavior, runtime overhead, and concurrent requests, rather than total upload size. Database work is synchronous and high-cardinality workloads can be much slower. This implementation prioritizes correctness and a reviewable design over a custom external sorting engine, parallel parsing, or peak billion-row throughput.

There is no durable job queue, resumable upload, cross-request persistence, multi-key identity system, deployment infrastructure, or Docker setup. Public GitHub publication is not performed by this implementation task; the workflow is ready to run when the source is pushed to a repository.

The original analysis and assumptions are in [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md). The tests exercise arithmetic, naming, validation, Unicode identity, bounded framing, forced disk merges, input-order independence, disk exhaustion, cleanup, authentication, capacity, timeout, a real large upload, and the console fixture.
