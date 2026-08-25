using System.Text.RegularExpressions;

namespace ClipsToDiscord;

internal enum MediaToolOperation
{
    General,
    SilhouetteMatte,
    SilhouetteMatteInspection
}

/// <summary>
/// Converts untrusted media-tool diagnostics into a small allow-listed support summary.
/// FFmpeg repeats input/output paths and command fragments in stderr, so redacting a few
/// path shapes is not sufficient: filenames, usernames, UNC shares, URLs, and tokens can
/// otherwise survive in surrounding text. This type copies none of the diagnostic text.
/// </summary>
internal static partial class MediaToolDiagnosticSanitizer
{
    private const int MaximumDiagnosticCharactersToInspect = 65_536;
    private const int MaximumCategories = 8;
    private const int MaximumCodes = 4;

    private static readonly DiagnosticCategory[] Categories =
    [
        new("permission denied", ["permission denied", "access is denied", "access denied"]),
        new("input media not found", ["no such file or directory", "file not found"]),
        new("invalid media data", ["invalid data found", "invalid data", "could not find codec parameters"]),
        new("invalid media argument", ["invalid argument", "option not found", "unrecognized option", "unknown option"]),
        new("media codec unavailable", ["unknown encoder", "encoder not found", "unknown decoder", "decoder not found", "codec not found"]),
        new("media codec initialization failed", ["error initializing output stream", "error while opening encoder", "could not open codec"]),
        new("input media could not be opened", ["error opening input", "failed to open input"]),
        new("output media could not be opened", ["error opening output", "failed to open output"]),
        new("media filter failed", ["error while filtering", "failed to configure output pad", "filter graph"]),
        new("media conversion failed", ["conversion failed", "error writing trailer", "error muxing a packet"]),
        new("storage is full", ["no space left on device", "disk quota exceeded"]),
        new("media device is busy", ["device or resource busy", "resource temporarily unavailable"]),
        new("media pipe closed", ["broken pipe", "pipe has been ended"]),
        new("memory allocation failed", ["cannot allocate memory", "out of memory"]),
        new("hardware acceleration failed", ["hardware acceleration", "device setup failed", "failed to initialise vaapi", "failed to initialize nvenc"])
    ];

    internal static string BuildFfmpegFailureMessage(
        MediaToolOperation operation,
        int exitCode,
        string? diagnostic)
    {
        var operationText = operation switch
        {
            MediaToolOperation.General => "exited",
            MediaToolOperation.SilhouetteMatte => "failed during silhouette matte generation",
            MediaToolOperation.SilhouetteMatteInspection => "failed during silhouette matte inspection",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
        return $"FFmpeg {operationText} with code {exitCode}. {Summarize(diagnostic)}";
    }

    internal static string Summarize(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return "Diagnostic category: unspecified media-tool failure.";
        }

        var truncated = diagnostic.Length > MaximumDiagnosticCharactersToInspect;
        var inspected = truncated
            ? diagnostic[^MaximumDiagnosticCharactersToInspect..]
            : diagnostic;
        var categories = Categories
            .Where(category => category.Markers.Any(marker =>
                inspected.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .Select(category => category.Label)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumCategories)
            .ToArray();
        var codes = DiagnosticCodePattern().Matches(inspected)
            .Select(match => match.Groups[1].Value)
            .Where(IsSafeDiagnosticCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumCodes)
            .ToArray();

        var categoryText = categories.Length == 0
            ? "Diagnostic category: unclassified media-tool failure."
            : $"Diagnostic categories: {string.Join(", ", categories)}.";
        var codeText = codes.Length == 0
            ? string.Empty
            : $" Diagnostic codes: {string.Join(", ", codes)}.";
        var truncationText = truncated
            ? " Only the bounded diagnostic tail was classified."
            : string.Empty;
        return categoryText + codeText + truncationText;
    }

    private static bool IsSafeDiagnosticCode(string value) =>
        value.Length is > 0 and <= 12 &&
        (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..].All(Uri.IsHexDigit)
            : value.TrimStart('-', '+').All(char.IsDigit));

    [GeneratedRegex(
        @"(?:error|errno|code)\s*(?:number|code)?\s*[:=]?\s*(-?(?:0x[0-9a-f]{1,8}|\d{1,10}))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticCodePattern();

    private sealed record DiagnosticCategory(string Label, string[] Markers);
}
