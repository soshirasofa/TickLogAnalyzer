using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using TickLog;

public sealed class TickLogAnalyzerCommands
{
    private static readonly TimeZoneInfo JapanTimeZone = TickParsing.ResolveTimeZone("Asia/Tokyo");

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
        var cadences = TickParsing.ParseCadences(cadenceMs);
        var summary = TickStats.BuildSummary(data, cadences, largeGapMs, JapanTimeZone);
        var jsonPath = TickFormatting.ResolveAnalysisOutputPath(jsonOut, data.Path, ".summary", ".json");

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
        var bucketWidth = TickParsing.ParseBucket(bucket, [1_000, 10_000, 60_000, 300_000, 900_000, 3_600_000]);
        var rows = TickStats.BuildHistogram(data.Records, bucketWidth);
        var csvPath = TickFormatting.ResolveAnalysisOutputPath(csvOut, data.Path, $".histogram.{TickFormatting.SanitizeFileNamePart(bucket)}", ".csv");

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
                Console.WriteLine($"  {row.BucketStartUtc:O},{row.BucketEndUtc:O},{row.TickCount},{TickFormatting.FormatNumber(row.TicksPerSecond)}");
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
        var timeZone = TickParsing.ResolveTimeZone(timezone);
        var outputRoot = Path.GetFullPath(@out);
        var groups = data.Records
            .GroupBy(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.TimestampUtc, timeZone).Date))
            .OrderBy(x => x.Key)
            .Select(x => new TickGroup(
                Path.Combine(
                    outputRoot,
                    TickFormatting.SanitizeFileNamePart(data.Header.Symbol),
                    $"{x.Key:yyyyMMdd}_{TickFormatting.SanitizeFileNamePart(data.Header.Symbol)}_{TickFormatting.SanitizeFileNamePart(data.Header.Broker)}.tlog"),
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
        var timeZone = TickParsing.ResolveTimeZone(timezone);
        var fromUtc = TickParsing.ParseInstant(from, timeZone);
        var toUtc = to is not null
            ? TickParsing.ParseInstant(to, timeZone)
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
        var fileName = $"{fromUtc:yyyyMMdd_HHmmss}_{toUtc:yyyyMMdd_HHmmss}_{TickFormatting.SanitizeFileNamePart(data.Header.Symbol)}_{TickFormatting.SanitizeFileNamePart(data.Header.Broker)}.tlog";
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
        var windowMs = TickParsing.ParseBucket(window, [3_600_000, 7_200_000, 10_800_000, 14_400_000, 21_600_000, 43_200_000]);
        var originMs = TickParsing.ResolveWindowOriginMs(data.Records[0].TimestampUtc, align, TickParsing.ResolveTimeZone(timezone));
        var outputRoot = Path.GetFullPath(@out);

        var groups = data.Records
            .GroupBy(x => originMs + ((x.TimestampMs - originMs) / windowMs) * windowMs)
            .OrderBy(x => x.Key)
            .Select(x =>
            {
                var start = DateTimeOffset.FromUnixTimeMilliseconds(x.Key);
                var end = start.AddMilliseconds(windowMs);
                var fileName = $"{start:yyyyMMdd_HHmmss}_{end:yyyyMMdd_HHmmss}_{TickFormatting.SanitizeFileNamePart(data.Header.Symbol)}_{TickFormatting.SanitizeFileNamePart(data.Header.Broker)}.tlog";
                return new TickGroup(Path.Combine(outputRoot, TickFormatting.SanitizeFileNamePart(data.Header.Symbol), fileName), x.ToArray());
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

    private static void PrintSummary(SummaryDocument summary)
    {
        Console.WriteLine("Tick log summary");
        Console.WriteLine($"File       : {summary.FilePath}");
        Console.WriteLine($"Symbol     : {summary.Symbol}");
        Console.WriteLine($"Broker     : {summary.Broker}");
        Console.WriteLine($"Source     : {summary.SourceKind}");
        Console.WriteLine($"Digits     : {summary.Digits}");
        Console.WriteLine($"Tick size  : {TickFormatting.FormatNumber(summary.TickSize)}");
        Console.WriteLine($"Price scale: {summary.PriceScale}");
        Console.WriteLine($"Records    : {summary.RecordCount}");
        Console.WriteLine($"First UTC  : {summary.FirstTickUtc:O}");
        Console.WriteLine($"First local: {summary.FirstTickLocal:O}");
        Console.WriteLine($"Last UTC   : {summary.LastTickUtc:O}");
        Console.WriteLine($"Last local : {summary.LastTickLocal:O}");
        Console.WriteLine($"Duration   : {summary.Duration}");
        Console.WriteLine($"Avg tick/s : {TickFormatting.FormatNumber(summary.AverageTicksPerSecond)}");
        Console.WriteLine($"Avg tick/m : {TickFormatting.FormatNumber(summary.AverageTicksPerMinute)}");
        Console.WriteLine($"Avg tick/h : {TickFormatting.FormatNumber(summary.AverageTicksPerHour)}");
        Console.WriteLine($"Bid min/max: {TickFormatting.FormatNumber(summary.BidMin)} / {TickFormatting.FormatNumber(summary.BidMax)}");
        Console.WriteLine($"Ask min/max: {TickFormatting.FormatNumber(summary.AskMin)} / {TickFormatting.FormatNumber(summary.AskMax)}");
        Console.WriteLine($"Spread     : min={TickFormatting.FormatNumber(summary.SpreadMin)}, avg={TickFormatting.FormatNumber(summary.SpreadAverage)}, p50={TickFormatting.FormatNumber(summary.SpreadP50)}, p90={TickFormatting.FormatNumber(summary.SpreadP90)}, p95={TickFormatting.FormatNumber(summary.SpreadP95)}, p99={TickFormatting.FormatNumber(summary.SpreadP99)}, max={TickFormatting.FormatNumber(summary.SpreadMax)}");
        Console.WriteLine($"Timestamp  : strictly monotonic={summary.IsStrictlyMonotonicTimestamp}, duplicates={summary.DuplicateTimestampCount}, non-monotonic={summary.NonMonotonicTimestampCount}");
        Console.WriteLine($"Bad spread : {summary.ZeroOrNegativeSpreadCount}");
        Console.WriteLine($"Max gap ms : {summary.MaxTimestampGapMs}");
        Console.WriteLine($"Tick/sec   : max={TickFormatting.FormatNumber(summary.TickDensity.MaxTicksPerSecondBucket)}, p50={TickFormatting.FormatNumber(summary.TickDensity.P50TicksPerSecondBucket)}, p90={TickFormatting.FormatNumber(summary.TickDensity.P90TicksPerSecondBucket)}, p95={TickFormatting.FormatNumber(summary.TickDensity.P95TicksPerSecondBucket)}, p99={TickFormatting.FormatNumber(summary.TickDensity.P99TicksPerSecondBucket)}");

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
                $"  {row.HourJst:00}   {row.MinuteBuckets,7} {row.TotalTicks,9} {TickFormatting.FormatNumber(row.AverageTicksPerMinute),7} {row.P95TicksPerMinute,7} {row.MaxTicksPerMinute,7} {TickFormatting.FormatNumber(row.P95TicksPerSecond),7} {TickFormatting.FormatNumber(row.MaxTicksPerSecond),7}");
        }

        if (summary.CadenceEstimates.Count > 0)
        {
            Console.WriteLine("Cadence estimates:");
            foreach (var estimate in summary.CadenceEstimates)
                Console.WriteLine($"  {estimate.CadenceMs}ms: {estimate.ObservedBuckets}/{estimate.RawTicks} ({TickFormatting.FormatPercent(estimate.ObservedRatio)})");
        }
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
                TickFormatting.Csv(row.BucketStartUtc.ToString("O", CultureInfo.InvariantCulture)),
                TickFormatting.Csv(row.BucketEndUtc.ToString("O", CultureInfo.InvariantCulture)),
                row.TickCount.ToString(CultureInfo.InvariantCulture),
                TickFormatting.FormatNumber(row.TicksPerSecond),
                TickFormatting.FormatNumber(row.FirstBid),
                TickFormatting.FormatNumber(row.FirstAsk),
                TickFormatting.FormatNumber(row.LastBid),
                TickFormatting.FormatNumber(row.LastAsk),
                TickFormatting.FormatNumber(row.BidHigh),
                TickFormatting.FormatNumber(row.BidLow),
                TickFormatting.FormatNumber(row.AskHigh),
                TickFormatting.FormatNumber(row.AskLow),
                TickFormatting.FormatNumber(row.SpreadAverage),
                TickFormatting.FormatNumber(row.SpreadP95),
                TickFormatting.FormatNumber(row.SpreadMax),
                row.MaxTimestampGapMs.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
