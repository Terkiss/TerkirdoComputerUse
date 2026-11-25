namespace TerkirdoComputer.Core;

public enum TextSelectionMode
{
    Text,
    CursorBefore,
    CursorAfter
}

public static class TextSelectionModeExtensions
{
    public static TextSelectionMode Parse(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return TextSelectionMode.Text;
        }

        return rawValue.Trim().ToLowerInvariant() switch
        {
            "text" => TextSelectionMode.Text,
            "cursor_before" => TextSelectionMode.CursorBefore,
            "cursor_after" => TextSelectionMode.CursorAfter,
            _ => throw new ArgumentException($"Unsupported selection mode \"{rawValue}\". Use text, cursor_before, or cursor_after.")
        };
    }

    public static string ToWireValue(this TextSelectionMode mode) => mode switch
    {
        TextSelectionMode.Text => "text",
        TextSelectionMode.CursorBefore => "cursor_before",
        TextSelectionMode.CursorAfter => "cursor_after",
        _ => "text"
    };
}

public readonly record struct ResolvedTextSelection(int Location, int Length);

public static class TextSelectionResolver
{
    public static ResolvedTextSelection Resolve(
        string source,
        string text,
        string? prefix,
        string? suffix,
        TextSelectionMode mode,
        int elementIndex)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("select_text requires a non-empty text string");
        }

        source ??= string.Empty;
        string? normalizedPrefix = NormalizeOptionalFilter(prefix);
        string? normalizedSuffix = NormalizeOptionalFilter(suffix);

        var matches = FindAllMatches(source, text)
            .Where(match =>
                MatchesPrefix(source, match, normalizedPrefix) &&
                MatchesSuffix(source, match, normalizedSuffix))
            .ToList();

        if (matches.Count == 0)
        {
            string context = (normalizedPrefix != null || normalizedSuffix != null)
                ? " with the specified prefix/suffix"
                : string.Empty;
            throw new ArgumentException($"select_text could not find the requested text{context} in element [{elementIndex}]");
        }

        if (matches.Count > 1)
        {
            throw new ArgumentException(
                $"select_text matched {matches.Count} occurrences in element [{elementIndex}]; provide prefix and/or suffix, or include more surrounding text");
        }

        var selected = matches[0];
        return mode switch
        {
            TextSelectionMode.Text => new ResolvedTextSelection(selected.Location, selected.Length),
            TextSelectionMode.CursorBefore => new ResolvedTextSelection(selected.Location, 0),
            TextSelectionMode.CursorAfter => new ResolvedTextSelection(selected.Location + selected.Length, 0),
            _ => new ResolvedTextSelection(selected.Location, selected.Length)
        };
    }

    private static string? NormalizeOptionalFilter(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static List<ResolvedTextSelection> FindAllMatches(string source, string needle)
    {
        if (string.IsNullOrEmpty(needle) || source.Length < needle.Length)
        {
            return [];
        }

        var results = new List<ResolvedTextSelection>();
        int searchStart = 0;
        while (searchStart <= source.Length - needle.Length)
        {
            int found = source.IndexOf(needle, searchStart, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }
            results.Add(new ResolvedTextSelection(found, needle.Length));
            searchStart = found + 1;
        }
        return results;
    }

    private static bool MatchesPrefix(string source, ResolvedTextSelection match, string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return true;
        }
        if (match.Location < prefix.Length)
        {
            return false;
        }
        return source.AsSpan(match.Location - prefix.Length, prefix.Length)
            .SequenceEqual(prefix.AsSpan());
    }

    private static bool MatchesSuffix(string source, ResolvedTextSelection match, string? suffix)
    {
        if (string.IsNullOrEmpty(suffix))
        {
            return true;
        }
        int end = match.Location + match.Length;
        if (end + suffix.Length > source.Length)
        {
            return false;
        }
        return source.AsSpan(end, suffix.Length)
            .SequenceEqual(suffix.AsSpan());
    }
}
