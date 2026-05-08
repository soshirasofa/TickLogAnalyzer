internal static class TickStats
{
    internal static double PercentileSorted(IReadOnlyList<double> sortedValues, double percentile)
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

    internal static IReadOnlyList<TimestampGap> BuildTimestampGaps(IReadOnlyList<TickData> records)
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

    internal static IReadOnlyList<DensityWindow> BuildDensityBuckets(IReadOnlyList<TickData> records, long bucketMs)
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

    internal static IReadOnlyList<CadenceEstimate> BuildCadenceEstimates(IReadOnlyList<TickData> records, IReadOnlyList<int> cadenceMs)
    {
        return cadenceMs
            .Select(cadence => new CadenceEstimate(
                cadence,
                records.Select(x => x.TimestampMs / cadence).Distinct().LongCount(),
                records.Count,
                records.Count == 0 ? 0 : (double)records.Select(x => x.TimestampMs / cadence).Distinct().LongCount() / records.Count))
            .ToArray();
    }

    internal static IReadOnlyList<HourlyDensityRow> BuildHourlyDensity(IReadOnlyList<TickData> records, TimeZoneInfo timeZone)
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

    internal static IReadOnlyList<HistogramRow> BuildHistogram(IReadOnlyList<TickData> records, long bucketMs)
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

    internal static SummaryDocument BuildSummary(TickLogData data, IReadOnlyList<int> cadences, long largeGapMs, TimeZoneInfo japanTimeZone)
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
            HourlyDensityJst: BuildHourlyDensity(records, japanTimeZone),
            TickDensity: new TickDensitySummary(
                MaxTicksPerSecondBucket: ticksPerSecondBuckets.Length == 0 ? records.Length : ticksPerSecondBuckets[^1],
                P50TicksPerSecondBucket: PercentileSorted(ticksPerSecondBuckets, 0.50),
                P90TicksPerSecondBucket: PercentileSorted(ticksPerSecondBuckets, 0.90),
                P95TicksPerSecondBucket: PercentileSorted(ticksPerSecondBuckets, 0.95),
                P99TicksPerSecondBucket: PercentileSorted(ticksPerSecondBuckets, 0.99)),
            CadenceEstimates: BuildCadenceEstimates(records, cadences));
    }
}
