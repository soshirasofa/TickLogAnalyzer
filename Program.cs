using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using TickLog;

var app = ConsoleApp.Create();
app.Add<TickLogAnalyzerCommands>();
await app.RunAsync(args);

public sealed class TickLogAnalyzerCommands
{
    private static readonly TimeZoneInfo JapanTimeZone = ResolveTimeZone("Asia/Tokyo");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Print and optionally write a JSON summary for a .tlog file.
    /// </summary>
    /// <param name="tlog">Input .tlog path.</param>
    /// <param name="jsonOut">JSON summary output path. Default: current directory with the input .tlog basename.</param>
    /// <param name="cadenceMs">Comma-separated polling cadence milliseconds, such as 1,10,33,100.</param>
    /// <param name="largeGapMs">Minimum timestamp gap to list as large gap. Default is 1000ms.</param>
    public int Summary(
        string tlog,
        string? jsonOut = null,
        string? cadenceMs = null,
        [Range(1, long.MaxValue)] long largeGapMs = 1000)
    {
        var data = LoadTickLog(tlog, requireRecords: true);
        var cadences = ParseCadences(cadenceMs);
        var summary = BuildSummary(data, cadences, largeGapMs);
        var jsonPath = ResolveAnalysisOutputPath(jsonOut, data.Path, ".summary", ".json");

        PrintSummary(summary);

        var parent = Path.GetDirectoryName(jsonPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(summary, JsonOptions), new UTF8Encoding(false));
        Console.WriteLine($"JSON summary: {jsonPath}");

        return 0;
    }

    /// <summary>
    /// Aggregate tick counts and price statistics by fixed UTC buckets.
    /// </summary>
    /// <param name="tlog">Input .tlog path.</param>
    /// <param name="bucket">Bucket width: 1s, 10s, 1m, 5m, 15m, or 1h.</param>
    /// <param name="csvOut">CSV output path. Default: current directory with the input .tlog basename and bucket.</param>
    public int Histogram(string tlog, string bucket = "1m", string? csvOut = null)
    {
        var data = LoadTickLog(tlog, requireRecords: true);
        var bucketWidth = ParseBucket(bucket, [1_000, 10_000, 60_000, 300_000, 900_000, 3_600_000]);
        var rows = BuildHistogram(data.Records, bucketWidth);
        var csvPath = ResolveAnalysisOutputPath(csvOut, data.Path, $".histogram.{SanitizeFileNamePart(bucket)}", ".csv");

        Console.WriteLine($"File       : {data.Path}");
        Console.WriteLine($"Bucket     : {bucket}");
        Console.WriteLine($"Rows       : {rows.Count}");
        Console.WriteLine($"Max ticks  : {(rows.Count == 0 ? 0 : rows.Max(x => x.TickCount))}");
        WriteHistogramCsv(csvPath, rows);
        Console.WriteLine($"CSV        : {csvPath}");

        if (csvOut is null)
        {
            Console.WriteLine("Preview    :");
            foreach (var row in rows.Take(20))
            {
                Console.WriteLine($"  {row.BucketStartUtc:O},{row.BucketEndUtc:O},{row.TickCount},{FormatNumber(row.TicksPerSecond)}");
            }

            if (rows.Count > 20)
                Console.WriteLine($"  ... {rows.Count - 20} more rows.");
        }

        return 0;
    }

