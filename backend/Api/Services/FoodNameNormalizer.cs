using System.Text.RegularExpressions;

namespace Api.Services;

/// <summary>
/// Normalize a food name for dedup matching: trim → lowercase → strip
/// parenthesized qualifiers → collapse internal whitespace → final trim.
/// Used identically by estimation matching, food creation, and migration
/// backfill so the same logical name always resolves to the same key.
/// </summary>
public static partial class FoodNameNormalizer
{
    /// <summary>Normalize a raw food name into its canonical dedup key.</summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var s = raw.Trim().ToLowerInvariant();

        // Strip parenthetical qualifiers — "Schnitzel (groß)" → "schnitzel"
        var paren = s.IndexOf('(');
        if (paren >= 0)
            s = s[..paren];

        // Collapse internal whitespace to single spaces
        s = WhitespaceRegex().Replace(s, " ");

        return s.Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
