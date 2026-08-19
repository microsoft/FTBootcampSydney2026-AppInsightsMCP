using System.Collections.Concurrent;
using System.Diagnostics;
using System.ServiceModel;
using System.Threading.Channels;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace DvBulkLoad;

internal static class Program
{
    private static long _written;
    private static long _failed;
    private static long _inFlight;
    private static int _warnings;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static async Task<int> Main(string[] args)
    {
        Options opts;
        try
        {
            opts = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}\n");
            Options.PrintUsage();
            return 2;
        }

        if (opts.ShowHelp)
        {
            Options.PrintUsage();
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\ncancellation requested, draining in-flight batches...");
            cts.Cancel();
        };

        Console.WriteLine($"connecting to {opts.Url} ...");
        ServiceClient root;
        try
        {
            root = new ServiceClient(opts.BuildConnectionString());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"connection failed: {Flatten(ex)}");
            return 1;
        }

        using (root)
        {
            if (!root.IsReady)
            {
                Console.Error.WriteLine($"connection failed: {root.LastError}");
                return 1;
            }
            return await RunAsync(root, opts, cts);
        }
    }

    private static async Task<int> RunAsync(ServiceClient root, Options opts, CancellationTokenSource cts)
    {
        // Affinity cookies pin every call to one back-end node, which caps bulk
        // throughput. Turning it off lets the platform spread load across nodes.
        root.EnableAffinityCookie = false;
        root.MaxRetryCount = 3;
        root.RetryPauseTime = TimeSpan.FromSeconds(3);

        Console.WriteLine($"connected to org {root.ConnectedOrgFriendlyName}");

        var meta = await LoadTableMetadataAsync(root, opts.Table, cts.Token);
        if (meta is null) return 1;

        var dop = opts.Dop ?? Math.Max(1, root.RecommendedDegreesOfParallelism);
        Console.WriteLine($"table         : {meta.LogicalName} ({meta.TableType})");
        Console.WriteLine($"columns       : {string.Join(", ", meta.Columns.Select(c => $"{c.LogicalName}:{c.Kind}"))}");
        Console.WriteLine($"rows          : {opts.Count:N0} (starting at offset {opts.StartAt:N0})");
        Console.WriteLine($"batch size    : {opts.BatchSize}");
        Console.WriteLine($"parallelism   : {dop} (server hint {root.RecommendedDegreesOfParallelism})");
        Console.WriteLine($"bypass plugins: {opts.BypassPlugins}");
        Console.WriteLine();

        if (opts.DryRun)
        {
            var sample = BuildBatch(meta, opts, opts.StartAt, Math.Min(3, opts.BatchSize));
            Console.WriteLine("dry run - sample rows:");
            foreach (var e in sample.Entities)
                Console.WriteLine("  " + string.Join(" | ", e.Attributes.Select(a => $"{a.Key}={a.Value}")));
            return 0;
        }

        var failures = new BlockingCollection<string>();
        var failureWriter = Task.Run(() =>
        {
            using var sw = new StreamWriter(opts.FailureLog, append: true);
            foreach (var line in failures.GetConsumingEnumerable())
            {
                sw.WriteLine(line);
                sw.Flush();
            }
        });

        // Canary: prove one small batch round-trips before spinning up workers.
        // Privilege, message-support and auth problems surface here in seconds
        // instead of looking like a silent stall.
        OptionalParameters["BypassCustomPluginExecution"] = opts.BypassPlugins;
        OptionalParameters["SuppressCallbackRegistrationExpanderJob"] = opts.BypassPlugins;

        Console.WriteLine("sending canary batch...");
        var canarySize = Math.Min(2, opts.BatchSize);
        var canary = BuildBatch(meta, opts, opts.StartAt, canarySize);
        var canaryClock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await ExecuteBatchAsync(root, canary, opts, cts.Token);
                Interlocked.Add(ref _written, canarySize);
                var active = OptionalParameters.Where(p => p.Value).Select(p => p.Key).ToArray();
                Console.WriteLine($"canary ok ({canaryClock.ElapsedMilliseconds:N0} ms for {canarySize} rows), options: {(active.Length > 0 ? string.Join(", ", active) : "none")}\n");
                break;
            }
            catch (Exception ex)
            {
                var unsupported = UnsupportedParameter(ex);
                if (unsupported is not null && OptionalParameters.TryUpdate(unsupported, false, true))
                {
                    Console.WriteLine($"  note: '{unsupported}' not supported here, disabling it and retrying");
                    continue;
                }

                failures.CompleteAdding();
                await failureWriter;
                Console.Error.WriteLine($"canary failed after {canaryClock.ElapsedMilliseconds:N0} ms: {Flatten(ex)}");
                Console.Error.WriteLine("aborting - fix the above before running the full load.");
                return 1;
            }
        }

        var channel = Channel.CreateBounded<(long Offset, int Size)>(
            new BoundedChannelOptions(dop * 4) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

        var producer = Task.Run(async () =>
        {
            var end = opts.StartAt + opts.Count;
            for (var offset = opts.StartAt + canarySize; offset < end && !cts.IsCancellationRequested; offset += opts.BatchSize)
            {
                var size = (int)Math.Min(opts.BatchSize, end - offset);
                await channel.Writer.WriteAsync((offset, size), cts.Token);
            }
            channel.Writer.Complete();
        }, cts.Token);

        var reporter = Task.Run(() => ReportProgressAsync(opts.Count, cts.Token), cts.Token);

        var workers = Enumerable.Range(0, dop).Select(id => Task.Run(async () =>
        {
            // ServiceClient is thread-safe and pools connections internally, so a
            // shared instance is the safe default. Cloning can re-trigger an
            // interactive token acquisition per worker and hang, so it is opt-in.
            var client = opts.CloneClients ? root.Clone() : root;
            if (opts.CloneClients) client.EnableAffinityCookie = false;

            try
            {
                await foreach (var work in channel.Reader.ReadAllAsync(cts.Token))
                {
                    var batch = BuildBatch(meta, opts, work.Offset, work.Size);
                    await SendWithRetryAsync(client, batch, opts, failures, id, cts.Token);
                }
            }
            finally
            {
                if (opts.CloneClients) client.Dispose();
            }
        }, cts.Token)).ToArray();

        try
        {
            await producer;
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) { /* expected on Ctrl+C */ }
        finally
        {
            failures.CompleteAdding();
            await failureWriter;
        }

        cts.Cancel();
        try { await reporter; } catch (OperationCanceledException) { }

        var written = Interlocked.Read(ref _written);
        var failed = Interlocked.Read(ref _failed);
        var secs = Math.Max(Clock.Elapsed.TotalSeconds, 1);
        Console.WriteLine();
        Console.WriteLine($"done. created {written:N0} rows, {failed:N0} failed, in {Clock.Elapsed:hh\\:mm\\:ss} ({written / secs:N0} rows/sec)");
        if (failed > 0) Console.WriteLine($"failures logged to {opts.FailureLog}");
        return failed > 0 ? 1 : 0;
    }

    // Optional parameters vary by table type and platform version. Any that the
    // server rejects get disabled at runtime rather than failing the whole load.
    private static readonly ConcurrentDictionary<string, bool> OptionalParameters = new();

    private static async Task ExecuteBatchAsync(ServiceClient client, EntityCollection batch, Options opts, CancellationToken ct)
    {
        var request = new CreateMultipleRequest { Targets = batch };
        foreach (var (name, enabled) in OptionalParameters)
            if (enabled) request[name] = true;

        // A hung request must surface as an error, not an indefinite stall.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(opts.RequestTimeout);
        try
        {
            await client.ExecuteAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"request exceeded --timeout of {opts.RequestTimeout.TotalSeconds:N0}s");
        }
    }

    private static string? UnsupportedParameter(Exception ex)
    {
        const string marker = "Unrecognized request parameter: ";
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            var idx = e.Message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var name = e.Message[(idx + marker.Length)..].Trim();
            var end = name.IndexOfAny(new[] { ' ', '.', ',', ';', '\r', '\n' });
            if (end > 0) name = name[..end];
            if (name.Length > 0 && OptionalParameters.ContainsKey(name)) return name;
        }
        return null;
    }

    private static async Task SendWithRetryAsync(
        ServiceClient client, EntityCollection batch, Options opts,
        BlockingCollection<string> failures, int workerId, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Interlocked.Increment(ref _inFlight);
                try
                {
                    await ExecuteBatchAsync(client, batch, opts, ct);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }

                Interlocked.Add(ref _written, batch.Entities.Count);
                if (opts.Verbose)
                    Console.WriteLine($"  [w{workerId}] ok {batch.Entities.Count} rows");
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Drop a parameter this environment rejects and retry immediately;
                // it costs one round trip once, not the run.
                var unsupported = UnsupportedParameter(ex);
                if (unsupported is not null && OptionalParameters.TryUpdate(unsupported, false, true))
                {
                    Console.WriteLine($"  note: '{unsupported}' not supported here, disabling it and retrying");
                    continue;
                }

                var retryAfter = GetRetryAfter(ex);
                if (retryAfter is not null && attempt < opts.MaxRetries)
                {
                    attempt++;
                    Warn(opts, $"  [w{workerId}] throttled, waiting {retryAfter.Value.TotalSeconds:N0}s (attempt {attempt}/{opts.MaxRetries})");
                    await Task.Delay(retryAfter.Value, ct);
                    continue;
                }

                if (IsTransient(ex) && attempt < opts.MaxRetries)
                {
                    attempt++;
                    var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    Warn(opts, $"  [w{workerId}] transient error, retry in {backoff.TotalSeconds:N0}s (attempt {attempt}/{opts.MaxRetries}): {ex.Message}");
                    await Task.Delay(backoff, ct);
                    continue;
                }

                // CreateMultiple is all-or-nothing, so one bad row kills the whole
                // batch. Bisect to salvage the rows that are actually valid.
                if (batch.Entities.Count > 1)
                {
                    Warn(opts, $"  [w{workerId}] batch of {batch.Entities.Count} failed, bisecting: {ex.Message}");
                    var half = batch.Entities.Count / 2;
                    var left = new EntityCollection(batch.Entities.Take(half).ToList()) { EntityName = batch.EntityName };
                    var right = new EntityCollection(batch.Entities.Skip(half).ToList()) { EntityName = batch.EntityName };
                    await SendWithRetryAsync(client, left, opts, failures, workerId, ct);
                    await SendWithRetryAsync(client, right, opts, failures, workerId, ct);
                    return;
                }

                Interlocked.Add(ref _failed, batch.Entities.Count);
                var row = batch.Entities[0];
                failures.Add($"{DateTime.UtcNow:o}\t{Flatten(ex)}\t{string.Join(",", row.Attributes.Select(a => $"{a.Key}={a.Value}"))}");
                return;
            }
        }
    }

    // First few problems always reach the console; the rest only in verbose mode,
    // so a systemic failure can never look like a silent stall.
    private static void Warn(Options opts, string message)
    {
        if (opts.Verbose || Interlocked.Increment(ref _warnings) <= 5)
            Console.WriteLine(message);
    }

    private static TimeSpan? GetRetryAfter(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is not FaultException<OrganizationServiceFault> fault) continue;

            var details = fault.Detail?.ErrorDetails;
            if (details is not null && details.TryGetValue("Retry-After", out var value))
            {
                return value switch
                {
                    TimeSpan ts => ts,
                    int i => TimeSpan.FromSeconds(i),
                    string s when double.TryParse(s, out var secs) => TimeSpan.FromSeconds(secs),
                    string s when TimeSpan.TryParse(s, out var ts2) => ts2,
                    _ => TimeSpan.FromSeconds(30)
                };
            }

            // Service-protection limit codes: burst / concurrency / number-of-requests.
            if (fault.Detail?.ErrorCode is -2147015902 or -2147015903 or -2147015898 or -2147015905)
                return TimeSpan.FromSeconds(30);
        }
        return null;
    }

    private static bool IsTransient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is TimeoutException or HttpRequestException or CommunicationException) return true;
            if (e is FaultException<OrganizationServiceFault> f &&
                (f.Detail?.ErrorCode == -2147204784 ||
                 f.Message.Contains("Generic SQL error", StringComparison.OrdinalIgnoreCase) ||
                 f.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException!)
            parts.Add(e.Message.Replace('\n', ' ').Replace('\t', ' '));
        return string.Join(" <- ", parts);
    }

    private static EntityCollection BuildBatch(TableMeta meta, Options opts, long offset, int size)
    {
        var rows = new List<Entity>(size);
        for (var i = 0; i < size; i++)
        {
            var n = offset + i;
            // Deterministic per-row seed keeps --start-at resumes reproducible.
            var rnd = new Random(unchecked((int)(n * 2654435761L ^ opts.Seed)));
            var e = new Entity(meta.LogicalName);
            foreach (var col in meta.Columns)
                e[col.LogicalName] = GenerateValue(col, n, rnd, opts);
            rows.Add(e);
        }
        return new EntityCollection(rows) { EntityName = meta.LogicalName };
    }

    private static readonly string[] Words =
    {
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india",
        "juliet", "kilo", "lima", "mike", "november", "oscar", "papa", "quebec", "romeo"
    };

    private static object GenerateValue(ColumnMeta col, long n, Random rnd, Options opts)
    {
        switch (col.Kind)
        {
            case AttributeTypeCode.String:
            case AttributeTypeCode.Memo:
            {
                string text;
                if (col.LogicalName.Contains("filter", StringComparison.OrdinalIgnoreCase))
                    // Low cardinality on purpose so filter queries have realistic selectivity.
                    text = $"{opts.Prefix}-bucket-{n % opts.FilterCardinality:D4}";
                else if (col.LogicalName.Contains("name", StringComparison.OrdinalIgnoreCase))
                    text = $"{opts.Prefix}-{Words[rnd.Next(Words.Length)]}-{n:D9}";
                else
                    text = $"{Words[rnd.Next(Words.Length)]}-{Words[rnd.Next(Words.Length)]}-{rnd.Next(1_000_000):D6}-{n:D9}";
                return col.MaxLength > 0 && text.Length > col.MaxLength ? text[..col.MaxLength] : text;
            }
            case AttributeTypeCode.Integer:
            case AttributeTypeCode.BigInt:
                return (int)(n % int.MaxValue);
            case AttributeTypeCode.Decimal:
                return Math.Round((decimal)rnd.NextDouble() * 1000m, 2);
            case AttributeTypeCode.Double:
                return Math.Round(rnd.NextDouble() * 1000, 2);
            case AttributeTypeCode.Money:
                return new Money(Math.Round((decimal)rnd.NextDouble() * 1000m, 2));
            case AttributeTypeCode.Boolean:
                return n % 2 == 0;
            case AttributeTypeCode.DateTime:
                return DateTime.UtcNow.AddMinutes(-(n % 500_000));
            case AttributeTypeCode.Picklist:
            case AttributeTypeCode.State:
            case AttributeTypeCode.Status:
                return new OptionSetValue(col.OptionValues.Length > 0
                    ? col.OptionValues[(int)(n % col.OptionValues.Length)]
                    : 1);
            case AttributeTypeCode.Uniqueidentifier:
                return Guid.NewGuid();
            default:
                return $"{opts.Prefix}-{n:D9}";
        }
    }

    private static async Task<TableMeta?> LoadTableMetadataAsync(ServiceClient client, string table, CancellationToken ct)
    {
        EntityMetadata em;
        try
        {
            var resp = (RetrieveEntityResponse)await client.ExecuteAsync(new RetrieveEntityRequest
            {
                LogicalName = table.ToLowerInvariant(),
                EntityFilters = EntityFilters.Attributes,
                RetrieveAsIfPublished = true
            }, ct);
            em = resp.EntityMetadata;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not read metadata for '{table}': {Flatten(ex)}");
            return null;
        }

        var writable = em.Attributes
            .Where(a => a.IsValidForCreate == true && a.IsPrimaryId != true && a.AttributeOf is null)
            .GroupBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var wanted = new[] { "ace_name", "ace_somemoredata", "ace_someotherdata", "ace_filterfield" };
        var cols = new List<ColumnMeta>();
        foreach (var name in wanted)
        {
            if (!writable.TryGetValue(name, out var a))
            {
                Console.Error.WriteLine($"column '{name}' not found or not writable on {em.LogicalName}");
                return null;
            }
            cols.Add(new ColumnMeta(
                a.LogicalName!,
                a.AttributeType ?? AttributeTypeCode.String,
                (a as StringAttributeMetadata)?.MaxLength ?? (a as MemoAttributeMetadata)?.MaxLength ?? 0,
                (a as EnumAttributeMetadata)?.OptionSet?.Options.Select(o => o.Value ?? 1).ToArray() ?? Array.Empty<int>()));
        }

        return new TableMeta(em.LogicalName!, string.IsNullOrEmpty(em.TableType) ? "Standard" : em.TableType, cols);
    }

    private static async Task ReportProgressAsync(long total, CancellationToken ct)
    {
        long last = 0;
        var lastAt = Clock.Elapsed;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var now = Interlocked.Read(ref _written);
            var elapsed = Clock.Elapsed;
            var instant = (now - last) / Math.Max((elapsed - lastAt).TotalSeconds, 0.001);
            var overall = now / Math.Max(elapsed.TotalSeconds, 0.001);
            var eta = overall > 0 ? TimeSpan.FromSeconds((total - now) / overall) : TimeSpan.Zero;
            Console.WriteLine($"[{elapsed:hh\\:mm\\:ss}] {now:N0}/{total:N0} ({(double)now / total:P1})  {instant:N0} rows/s now, {overall:N0} avg, eta {eta:hh\\:mm\\:ss}, in-flight {Interlocked.Read(ref _inFlight)}, failed {Interlocked.Read(ref _failed):N0}");
            last = now;
            lastAt = elapsed;
        }
    }
}

