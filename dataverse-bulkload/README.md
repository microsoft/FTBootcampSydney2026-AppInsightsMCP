# DvBulkLoad

High-throughput row generator for Dataverse, built for `ace_sqlantipatterntestentity`
(`ace_name`, `ace_somemoredata`, `ace_someotherdat`, `ace_filterfield`).

## Why it's fast

| Technique | Effect |
|---|---|
| `CreateMultipleRequest` (100–1000 rows/call) | one round trip per batch instead of per row |
| `ServiceClient.Clone()` per worker | parallel HTTP pipelines, single auth handshake |
| `RecommendedDegreesOfParallelism` (`x-ms-dop-hint`) | matches the server's advertised concurrency |
| `EnableAffinityCookie = false` | spreads load across back-end nodes instead of pinning one |
| `BypassCustomPluginExecution` | skips custom plugin/flow execution on write |
| `SuppressDuplicateDetection` | avoids duplicate-detection scans |
| `Retry-After`-aware backoff | rides out service protection limits instead of failing |
| Batch bisection on error | a single bad row loses 1 row, not the whole batch |
| Bounded channel producer/consumer | constant memory regardless of row count |

## Usage

```bash
cd ~/dataverse-bulkload

# preview generated data (connects to read table metadata)
dotnet run -c Release -- --url https://yourorg.crm6.dynamics.com --dry-run

# small smoke test
dotnet run -c Release -- --url https://yourorg.crm6.dynamics.com --count 1000

# the real thing
dotnet run -c Release -- --url https://yourorg.crm6.dynamics.com --count 10000000

# resume after an interrupt (rows are deterministic per offset)
dotnet run -c Release -- --url https://yourorg.crm6.dynamics.com --count 10000000 --start-at 4200000
```

Run `--help` for the full option list.

## When it looks stuck

The loader now sends a **canary batch** before starting workers, so permission,
message-support and auth problems abort immediately with the real error instead of
looking like a stall. If you still see zero rows:

```bash
./dvbulkload --url https://yourorg.crm6.dynamics.com --count 100 --verbose
```

- Progress lines include `in-flight`, the number of batches currently awaiting a
  response. Non-zero in-flight with zero rows written means requests are hanging,
  not failing.
- `--timeout <seconds>` (default 120) forces a hung request to surface as an error
  rather than blocking a worker forever.
- Throttling, retries and batch bisection are logged — the first five always print,
  and `--verbose` prints all of them.
- `--clone` gives each worker its own client. It is **off by default** because
  cloning an interactively-authenticated client can re-trigger token acquisition per
  worker and hang. Use it only with service principal auth.

Note the failure log is written relative to the working directory, so running the
binary from `bin/Release/net10.0` leaves `failures.log` there.

### Auth

- **Interactive** (default): browser sign-in, token cached in temp.
- **Service principal**: `export DV_CLIENT_ID=... DV_CLIENT_SECRET=...` — recommended for
  long runs so nothing expires mid-load. The app user needs create privileges on the table,
  plus `prvBypassCustomPluginExecution` if you keep `--bypass-plugins` on (default).

`DV_URL`, `DV_CLIENT_ID`, `DV_CLIENT_SECRET` are read from the environment.

## Generated data

- `ace_name` — `bulk-<word>-<000000001>`, unique per row
- `ace_somemoredata` / `ace_someotherdat` — random word pairs plus the row ordinal
- `ace_filterfield` — `bulk-bucket-NNNN`, low cardinality (default 1000 distinct values)
  so filter queries have realistic selectivity

Values are truncated to each column's `MaxLength`, and typed automatically from table
metadata, so non-string column types still work if the schema changes.

Data is deterministic for a given `--seed` and row offset, which is what makes
`--start-at` a safe resume.

## Before a 10M run

1. Disable auditing on the table.
2. Disable duplicate detection rules.
3. Deactivate real-time workflows / synchronous plugins (or rely on `--bypass-plugins`).
4. Start with `--count 100000` and watch the rows/sec line, then tune `--batch` and `--dop`.
5. Consider an **elastic table** if you own the schema — much higher sustained write throughput.

Expect roughly 1–5k rows/sec once tuned, so a few hours for 10M.

## Notes

- Ctrl+C cancels cleanly; note the last reported row count and resume with `--start-at`.
- Failed rows land in `failures.log` (timestamp, error chain, row values).
- `dotnet restore` warns about `System.Security.Cryptography.Xml` advisories. It is a
  transitive dependency of `Microsoft.PowerPlatform.Dataverse.Client` with no patched
  version available, so it is left as-is rather than suppressed.
