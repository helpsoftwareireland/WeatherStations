# Weather Station Aggregation Service: analysis and implementation plan

Prepared: 4 November 2026

Source: `NextGen Technical Assessment.pdf`, pages 1–2 (local assignment brief)

Status: Original design plan; implementation is now available. See [README.md](README.md) for execution instructions and [PERFORMANCE_RESULTS.md](PERFORMANCE_RESULTS.md) for measured evidence.

## 1. Goal and agreed scope

Build an ASP.NET Core service that accepts a JSON Lines stream of weather measurements and returns one JSON line per station containing minimum, mean, and maximum temperatures. Deliver a streaming .NET console client, tests, GitHub build checks, and a documented public repository.

Use .NET 10 LTS, a production-oriented design, and normal .NET commands without Docker. Target two to three working days. Reject invalid input at the first invalid line. Include disk-backed aggregation for station counts that exceed memory. Require repeatable smaller performance tests; make a billion-measurement run optional.

This is production-oriented within an assessment time budget. It does not promise production capacity, availability, or a specific throughput without measurement. Local implementation and verification are complete.

## 2. What the assignment explicitly requires

| Requirement | Source | Implementation implication |
|---|---|---|
| ASP.NET service accepts JSONL measurements | Page 1 | Read the request body incrementally; do not deserialize a JSON array. |
| A configurable single API key is required for all requests | Page 1 | Apply authentication globally, including any health or documentation endpoints. |
| `stationName` is a UTF-8 string of 1–100 bytes | Page 1 | Validate decoded text by UTF-8 byte count, not character count or escaped JSON length. |
| Station matching is case-insensitive | Pages 1–2 | Use the same comparison semantics in memory and on disk. |
| Temperatures are between -99.9 and 99.9 with one fractional digit | Page 1 | Validate precision and range; aggregate exact integer tenths. |
| Up to 1,000,000,000 measurements, representing 10+ GB | Page 1 | Stream end to end and avoid request buffering or one task per measurement. |
| Output names capitalize each word and lowercase its remaining letters | Page 1 | Preserve spaces and punctuation; match examples such as `St. John's`. |
| Calculate minimum, mean, maximum per station | Page 2 | Retain minimum, maximum, sum, and count rather than raw measurements. |
| Return JSONL sorted alphabetically by station name | Page 2 | Sort the completed station aggregates before writing results. |
| Configurable .NET console generator uploads measurements and prints results | Page 2 | Generate directly into the HTTP upload stream and stream the response to stdout. |
| Public GitHub source and README | Page 2 | Document execution, configuration, examples, assumptions, and performance. |

The brief's output example includes stations and measurements absent from its three-line input example. Treat those examples as format illustrations, not a single matching input/output test.

## 3. Proposed contract and explicit assumptions

These choices fill gaps in the PDF and should appear in the README and tests.

| Area | Proposed behavior |
|---|---|
| Endpoint | `POST /measurements/aggregate`; request and successful response use `application/x-ndjson`. |
| API key | Require exactly one `X-Api-Key` header. Missing, duplicate, or invalid keys return 401. Reject startup if the configured key is missing or empty. |
| Configuration | Support standard ASP.NET configuration, including `Authentication__ApiKey`. Keep real keys out of committed configuration and command-line arguments. |
| Request isolation | Each request represents an independent aggregation; no cross-request accumulation. |
| Input encoding | Strict UTF-8. Accept LF and CRLF and a final line without a newline. Reject invalid encoding and a byte-order mark. |
| Empty input | Return 200 with an empty JSONL body. Blank lines within input are invalid. |
| JSON schema | Require one object per line with exactly one string `stationName` and one numeric `temperature`; reject unknown or duplicate properties, nulls, comments, trailing commas, and nested values. |
| Temperature notation | Accept ordinary decimal JSON numbers with exactly one fractional digit, including `12.0`; reject `12`, `12.00`, scientific notation, and quoted numbers. This strict lexical interpretation needs clear documentation. |
| Name whitespace | Preserve leading, trailing, and repeated spaces; do not trim. The PDF does not prohibit whitespace-only names, so accept them if they meet the byte limit. |
| Matching | Use `StringComparer.OrdinalIgnoreCase`. Preserve accents and punctuation; do not equate accented and unaccented names or apply Unicode canonical normalization. |
| Output casing | Use invariant Unicode casing. Start words at the beginning and after whitespace or punctuation, except an apostrophe inside letters does not start a new word. Handle both `'` and `’` internally so `st. john's` becomes `St. John's`. Preserve separators exactly. |
| Unicode variants | For equivalent names with different normalized spellings, choose the ordinal-smallest normalized display name. This makes output independent of input order. Test this rule explicitly. |
| Sorting | Order normalized output names with ordinal, case-insensitive comparison, then ordinal comparison as a deterministic tie-breaker. This is a culture-independent interpretation of “alphabetically,” not linguistic collation. |
| Mean | Round to one decimal place using midpoint rounding away from zero. Render all three temperatures with one fractional digit and invariant decimal punctuation; render negative zero as `0.0`. |
| Errors | Stop on the first invalid line. Return a JSON problem response with a stable error code and 1-based line number, without echoing the raw measurement or key. Do not emit partial station results. |
| Limits | Document configurable line-size, temporary-disk, concurrency, and time limits. Enforce the measurement ceiling of one billion. Operational limit breaches are distinct from malformed JSON. |