internal sealed record ColumnMeta(string LogicalName, AttributeTypeCode Kind, int MaxLength, int[] OptionValues);

internal sealed record TableMeta(string LogicalName, string TableType, List<ColumnMeta> Columns);

internal sealed class Options
{
    public string Url { get; private set; } = "";
    public string Table { get; private set; } = "ace_sqlantipatterntestentity";
    public long Count { get; private set; } = 10_000_000;
    public long StartAt { get; private set; }
    public int BatchSize { get; private set; } = 500;
    public int? Dop { get; private set; }
    public string Prefix { get; private set; } = "bulk";
    public int FilterCardinality { get; private set; } = 1000;
    public int Seed { get; private set; } = 1337;
    public int MaxRetries { get; private set; } = 6;
    public bool BypassPlugins { get; private set; } = true;
    public bool CloneClients { get; private set; }
    public bool Verbose { get; private set; }
    public TimeSpan RequestTimeout { get; private set; } = TimeSpan.FromSeconds(120);
    public bool DryRun { get; private set; }
    public bool ShowHelp { get; private set; }
    public string FailureLog { get; private set; } = "failures.log";
    public string? ClientId { get; private set; }
    public string? ClientSecret { get; private set; }

    public string BuildConnectionString()
    {
        if (!string.IsNullOrEmpty(ClientSecret))
            return $"AuthType=ClientSecret;Url={Url};ClientId={ClientId};ClientSecret={ClientSecret};RequireNewInstance=true";

        var appId = ClientId ?? "51f81489-12ee-4a9e-aaae-a2591f45987d";
        var cache = Path.Combine(Path.GetTempPath(), "dvbulkload.tokencache");
        return $"AuthType=OAuth;Url={Url};AppId={appId};RedirectUri=http://localhost;LoginPrompt=Auto;TokenCacheStorePath={cache};RequireNewInstance=true";
    }