    /// <summary>
    /// Split a .tlog file at date boundaries in the specified timezone.
    /// </summary>
    /// <param name="tlog">Input .tlog path.</param>
    /// <param name="out">Output root directory.</param>
    /// <param name="timezone">Timezone for date boundaries. Default is UTC.</param>
    /// <param name="overwrite">Overwrite existing output files.</param>
    /// <param name="dryRun">Only print planned files and tick counts.</param>
    [Command("split-day")]
    public int SplitDay(
        string tlog,
        string @out,
        string timezone = "UTC",
        bool overwrite = false,
        bool dryRun = false)
    {
        var data = LoadTickLog(tlog, requireRecords: true);
        var timeZone = ResolveTimeZone(timezone);
        var outputRoot = Path.GetFullPath(@out);
        var groups = data.Records
            .GroupBy(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.TimestampUtc, timeZone).Date))
            .OrderBy(x => x.Key)
            .Select(x => new TickGroup(
                Path.Combine(
                    outputRoot,
                    SanitizeFileNamePart(data.Header.Symbol),
                    $"{x.Key:yyyyMMdd}_{SanitizeFileNamePart(data.Header.Symbol)}_{SanitizeFileNamePart(data.Header.Broker)}.tlog"),
                x.ToArray()))
            .ToArray();

        WriteGroups(data, groups, outputRoot, overwrite, dryRun, copySidecar: true);
        return 0;
    }

    /// <summary>
    /// Slice a .tlog file by from <= tick < to.
    /// </summary>
    /// <param name="tlog">Input .tlog path.</param>
    /// <param name="from">Inclusive start time. Offset-aware values are used as-is; offset-less values are interpreted in --timezone.</param>
    /// <param name="out">Output directory.</param>
    /// <param name="to">Exclusive end time.</param>
    /// <param name="hours">Duration in hours. Specify either --to or --hours.</param>
    /// <param name="timezone">Timezone for offset-less --from/--to values. Default is UTC.</param>
    /// <param name="overwrite">Overwrite existing output file.</param>
    /// <param name="dryRun">Only print planned file and tick count.</param>
    public int Slice(
        string tlog,
        string from,
        string @out,
        string? to = null,
        double? hours = null,
        string timezone = "UTC",
        bool overwrite = false,
        bool dryRun = false)
    {
        if ((to is null && hours is null) || (to is not null && hours is not null))
            throw new ArgumentException("Specify exactly one of --to or --hours.");

        if (hours is <= 0)
            throw new ArgumentException("--hours must be greater than zero.");

        var data = LoadTickLog(tlog, requireRecords: true);
        var timeZone = ResolveTimeZone(timezone);
        var fromUtc = ParseInstant(from, timeZone);
        var toUtc = to is not null
            ? ParseInstant(to, timeZone)
            : fromUtc.Add(TimeSpan.FromHours(hours!.Value));

        if (toUtc <= fromUtc)
            throw new ArgumentException("--to must be greater than --from.");

        var records = data.Records
            .Where(x => x.TimestampUtc >= fromUtc && x.TimestampUtc < toUtc)
            .ToArray();

        if (records.Length == 0)
        {
            Console.Error.WriteLine("No ticks matched the specified range. No file was written.");
            return 2;
        }

        var outputRoot = Path.GetFullPath(@out);
        var fileName = $"{fromUtc:yyyyMMdd_HHmmss}_{toUtc:yyyyMMdd_HHmmss}_{SanitizeFileNamePart(data.Header.Symbol)}_{SanitizeFileNamePart(data.Header.Broker)}.tlog";
        WriteGroups(data, [new TickGroup(Path.Combine(outputRoot, fileName), records)], outputRoot, overwrite, dryRun, copySidecar: true);
        return 0;
    }

    /// <summary>
    /// Split a .tlog file into fixed-width windows.
    /// </summary>
    /// <param name="tlog">Input .tlog path.</param>
    /// <param name="window">Window width: 1h, 2h, 3h, 4h, 6h, or 12h.</param>
    /// <param name="out">Output root directory.</param>
    /// <param name="align">Use "first" or "day". Day alignment uses --timezone day boundaries.</param>
    /// <param name="timezone">Timezone for --align day. Default is UTC.</param>
    /// <param name="overwrite">Overwrite existing output files.</param>
    /// <param name="dryRun">Only print planned files and tick counts.</param>
    [Command("split-window")]
    public int SplitWindow(
        string tlog,
        string window,
        string @out,
        string align = "first",
        string timezone = "UTC",
        bool overwrite = false,
        bool dryRun = false)
    {
        var data = LoadTickLog(tlog, requireRecords: true);
        var windowMs = ParseBucket(window, [3_600_000, 7_200_000, 10_800_000, 14_400_000, 21_600_000, 43_200_000]);
        var originMs = ResolveWindowOriginMs(data.Records[0].TimestampUtc, align, ResolveTimeZone(timezone));
        var outputRoot = Path.GetFullPath(@out);

        var groups = data.Records
            .GroupBy(x => originMs + ((x.TimestampMs - originMs) / windowMs) * windowMs)
            .OrderBy(x => x.Key)
            .Select(x =>
            {
                var start = DateTimeOffset.FromUnixTimeMilliseconds(x.Key);
                var end = start.AddMilliseconds(windowMs);
                var fileName = $"{start:yyyyMMdd_HHmmss}_{end:yyyyMMdd_HHmmss}_{SanitizeFileNamePart(data.Header.Symbol)}_{SanitizeFileNamePart(data.Header.Broker)}.tlog";
                return new TickGroup(Path.Combine(outputRoot, SanitizeFileNamePart(data.Header.Symbol), fileName), x.ToArray());
            })
            .ToArray();

        WriteGroups(data, groups, outputRoot, overwrite, dryRun, copySidecar: true);
        return 0;
    }

    private static TickLogData LoadTickLog(string path, bool requireRecords)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Tick log file was not found.", fullPath);

        using var reader = TickFileReader.Open(fullPath);
        var header = new TickLogHeader(
            reader.Header.SourceKind,
            reader.Header.Broker,
            reader.Header.Symbol,
            reader.Header.Digits,
            reader.Header.TickSize,
            reader.Header.PriceScale,
            reader.Header.SessionStartMs);

        var records = reader.ReadRecords()
            .Select(x => new TickData(
                x.TimestampMs,
                DateTimeOffset.FromUnixTimeMilliseconds(x.TimestampMs),
                x.GetBidPrice(header.PriceScale),
                x.GetAskPrice(header.PriceScale)))
            .ToArray();

        if (requireRecords && records.Length == 0)
            throw new InvalidDataException($"Tick log '{fullPath}' has no records.");

        return new TickLogData(fullPath, header, records);
    }

    private static SummaryDocument BuildSummary(TickLogData data, IReadOnlyList<int> cadences, long largeGapMs)
    {
        var records = data.Records;
        var first = records[0];
        var last = records[^1];
        var duration = last.TimestampUtc - first.TimestampUtc;
        var durationSeconds = Math.Max(duration.TotalSeconds, 0d);
        var spreads = records.Select(x => x.Spread).Order().ToArray();
        var gaps = BuildTimestampGaps(records);
        var largeGaps = gaps
            .Where(x => x.GapMs >= largeGapMs)
            .OrderByDescending(x => x.GapMs)
            .Take(20)
            .ToArray();
        var oneSecondBuckets = BuildDensityBuckets(records, 1_000);
        var ticksPerSecondBuckets = oneSecondBuckets.Select(x => (double)x.TickCount).Order().ToArray();

        var duplicateTimestampCount = records
            .GroupBy(x => x.TimestampMs)
            .Where(x => x.Count() > 1)
            .Sum(x => x.Count() - 1);
        var nonMonotonicTimestampCount = records.Zip(records.Skip(1), (a, b) => b.TimestampMs < a.TimestampMs).Count(x => x);
        var zeroOrNegativeSpreadCount = records.Count(x => x.Spread <= 0);

        return new SummaryDocument(
            FilePath: data.Path,
            Symbol: data.Header.Symbol,
            Broker: data.Header.Broker,
            SourceKind: data.Header.SourceKind.ToString(),
            Digits: data.Header.Digits,
            TickSize: data.Header.TickSize,
            PriceScale: data.Header.PriceScale,
            RecordCount: records.Length,
            FirstTickUtc: first.TimestampUtc,
            FirstTickLocal: first.TimestampUtc.ToLocalTime(),
            LastTickUtc: last.TimestampUtc,
            LastTickLocal: last.TimestampUtc.ToLocalTime(),
            Duration: duration,
            AverageTicksPerSecond: durationSeconds == 0 ? records.Length : records.Length / durationSeconds,
            AverageTicksPerMinute: durationSeconds == 0 ? records.Length : records.Length / (durationSeconds / 60d),
            AverageTicksPerHour: durationSeconds == 0 ? records.Length : records.Length / (durationSeconds / 3600d),
            BidMin: records.Min(x => x.Bid),
            BidMax: records.Max(x => x.Bid),
            AskMin: records.Min(x => x.Ask),
            AskMax: records.Max(x => x.Ask),
            SpreadMin: spreads[0],
            SpreadMax: spreads[^1],
            SpreadAverage: spreads.Average(),
            SpreadP50: PercentileSorted(spreads, 0.50),
            SpreadP90: PercentileSorted(spreads, 0.90),
            SpreadP95: PercentileSorted(spreads, 0.95),
            SpreadP99: PercentileSorted(spreads, 0.99),
            IsStrictlyMonotonicTimestamp: duplicateTimestampCount == 0 && nonMonotonicTimestampCount == 0,
            DuplicateTimestampCount: duplicateTimestampCount,
            NonMonotonicTimestampCount: nonMonotonicTimestampCount,
            ZeroOrNegativeSpreadCount: zeroOrNegativeSpreadCount,
            MaxTimestampGapMs: gaps.Count == 0 ? 0 : gaps.Max(x => x.GapMs),
            LargeGaps: largeGaps,
            HighDensityWindows: oneSecondBuckets.OrderByDescending(x => x.TickCount).ThenBy(x => x.BucketStartUtc).Take(20).ToArray(),
            HourlyDensityJst: BuildHourlyDensity(records, JapanTimeZone),
            TickDensity: new TickDensitySummary(
                MaxTicksPerSecondBucket: ticksPerSecondBuckets.Length == 0 ? records.Length : ticksPerSecondBuckets[^1],
                P50TicksPerSecondBucket: PercentileSorted(ticksPerSecondBuckets, 0.50),
                P90TicksPerSecondBucket: PercentileSorted(ticksPerSecondBuckets, 0.90),
                P95TicksPerSecondBucket: PercentileSorted(ticksPerSecondBuckets, 0.95),
                P99TicksPerSecondBucket: PercentileSorted(ticksPerSecondBuckets, 0.99)),
            CadenceEstimates: BuildCadenceEstimates(records, cadences));
    }

    private static void PrintSummary(SummaryDocument summary)
    {
        Console.WriteLine("Tick log summary");
        Console.WriteLine($"File       : {summary.FilePath}");
        Console.WriteLine($"Symbol     : {summary.Symbol}");
        Console.WriteLine($"Broker     : {summary.Broker}");
        Console.WriteLine($"Source     : {summary.SourceKind}");
        Console.WriteLine($"Digits     : {summary.Digits}");
        Console.WriteLine($"Tick size  : {FormatNumber(summary.TickSize)}");
        Console.WriteLine($"Price scale: {summary.PriceScale}");
        Console.WriteLine($"Records    : {summary.RecordCount}");
        Console.WriteLine($"First UTC  : {summary.FirstTickUtc:O}");
        Console.WriteLine($"First local: {summary.FirstTickLocal:O}");
        Console.WriteLine($"Last UTC   : {summary.LastTickUtc:O}");
        Console.WriteLine($"Last local : {summary.LastTickLocal:O}");
        Console.WriteLine($"Duration   : {summary.Duration}");
        Console.WriteLine($"Avg tick/s : {FormatNumber(summary.AverageTicksPerSecond)}");
        Console.WriteLine($"Avg tick/m : {FormatNumber(summary.AverageTicksPerMinute)}");
        Console.WriteLine($"Avg tick/h : {FormatNumber(summary.AverageTicksPerHour)}");
        Console.WriteLine($"Bid min/max: {FormatNumber(summary.BidMin)} / {FormatNumber(summary.BidMax)}");
        Console.WriteLine($"Ask min/max: {FormatNumber(summary.AskMin)} / {FormatNumber(summary.AskMax)}");
        Console.WriteLine($"Spread     : min={FormatNumber(summary.SpreadMin)}, avg={FormatNumber(summary.SpreadAverage)}, p50={FormatNumber(summary.SpreadP50)}, p90={FormatNumber(summary.SpreadP90)}, p95={FormatNumber(summary.SpreadP95)}, p99={FormatNumber(summary.SpreadP99)}, max={FormatNumber(summary.SpreadMax)}");
        Console.WriteLine($"Timestamp  : strictly monotonic={summary.IsStrictlyMonotonicTimestamp}, duplicates={summary.DuplicateTimestampCount}, non-monotonic={summary.NonMonotonicTimestampCount}");
        Console.WriteLine($"Bad spread : {summary.ZeroOrNegativeSpreadCount}");
        Console.WriteLine($"Max gap ms : {summary.MaxTimestampGapMs}");
        Console.WriteLine($"Tick/sec   : max={FormatNumber(summary.TickDensity.MaxTicksPerSecondBucket)}, p50={FormatNumber(summary.TickDensity.P50TicksPerSecondBucket)}, p90={FormatNumber(summary.TickDensity.P90TicksPerSecondBucket)}, p95={FormatNumber(summary.TickDensity.P95TicksPerSecondBucket)}, p99={FormatNumber(summary.TickDensity.P99TicksPerSecondBucket)}");

        Console.WriteLine("Large gaps :");
        foreach (var gap in summary.LargeGaps.Take(10))
            Console.WriteLine($"  {gap.PreviousUtc:O} -> {gap.CurrentUtc:O}: {gap.GapMs}ms");
        if (summary.LargeGaps.Count == 0)
            Console.WriteLine("  none");

        Console.WriteLine("High density windows:");
        foreach (var window in summary.HighDensityWindows.Take(10))
            Console.WriteLine($"  {window.BucketStartUtc:O} -> {window.BucketEndUtc:O}: {window.TickCount} ticks");

        Console.WriteLine("JST hourly density:");
        Console.WriteLine("  hour minutes total avg/min p95/min max/min p95/sec max/sec");
        foreach (var row in summary.HourlyDensityJst)
        {
            Console.WriteLine(
                $"  {row.HourJst:00}   {row.MinuteBuckets,7} {row.TotalTicks,9} {FormatNumber(row.AverageTicksPerMinute),7} {row.P95TicksPerMinute,7} {row.MaxTicksPerMinute,7} {FormatNumber(row.P95TicksPerSecond),7} {FormatNumber(row.MaxTicksPerSecond),7}");
        }

        if (summary.CadenceEstimates.Count > 0)
        {
            Console.WriteLine("Cadence estimates:");
            foreach (var estimate in summary.CadenceEstimates)
                Console.WriteLine($"  {estimate.CadenceMs}ms: {estimate.ObservedBuckets}/{estimate.RawTicks} ({FormatPercent(estimate.ObservedRatio)})");
        }
    }

    private static IReadOnlyList<HistogramRow> BuildHistogram(IReadOnlyList<TickData> records, long bucketMs)
    {
        return records
            .GroupBy(x => x.TimestampMs / bucketMs * bucketMs)
            .OrderBy(x => x.Key)
            .Select(group =>
            {
                var ordered = group.OrderBy(x => x.TimestampMs).ToArray();
                var spreads = ordered.Select(x => x.Spread).Order().ToArray();
                var gaps = BuildTimestampGaps(ordered);
                var bucketStart = DateTimeOffset.FromUnixTimeMilliseconds(group.Key);
                return new HistogramRow(
                    BucketStartUtc: bucketStart,
                    BucketEndUtc: bucketStart.AddMilliseconds(bucketMs),
                    TickCount: ordered.Length,
                    TicksPerSecond: ordered.Length / (bucketMs / 1000d),
                    FirstBid: ordered[0].Bid,
                    FirstAsk: ordered[0].Ask,
                    LastBid: ordered[^1].Bid,
                    LastAsk: ordered[^1].Ask,
                    BidHigh: ordered.Max(x => x.Bid),
                    BidLow: ordered.Min(x => x.Bid),
                    AskHigh: ordered.Max(x => x.Ask),
                    AskLow: ordered.Min(x => x.Ask),
                    SpreadAverage: spreads.Average(),
                    SpreadP95: PercentileSorted(spreads, 0.95),
                    SpreadMax: spreads[^1],
                    MaxTimestampGapMs: gaps.Count == 0 ? 0 : gaps.Max(x => x.GapMs));
            })
            .ToArray();
    }

    private static IReadOnlyList<HourlyDensityRow> BuildHourlyDensity(IReadOnlyList<TickData> records, TimeZoneInfo timeZone)
    {
        return records
            .GroupBy(x =>
            {
                var local = TimeZoneInfo.ConvertTime(x.TimestampUtc, timeZone);
                var minuteStart = new DateTimeOffset(
                    local.Year,
                    local.Month,
                    local.Day,
                    local.Hour,
                    local.Minute,
                    0,
                    local.Offset);
                return minuteStart;
            })
            .Select(x => new
            {
                MinuteStart = x.Key,
                TickCount = x.Count()
            })
            .GroupBy(x => x.MinuteStart.Hour)
            .OrderBy(x => x.Key)
            .Select(x =>
            {
                var minuteCounts = x.Select(y => y.TickCount).Order().ToArray();
                return new HourlyDensityRow(
                    HourJst: x.Key,
                    MinuteBuckets: minuteCounts.Length,
                    TotalTicks: minuteCounts.Sum(),
                    AverageTicksPerMinute: minuteCounts.Average(),
                    P95TicksPerMinute: (int)PercentileSorted(minuteCounts.Select(y => (double)y).ToArray(), 0.95),
                    MaxTicksPerMinute: minuteCounts[^1],
                    P95TicksPerSecond: PercentileSorted(minuteCounts.Select(y => y / 60d).Order().ToArray(), 0.95),
                    MaxTicksPerSecond: minuteCounts[^1] / 60d);
            })
            .ToArray();
    }

    private static IReadOnlyList<TimestampGap> BuildTimestampGaps(IReadOnlyList<TickData> records)
    {
        var gaps = new List<TimestampGap>(Math.Max(0, records.Count - 1));
        for (var i = 1; i < records.Count; i++)
        {
            var previous = records[i - 1];
            var current = records[i];
            gaps.Add(new TimestampGap(previous.TimestampUtc, current.TimestampUtc, current.TimestampMs - previous.TimestampMs));
        }

        return gaps;
    }

    private static IReadOnlyList<DensityWindow> BuildDensityBuckets(IReadOnlyList<TickData> records, long bucketMs)
    {
        return records
            .GroupBy(x => x.TimestampMs / bucketMs * bucketMs)
            .Select(x =>
            {
                var start = DateTimeOffset.FromUnixTimeMilliseconds(x.Key);
                return new DensityWindow(start, start.AddMilliseconds(bucketMs), x.Count());
            })
            .ToArray();
    }

    private static IReadOnlyList<CadenceEstimate> BuildCadenceEstimates(IReadOnlyList<TickData> records, IReadOnlyList<int> cadenceMs)
    {
        return cadenceMs
            .Select(cadence => new CadenceEstimate(
                cadence,
                records.Select(x => x.TimestampMs / cadence).Distinct().LongCount(),
                records.Count,
                records.Count == 0 ? 0 : (double)records.Select(x => x.TimestampMs / cadence).Distinct().LongCount() / records.Count))
            .ToArray();
    }

    private static void WriteGroups(
        TickLogData data,
        IReadOnlyList<TickGroup> groups,
        string outputRoot,
        bool overwrite,
        bool dryRun,
        bool copySidecar)
    {
        if (groups.Count == 0)
        {
            Console.WriteLine("No output groups.");
            return;
        }

        foreach (var group in groups)
        {
            if (File.Exists(group.OutputPath) && !overwrite)
                throw new IOException($"Output file already exists: {group.OutputPath}. Use --overwrite to replace it.");

            var outputSidecar = Path.ChangeExtension(group.OutputPath, ".meta.json");
            if (copySidecar && File.Exists(GetInputSidecarPath(data.Path)) && File.Exists(outputSidecar) && !overwrite)
                throw new IOException($"Output sidecar already exists: {outputSidecar}. Use --overwrite to replace it.");
        }

        foreach (var group in groups)
        {
            Console.WriteLine($"{(dryRun ? "Would write" : "Writing")} {group.Records.Length} ticks: {group.OutputPath}");
            if (dryRun)
                continue;

            var parent = Path.GetDirectoryName(group.OutputPath) ?? outputRoot;
            Directory.CreateDirectory(parent);
            WriteTickLogFile(data, group.Records, group.OutputPath, overwrite);

            if (copySidecar)
                CopySidecarIfExists(data.Path, group.OutputPath, overwrite);
        }
    }

    private static void WriteTickLogFile(TickLogData data, IReadOnlyList<TickData> records, string outputPath, bool overwrite)
    {
        if (records.Count == 0)
            return;

        var outputParent = Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory;
        var tempRoot = Path.Combine(outputParent, $".tmp-tlog-{Guid.NewGuid():N}");
        try
        {
            string tempFilePath;
            using (var writer = new TickBinaryWriter(
                tempRoot,
                data.Header.SourceKind,
                data.Header.Broker,
                data.Header.Symbol,
                data.Header.TickSize,
                data.Header.Digits,
                records[0].TimestampMs))
            {
                foreach (var record in records)
                    writer.Write(record.TimestampMs, record.Bid, record.Ask);

                writer.Flush();
                tempFilePath = writer.FilePath;
            }

            if (File.Exists(outputPath))
                File.Delete(outputPath);

            File.Move(tempFilePath, outputPath);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void CopySidecarIfExists(string inputTickLogPath, string outputTickLogPath, bool overwrite)
    {
        var inputSidecar = GetInputSidecarPath(inputTickLogPath);
        if (!File.Exists(inputSidecar))
            return;

        var outputSidecar = Path.ChangeExtension(outputTickLogPath, ".meta.json");
        if (File.Exists(outputSidecar) && !overwrite)
            throw new IOException($"Output sidecar already exists: {outputSidecar}. Use --overwrite to replace it.");

        File.Copy(inputSidecar, outputSidecar, overwrite);
    }

    private static string GetInputSidecarPath(string inputTickLogPath) => Path.ChangeExtension(inputTickLogPath, ".meta.json");

    private static void WriteHistogramCsv(string path, IReadOnlyList<HistogramRow> rows)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("bucket_start_utc,bucket_end_utc,tick_count,tick_per_sec,first_bid,first_ask,last_bid,last_ask,bid_high,bid_low,ask_high,ask_low,spread_avg,spread_p95,spread_max,max_timestamp_gap_ms");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(',',
                Csv(row.BucketStartUtc.ToString("O", CultureInfo.InvariantCulture)),
                Csv(row.BucketEndUtc.ToString("O", CultureInfo.InvariantCulture)),
                row.TickCount.ToString(CultureInfo.InvariantCulture),
                FormatNumber(row.TicksPerSecond),
                FormatNumber(row.FirstBid),
                FormatNumber(row.FirstAsk),
                FormatNumber(row.LastBid),
                FormatNumber(row.LastAsk),
                FormatNumber(row.BidHigh),
                FormatNumber(row.BidLow),
                FormatNumber(row.AskHigh),
                FormatNumber(row.AskLow),
                FormatNumber(row.SpreadAverage),
                FormatNumber(row.SpreadP95),
                FormatNumber(row.SpreadMax),
                row.MaxTimestampGapMs.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static string ResolveAnalysisOutputPath(string? outputPath, string tlogPath, string suffix, string extension)
    {
        var defaultFileName = $"{Path.GetFileNameWithoutExtension(tlogPath)}{suffix}{extension}";
        if (string.IsNullOrWhiteSpace(outputPath))
            return Path.Combine(Environment.CurrentDirectory, defaultFileName);

        var fullPath = Path.GetFullPath(outputPath);
        if (Directory.Exists(fullPath) || HasDirectorySeparatorSuffix(outputPath) || string.IsNullOrEmpty(Path.GetExtension(fullPath)))
            return Path.Combine(fullPath, defaultFileName);

        return fullPath;
    }

    private static bool HasDirectorySeparatorSuffix(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) ||
               path.EndsWith(Path.AltDirectorySeparatorChar);
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException) when (string.Equals(id, "Asia/Tokyo", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
        }
        catch (InvalidTimeZoneException) when (string.Equals(id, "Asia/Tokyo", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
        }
    }

    private static DateTimeOffset ParseInstant(string value, TimeZoneInfo timeZone)
    {
        if (HasExplicitOffset(value))
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

        var local = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault);
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone), TimeSpan.Zero);
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z'))
            return true;

        if (value.Length < 6)
            return false;

        var suffix = value.AsSpan(value.Length - 6);
        return (suffix[0] == '+' || suffix[0] == '-') &&
               char.IsDigit(suffix[1]) &&
               char.IsDigit(suffix[2]) &&
               suffix[3] == ':' &&
               char.IsDigit(suffix[4]) &&
               char.IsDigit(suffix[5]);
    }

    private static long ResolveWindowOriginMs(DateTimeOffset firstTickUtc, string align, TimeZoneInfo timeZone)
    {
        if (string.Equals(align, "first", StringComparison.OrdinalIgnoreCase))
            return firstTickUtc.ToUnixTimeMilliseconds();

        if (!string.Equals(align, "day", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--align must be either first or day.");

        var local = TimeZoneInfo.ConvertTime(firstTickUtc, timeZone);
        var localMidnight = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localMidnight, timeZone), TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    private static long ParseBucket(string value, long[] allowedMs)
    {
        var ms = ParseDurationMs(value);
        if (!allowedMs.Contains(ms))
            throw new ArgumentException($"Unsupported duration '{value}'.");

        return ms;
    }

    private static long ParseDurationMs(string value)
    {
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(value[..^2], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) &&
            milliseconds > 0)
        {
            return milliseconds;
        }

        if (value.Length < 2)
            throw new ArgumentException($"Invalid duration '{value}'.");

        var unit = value[^1];
        if (!long.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            throw new ArgumentException($"Invalid duration '{value}'.");

        return unit switch
        {
            's' or 'S' => amount * 1_000,
            'm' or 'M' => amount * 60_000,
            'h' or 'H' => amount * 3_600_000,
            _ => throw new ArgumentException($"Invalid duration unit in '{value}'.")
        };
    }

    private static IReadOnlyList<int> ParseCadences(string? cadenceMs)
    {
        if (string.IsNullOrWhiteSpace(cadenceMs))
            return [];

        return cadenceMs
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x =>
            {
                if (!int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
                    throw new ArgumentException($"Invalid cadence value: '{x}'.");

                return value;
            })
            .Distinct()
            .Order()
            .ToArray();
    }

    private static double PercentileSorted(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
            return 0;

        if (sortedValues.Count == 1)
            return sortedValues[0];

        var position = (sortedValues.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sortedValues[lower];

        var weight = position - lower;
        return sortedValues[lower] * (1 - weight) + sortedValues[upper] * weight;
    }

    private static string Csv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string SanitizeFileNamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(invalid.Contains(ch) ? '_' : ch);

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static string FormatNumber(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    private static string FormatPercent(double value) => value.ToString("0.##%", CultureInfo.InvariantCulture);
}

internal sealed record TickLogHeader(
    TickSourceKind SourceKind,
    string Broker,
    string Symbol,
    int Digits,
    double TickSize,
    long PriceScale,
    long SessionStartMs);

internal sealed record TickLogData(string Path, TickLogHeader Header, TickData[] Records);

internal sealed record TickData(long TimestampMs, DateTimeOffset TimestampUtc, double Bid, double Ask)
{
    public double Spread => Ask - Bid;
}

internal sealed record TickGroup(string OutputPath, TickData[] Records);

internal sealed record SummaryDocument(
    string FilePath,
    string Symbol,
    string Broker,
    string SourceKind,
    int Digits,
    double TickSize,
    long PriceScale,
    int RecordCount,
    DateTimeOffset FirstTickUtc,
    DateTimeOffset FirstTickLocal,
    DateTimeOffset LastTickUtc,
    DateTimeOffset LastTickLocal,
    TimeSpan Duration,
    double AverageTicksPerSecond,
    double AverageTicksPerMinute,
    double AverageTicksPerHour,
    double BidMin,
    double BidMax,
    double AskMin,
    double AskMax,
    double SpreadMin,
    double SpreadMax,
    double SpreadAverage,
    double SpreadP50,
    double SpreadP90,
    double SpreadP95,
    double SpreadP99,
    bool IsStrictlyMonotonicTimestamp,
    int DuplicateTimestampCount,
    int NonMonotonicTimestampCount,
    int ZeroOrNegativeSpreadCount,
    long MaxTimestampGapMs,
    IReadOnlyList<TimestampGap> LargeGaps,
    IReadOnlyList<DensityWindow> HighDensityWindows,
    IReadOnlyList<HourlyDensityRow> HourlyDensityJst,
    TickDensitySummary TickDensity,
    IReadOnlyList<CadenceEstimate> CadenceEstimates);

internal sealed record TimestampGap(DateTimeOffset PreviousUtc, DateTimeOffset CurrentUtc, long GapMs);

internal sealed record DensityWindow(DateTimeOffset BucketStartUtc, DateTimeOffset BucketEndUtc, int TickCount);

internal sealed record HourlyDensityRow(
    int HourJst,
    int MinuteBuckets,
    int TotalTicks,
    double AverageTicksPerMinute,
    int P95TicksPerMinute,
    int MaxTicksPerMinute,
    double P95TicksPerSecond,
    double MaxTicksPerSecond);

internal sealed record TickDensitySummary(
    double MaxTicksPerSecondBucket,
    double P50TicksPerSecondBucket,
    double P90TicksPerSecondBucket,
    double P95TicksPerSecondBucket,
    double P99TicksPerSecondBucket);

internal sealed record CadenceEstimate(int CadenceMs, long ObservedBuckets, int RawTicks, double ObservedRatio);

internal sealed record HistogramRow(
    DateTimeOffset BucketStartUtc,
    DateTimeOffset BucketEndUtc,
    int TickCount,
    double TicksPerSecond,
    double FirstBid,
    double FirstAsk,
    double LastBid,
    double LastAsk,
    double BidHigh,
    double BidLow,
    double AskHigh,
    double AskLow,
    double SpreadAverage,
    double SpreadP95,
    double SpreadMax,
    long MaxTimestampGapMs);
