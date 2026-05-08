using System.Globalization;
using System.Text;

internal static class TickFormatting
{
    internal static string Csv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    internal static string SanitizeFileNamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(invalid.Contains(ch) ? '_' : ch);

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    internal static string FormatNumber(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    internal static string FormatPercent(double value) => value.ToString("0.##%", CultureInfo.InvariantCulture);

    internal static string ResolveAnalysisOutputPath(string? outputPath, string tlogPath, string suffix, string extension)
    {
        var defaultFileName = $"{Path.GetFileNameWithoutExtension(tlogPath)}{suffix}{extension}";
        if (string.IsNullOrWhiteSpace(outputPath))
            return Path.Combine(Environment.CurrentDirectory, defaultFileName);

        var fullPath = Path.GetFullPath(outputPath);
        if (Directory.Exists(fullPath) || HasDirectorySeparatorSuffix(outputPath) || string.IsNullOrEmpty(Path.GetExtension(fullPath)))
            return Path.Combine(fullPath, defaultFileName);

        return fullPath;
    }

    internal static bool HasDirectorySeparatorSuffix(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) ||
               path.EndsWith(Path.AltDirectorySeparatorChar);
    }
}