    public static Options Parse(string[] args)
    {
        var o = new Options
        {
            ClientId = Environment.GetEnvironmentVariable("DV_CLIENT_ID"),
            ClientSecret = Environment.GetEnvironmentVariable("DV_CLIENT_SECRET"),
            Url = Environment.GetEnvironmentVariable("DV_URL") ?? ""
        };

        for (var i = 0; i < args.Length; i++)
        {
            var flag = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{flag} requires a value");
                return args[++i];
            }

            switch (flag)
            {
                case "--url": o.Url = Next(); break;
                case "--table": o.Table = Next(); break;
                case "--count": o.Count = ParseLong(Next()); break;
                case "--start-at": o.StartAt = ParseLong(Next()); break;
                case "--batch": o.BatchSize = int.Parse(Next()); break;
                case "--dop": o.Dop = int.Parse(Next()); break;
                case "--prefix": o.Prefix = Next(); break;
                case "--filter-cardinality": o.FilterCardinality = int.Parse(Next()); break;
                case "--seed": o.Seed = int.Parse(Next()); break;
                case "--max-retries": o.MaxRetries = int.Parse(Next()); break;
                case "--client-id": o.ClientId = Next(); break;
                case "--client-secret": o.ClientSecret = Next(); break;
                case "--failure-log": o.FailureLog = Next(); break;
                case "--no-bypass-plugins": o.BypassPlugins = false; break;
                case "--clone": o.CloneClients = true; break;
                case "--verbose": o.Verbose = true; break;
                case "--timeout": o.RequestTimeout = TimeSpan.FromSeconds(double.Parse(Next())); break;
                case "--dry-run": o.DryRun = true; break;
                case "-h":
                case "--help": o.ShowHelp = true; return o;
                default: throw new ArgumentException($"unknown argument '{flag}'");
            }
        }

