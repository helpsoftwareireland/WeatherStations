# Additional service and usability features

This document covers the additions to the original weather aggregation assignment: protected Swagger/OpenAPI documentation, console progress, request correlation and metrics, and a one-command local launcher. The original aggregation contract remains documented in [README.md](README.md).

## 1. Start everything with one command

Open PowerShell **5.1 or later** in `C:\Projects\Broadridge`, then run:

```powershell
.\Start-Local.ps1 -ShowApiKey
```

The launcher builds the Release solution, generates a temporary 256-bit API key if `Authentication__ApiKey` is unset, starts the service on loopback port 5080, waits for an authenticated health check, and runs the three-measurement example. It then keeps the service running until you press **Ctrl+C**. It stops only the service process it created and restores the previous environment key on exit.

`-ShowApiKey` displays the local key so you can log into Swagger or use another client. Omit this switch to keep the key hidden. The key is passed to child processes through the environment; it is not placed in command-line arguments or service log files. If you have already configured `Authentication__ApiKey`, the launcher reuses it.

Other options:

```powershell
# Use another port if 5080 is occupied.
.\Start-Local.ps1 -Port 5081 -ShowApiKey

# Build, start, run the example, then stop automatically.
.\Start-Local.ps1 -Port 5081 -SmokeTest
```

Stop any service you previously launched from this checkout before rebuilding: Windows can lock its executable while it runs. The launcher will not terminate unrelated service instances or reuse a server already occupying its chosen port. Startup failures point to log files under `.local/launcher/<run-id>/`. These local logs and generated files are ignored by Git. An initial build may need NuGet access.

The launcher is a development convenience. It binds to loopback HTTP; configure HTTPS for use outside local development. Its output includes build messages and the example, so use the console client directly when redirecting only JSONL results to a file.

## 2. Protected Swagger and OpenAPI

With the service running, open:

```text
http://localhost:5080/docs/
```

The browser prompts for credentials:

- Username: `apikey`
- Password: the configured API key, or the key displayed by the launcher.

This browser login protects the documentation page, its JavaScript/CSS assets, and its OpenAPI specification. It uses the same single API key as the rest of the application. Basic authentication is accepted only under `/docs`; service operations and metrics continue to require `X-Api-Key`. A wrong or duplicate `X-Api-Key` is never rescued by a valid Basic login.

In Swagger, click **Authorize**, enter the API key, and then use **Try it out** on `POST /measurements/aggregate`. The JSONL example is ready to send as raw text. Do not wrap the example in quotes or a JSON array. Swagger is for small interactive examples; use the streaming console client for large uploads.

The API documentation includes:

- Required authentication and optional `X-Request-Id` header.
- JSONL request/response examples and measurement/result schemas.
- Validation, Unicode casing, sorting, and mean-rounding rules.
- Error status codes, stable error codes, line numbers, and request IDs.
- Health and operational metrics endpoints.

The machine-readable document is at `/docs/openapi.json`. Tools can fetch it with `X-Api-Key`. The OpenAPI document is embedded in the API assembly and its internal references and executable examples are tested. It is maintained explicitly because the request body is a raw JSONL stream rather than an ordinary JSON model.

Swagger authorization is not persisted in local storage. Browsers may cache Basic credentials for the documentation session; close the browser session or use a private window when sharing a machine. Never put the key in a URL. No key is embedded in the specification or served HTML.

## 3. Console progress and request IDs

Progress is enabled by default:

```powershell
$env:Authentication__ApiKey = 'your-local-key'
dotnet run --project src/WeatherAggregation.Client -c Release --no-build -- --measurements 1000000 --stations 10000
```

Approximately once per second, the client writes a line to **stderr**, for example:

```text
Uploading / awaiting results: generated 450000/1000000 (45.0%); uploaded 24.20 MiB; elapsed 3.1s; 145161 rows/s, 7.81 MiB/s.
```

Values in that line are illustrative. A final snapshot is emitted even for very short requests. Progress shows generated measurement count and percentage, bytes written to the upload transport, elapsed time, and average generation/upload throughput. When response headers arrive, the phase changes to `Receiving results`.

Generated rows may be slightly ahead of bytes sent because output is buffered. Uploaded bytes mean bytes handed to the HTTP transport, not a server acknowledgement. The client cannot know the server's aggregation percentage; it reports `Uploading / awaiting results` during that phase rather than inventing server progress. Throughput averages include elapsed waiting time.

JSONL results remain on **stdout**, so this still produces a clean results file:

```powershell
dotnet run --project src/WeatherAggregation.Client -c Release --no-build -- --measurements 1000000 --stations 10000 > results.jsonl
```

To disable periodic progress and its final snapshot:

```text
dotnet run --project src/WeatherAggregation.Client -c Release --no-build -- --progress false
```

Request IDs, errors, and completion timing remain on stderr. The client generates a request ID before uploading and displays the server's returned ID when available, making a failed run easier to find in service logs.

## 4. Correlate requests with logs

Every HTTP response carries `X-Request-Id`, including authentication failures. Application-generated problem responses also contain `requestId`. A caller may supply its own ID using 1–64 ASCII letters, digits, dots, underscores, or hyphens. Multiple, oversized, or invalid values are replaced with a generated ID.

For example:

```powershell
Invoke-WebRequest -Uri http://localhost:5080/health -Headers @{
    'X-Api-Key' = $env:Authentication__ApiKey
    'X-Request-Id' = 'local-demo-001'
}
```

The service logs completion with that ID, a bounded route category, HTTP status, and elapsed milliseconds. A logging scope also carries `RequestId`. API keys, Basic credentials, request bodies, and station names are not logged by these additions. Request IDs are never used as metric labels, avoiding a new metric series for every upload.

## 5. Operational metrics

Fetch the protected snapshot with:

```powershell
Invoke-RestMethod -Uri http://localhost:5080/metrics -Headers @{
    'X-Api-Key' = $env:Authentication__ApiKey
}
```

| JSON field | Meaning |
|---|---|
| `completedRequests` | Completed HTTP requests since this process started, including health, docs, metrics, and rejected requests. |
| `rejectedRequests` | Requests ending with HTTP status 400 or higher; includes validation, authentication, capacity, timeout, storage, and other failures. Client-aborted requests are recorded as 499. |
| `activeAggregations` | Requests holding an aggregation slot, including upload, processing, and response streaming. |
| `acceptedMeasurements` | Measurements in fully validated and persisted uploads; an ensuing output failure does not subtract them. Invalid uploads contribute zero. |
| `totalRequestSeconds` | Sum of HTTP request durations, including upload and response time. This is not CPU time. |
| `temporaryStorageBytes` | Approximate current size of recognized temporary database and journal files, including abandoned files awaiting cleanup. |
| `meter` | Native .NET meter name: `WeatherAggregation`. |

The snapshot excludes its own still-running request. Values may change concurrently, reset on restart, and are not durable audit records. Storage is sampled on demand and may miss files during concurrent cleanup. It excludes runtime memory, lock files, and unrelated files. The JSON endpoint is not a Prometheus exporter.

Native `System.Diagnostics.Metrics` instruments are also available for a .NET diagnostics or OpenTelemetry collector:

| Instrument | Kind | Unit |
|---|---|---|
| `weather.requests` | Counter | requests |
| `weather.requests.rejected` | Counter | requests |
| `weather.request.duration` | Histogram | seconds |
| `weather.aggregations.active` | Observable gauge | requests |
| `weather.measurements.accepted` | Observable counter | measurements |
| `weather.storage.temporary` | Observable gauge | bytes |

Request counters have bounded `route` and `status` tags. The duration histogram has a bounded `route` tag. Unknown paths use `other`, and documentation paths use `/docs`. No station names, keys, request IDs, or raw URL query values become labels. An OpenTelemetry exporter or metrics backend is not installed by this change; attach one to the `WeatherAggregation` meter if needed.

## Verification

Build and run the test suite as described in [README.md](README.md). Added checks cover documentation/specification/asset protection, browser Basic login boundaries, valid OpenAPI references and examples, correlation IDs, metrics snapshots/native instruments, and progress on stderr with unchanged JSONL stdout. The PowerShell launcher's `-SmokeTest` option exercises its build, startup, authenticated health check, example client, and process cleanup.

The earlier performance results describe the pre-observability implementation and are retained as historical evidence. These additions have not been used to make a new billion-row performance claim.

Verified locally on 4 November 2026: Release build with zero warnings/errors, 45 passing unit tests and 21 passing integration tests (66 total), and a successful `Start-Local.ps1 -Port 5089 -SmokeTest` run in Windows PowerShell. Validation used an isolated source copy because an existing service was running from the main checkout. Stop that instance before rebuilding or using the new launcher from the main folder. The Windows GitHub workflow now includes a launcher smoke-test step; hosted execution remains deferred while the project stays local.