Word boundaries beyond the PDF examples are an assumption. Test hyphens, apostrophes, periods, repeated spaces, accented letters, and non-Latin names instead of relying uncritically on a generic title-case function.

## 4. Architecture

Use a small solution with clear responsibilities rather than a large application framework:

```text
Broadridge.sln
src/
  WeatherAggregation.Api/          HTTP endpoint, authentication, configuration
  WeatherAggregation.Core/         Validation, naming rules, aggregate arithmetic
  WeatherAggregation.Storage/      Temporary SQLite store and bounded batching
  WeatherAggregation.Client/       Console generator and streaming HTTP client
tests/
  WeatherAggregation.UnitTests/
  WeatherAggregation.IntegrationTests/
  WeatherAggregation.Performance/  Repeatable end-to-end performance runner
.github/workflows/ci.yml
README.md
```

Avoid an ORM in the ingestion path. Use `Microsoft.Data.Sqlite` with parameterized commands and explicit transactions. Use an integration-test host plus a real Kestrel smoke test; an in-process host alone cannot verify transport limits.

### Request processing

1. Authenticate before reading the body; reject unsupported media types.
2. Acquire a configurable processing slot before ingestion. Return a documented capacity response when full instead of retaining an unbounded queue of uploads.
3. Create a private, request-specific temporary directory and SQLite database.
4. Read bounded byte buffers, frame JSONL records, and enforce the line limit before allocating a complete record. Handle records spanning buffer boundaries and multiple records in one buffer.
5. Parse, validate, and convert each temperature to integer tenths.
6. Update a bounded dictionary of partial station aggregates. Flush when its entry or estimated byte budget is reached.
7. Merge each batch into SQLite using a reused parameterized UPSERT inside a transaction. Clear the batch after successful persistence.
8. After valid end-of-input, flush the final batch and prepare the ordered result query.
9. Stream database rows as JSONL using bounded output buffers. Do not collect the complete result set in memory.
10. Close resources and delete temporary files on success, validation failure, cancellation, and exceptions.

No successful response body starts before input validation completes. Once response streaming begins, a later disk or network failure cannot be converted into a clean JSON problem response; log the failure and terminate the response. The client must treat a broken response as a failed request.

### Exact arithmetic

Store `minTenths` and `maxTenths` as integers and `sumTenths` and `count` as signed 64-bit integers. A billion measurements at the positive limit produce a sum of 999,000,000,000 tenths, safely within a signed 64-bit integer. A 32-bit sum is insufficient.

Compute the final mean with decimal arithmetic: `sumTenths / (10m * count)`, rounded once at output. Never average partial means. A batch merge adds sums and counts and takes the lower minimum and higher maximum.

### Disk-backed aggregation and ordering

Create one aggregate row per station with the identity name, deterministic display name, minimum, maximum, sum, and count. Register a custom SQLite collation backed by .NET ordinal-ignore-case comparison for station identity and use it consistently for uniqueness and lookup. Register matching output ordering semantics for display names. SQLite's built-in `NOCASE` is insufficient for general Unicode names.