        if (o.ShowHelp) return o;
        if (string.IsNullOrWhiteSpace(o.Url)) throw new ArgumentException("--url (or DV_URL) is required");
        if (o.BatchSize is < 1 or > 1000) throw new ArgumentException("--batch must be between 1 and 1000");
        if (o.Count < 1) throw new ArgumentException("--count must be >= 1");
        if (o.FilterCardinality < 1) throw new ArgumentException("--filter-cardinality must be >= 1");
        if (!string.IsNullOrEmpty(o.ClientSecret) && string.IsNullOrEmpty(o.ClientId))
            throw new ArgumentException("--client-secret requires --client-id (or DV_CLIENT_ID)");

        return o;
    }

    private static long ParseLong(string s) => long.Parse(s.Replace("_", "").Replace(",", ""));

    public static void PrintUsage() => Console.WriteLine("""
        DvBulkLoad - high-throughput Dataverse row generator (CreateMultiple + parallel clients)

        usage:
          dotnet run -c Release -- --url https://org.crm6.dynamics.com [options]

        options:
          --url <url>                 Dataverse environment URL (env: DV_URL)
          --table <name>              target table (default ace_sqlantipatterntestentity)
          --count <n>                 rows to create (default 10000000)
          --start-at <n>              row offset to resume from (default 0)
          --batch <n>                 rows per CreateMultiple call, 1-1000 (default 500)
          --dop <n>                   parallel clients (default: server x-ms-dop-hint)
          --prefix <s>                prefix for generated text (default "bulk")
          --filter-cardinality <n>    distinct ace_filterfield values (default 1000)
          --seed <n>                  RNG seed for reproducible data (default 1337)
          --max-retries <n>           retries per batch (default 6)
          --timeout <seconds>         per-request timeout (default 120)
          --clone                     one cloned client per worker (default: shared client)
          --verbose                   log every batch, retry and throttle event
          --no-bypass-plugins         run plugins/flows instead of bypassing them
          --client-id <guid>          app registration (env: DV_CLIENT_ID)
          --client-secret <secret>    client secret auth (env: DV_CLIENT_SECRET)
          --failure-log <path>        failed-row log (default failures.log)
          --dry-run                   print sample rows and exit
          -h, --help                  show this help

        examples:
          dotnet run -c Release -- --url https://org.crm6.dynamics.com --count 1000 --dry-run
          dotnet run -c Release -- --url https://org.crm6.dynamics.com --count 100000
          dotnet run -c Release -- --url https://org.crm6.dynamics.com --count 10000000 --start-at 4200000
        """);
}
