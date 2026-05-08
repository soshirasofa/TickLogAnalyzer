using TickLog;

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
