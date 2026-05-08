using System.Globalization;

internal static class TickParsing
{
    internal static long ParseDurationMs(string value)
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

    internal static long ParseBucket(string value, long[] allowedMs)
    {
        var ms = ParseDurationMs(value);
        if (!allowedMs.Contains(ms))
            throw new ArgumentException($"Unsupported duration '{value}'.");

        return ms;
    }

    internal static IReadOnlyList<int> ParseCadences(string? cadenceMs)
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

    internal static DateTimeOffset ParseInstant(string value, TimeZoneInfo timeZone)
    {
        if (HasExplicitOffset(value))
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

        var local = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault);
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone), TimeSpan.Zero);
    }

    internal static bool HasExplicitOffset(string value)
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

    internal static TimeZoneInfo ResolveTimeZone(string id)
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

    internal static long ResolveWindowOriginMs(DateTimeOffset firstTickUtc, string align, TimeZoneInfo timeZone)
    {
        if (string.Equals(align, "first", StringComparison.OrdinalIgnoreCase))
            return firstTickUtc.ToUnixTimeMilliseconds();

        if (!string.Equals(align, "day", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--align must be either first or day.");

        var local = TimeZoneInfo.ConvertTime(firstTickUtc, timeZone);
        var localMidnight = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localMidnight, timeZone), TimeSpan.Zero).ToUnixTimeMilliseconds();
    }
}