Bound the SQLite page cache and configure temporary sorting to use files when needed. Let SQLite perform the final order operation without materializing all station names in .NET. A single reader and writer per request keeps the first implementation understandable; synchronous SQLite operations should run within bounded request concurrency rather than through a task per record.

This design bounds application memory by configured batch and cache sizes, rather than total measurement or station count. Temporary storage still grows with distinct stations, indexes, journals, and sorting work. It does not provide unlimited capacity. Enforce a per-request disk budget, reserve capacity before accepting large uploads where practical, and fail cleanly when storage is exhausted. Deleting abandoned request directories after a crash requires a safe startup scavenger limited to the service-owned temporary root.

Expected work is approximately O(N) parsing plus indexed aggregate merges and O(S log S) final sorting, where N is measurement count and S is distinct station count. Actual database costs depend on batch reuse and index behavior. Near-unique names create a much more demanding workload than a billion readings from a small station set.

### Large-upload hosting

Configure Kestrel specifically for the aggregation endpoint so the default request-body limit does not block 10+ GB uploads. Avoid blanket removal of unrelated endpoint limits. Enforce line, measurement, concurrency, disk, and time budgets separately.

Document any reverse-proxy body-size, buffering, idle-timeout, and total-request-timeout requirements. Use HTTPS outside local development. Preserve appropriate slow-upload protection, configure the console client's long-running request timeout explicitly, and propagate request cancellation through reading, batch processing, and result writing.

Choose an initial bounded line limit, such as 16 KiB, and document that it constrains otherwise-valid excessive JSON whitespace. The 100-byte name constraint alone does not impose a maximum JSON record length.

## 5. Console test client

Expose measurement count, station count, random seed, service URL, and timeout as options. Read the API key from configuration or an environment variable. Validate counts before upload and use 64-bit counters.

Generate each measurement lazily into custom streaming HTTP content. Do not create a billion-element collection, a complete JSONL string, or a mandatory intermediate file. Use bounded flush intervals rather than flushing every line. Generate integer tenths so all temperatures satisfy the input contract.

Use seeded data for reproducibility and optionally vary capitalization and multi-word names. Include a fixture with known expected results. For high-cardinality generation, derive names from numeric identifiers rather than retaining every generated name.

Use response-header streaming and copy returned JSONL incrementally to stdout. Send timing and diagnostic information to stderr so stdout remains valid JSONL. On non-success HTTP status, show the problem response and exit nonzero. Handle early server rejection while uploading, cancellation, and broken response streams. Do not automatically retry a huge upload.

## 6. Verification and acceptance criteria

### Correctness tests

- Interleaved stations, case variants, a single measurement, and all-equal values.
- Every normalization example from the PDF, especially `st. john's`, `ST. JOHN'S`, and `St. John's` as a single station.
- UTF-8 names at 1, 100, and 101 bytes, including multibyte characters and JSON-escaped characters.
- Temperature boundaries, out-of-range values, wrong precision, exponents, nulls, duplicate properties, malformed JSON, invalid UTF-8, and excessive line lengths.
- LF, CRLF, a missing final newline, empty input, and invalid blank lines.
- Positive and negative midpoint mean rounding, negative zero, and large counts/sums tested directly without generating a billion records.
- Different host cultures produce identical matching, normalization, ordering, and numeric formatting.
- Very small configured batches force repeated disk merges; results must match an independent, simple reference implementation on small randomized fixtures.
- Input-order permutations produce identical output, including Unicode case variants.
- Output has one JSON object per line, valid escaping, sorted station names, and exactly one decimal place per numeric field.

### Integration and operational tests

- Missing, wrong, duplicate, and correct API keys; missing configured key prevents startup. All mapped endpoints require authentication.
- Invalid input near the end returns an error without partial successful output.
- Concurrent requests remain isolated and respect the configured capacity limit.
- Client disconnect, cancellation, disk-budget breach, simulated storage failure, and normal completion release resources and temporary files.
- A real Kestrel upload exceeding its usual default body limit proves the intended hosting configuration works.
- The console client uploads incrementally and returns exactly the expected fixture output.

### Performance evidence

Run Release builds with fixed seeds and record hardware, SDK/runtime, configuration, measurement count, distinct station count, input bytes, elapsed upload/processing/response time, throughput, peak process memory, and peak temporary-disk usage. Performance numbers must come from actual runs.

Use at least three workload shapes: many readings across few stations; mixed station popularity; and mostly unique station names that force substantial disk usage. Compare growing N with fixed S to check for input-size-dependent memory growth, then grow S to verify batch/cache bounds and disk behavior. Include both service and generator memory observations.

Use a larger-than-memory workload where practical. Keep CI performance smoke tests small and free of brittle wall-clock thresholds. Provide a separate optional billion-measurement command, with estimated time and disk requirements based on smaller measured runs. Passing smaller tests is not proof of billion-row capacity.

## 7. Delivery schedule

| Time | Work | Exit condition |
|---|---|---|
| Day 1, morning | Solution setup, contract, configuration, authentication, numeric and naming rules | Core rules and API-key tests pass. |
| Day 1, afternoon | Bounded JSONL framing, validation, aggregate model, SQLite collation and batch merges | Small inputs produce correct disk-backed results; forced multi-batch tests pass. |
| Day 2, morning | Ordered output, cancellation, cleanup, capacity and disk limits, hosting configuration | End-to-end service handles success and failure without retained temporary files. |
| Day 2, afternoon | Streaming console client and integration tests | Real client/server fixture and large-upload smoke test pass. |
| Day 3 | Performance runs, fixes, GitHub workflow, README, final review | Evidence recorded, clean-checkout instructions verified, repository ready for publication. |

The disk-backed requirement and Unicode consistency are the largest schedule risks. A two-day result may leave less time for tuning and failure injection; reserve the third day for verification. If time is short, reduce optional tooling and tuning rather than remove streaming, exact arithmetic, disk-backed correctness, or required deliverables.

## 8. GitHub checks and README

Configure GitHub Actions for pushes and pull requests to install .NET 10, restore, build in Release, and run unit and integration tests. Run the cross-platform test suite on Windows and Linux where feasible, and upload test reports on failure. Keep secrets and generated large datasets out of the repository. Do not place the optional billion-row run on normal CI.

The README must include prerequisites; exact restore/build/test/run commands; API-key configuration using environment variables; a small copyable JSONL example with independently verified output; client options; rounding and Unicode rules; error responses; temporary-storage and hosting configuration; concurrency behavior; performance results and limitations; and the optional large-run procedure.

Final completion checklist:

- [x] Service implements the PDF's functional service requirements.
- [x] Client generates and uploads without full buffering and prints returned JSONL.
- [x] Disk merges and sorting work with station counts exceeding the in-memory batch.
- [x] Correctness, integration, authentication, cleanup, and additional-feature checks pass: 45 unit tests and 21 integration tests.
- [x] Repeatable performance evidence and capacity limitations are documented.
- [x] Fresh-source restore, build, and test commands work with .NET 10; the console executable is exercised against real Kestrel.
- Hosted build and test results are tracked in [GitHub Actions](https://github.com/helpsoftwareireland/WeatherStations/actions).
- [x] Source and README are published to [WeatherStations](https://github.com/helpsoftwareireland/WeatherStations).

Latest local validation: 4 November 2026, using isolated source copies. Restore succeeded, the latest Release build completed with zero warnings/errors, and all 66 tests passed. The one-command launcher also passed its Windows PowerShell smoke test. Additional features are documented in [EXTRA_FEATURES.md](EXTRA_FEATURES.md). Performance testing covers up to one million measurements; the optional billion-row run remains unperformed.

## 9. References

- Assignment PDF, pages 1–2: authoritative source for functional requirements.
- [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy): .NET 10 is an LTS release supported through November 2028; use a current supported patch during implementation.
- [Kestrel configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-10.0): request-body limits and hosting controls.
- [Microsoft.Data.Sqlite collation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/collation): built-in `NOCASE` covers ASCII letters; custom collations are supported.
- [Microsoft.Data.Sqlite bulk insert guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/bulk-insert): transactions and reused parameterized commands reduce insertion overhead.

The architecture, limits, contract choices, and schedule above are engineering proposals based on the assignment and agreed preferences; they are not additional requirements stated by Broadridge.
