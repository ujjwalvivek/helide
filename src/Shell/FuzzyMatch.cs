namespace Helide.Shell;

/// <summary>
/// Ranks a palette entry against what the user typed.
/// </summary>
/// <remarks>
/// Subsequence matching, so "ntp" finds "New Terminal Pane" without the user caring
/// about word boundaries. Scoring exists only to order the results that all match:
/// <list type="bullet">
/// <item>consecutive characters score higher than scattered ones, so "op" prefers
/// "Open Folder" over "Close Opened Workspace";</item>
/// <item>a match at the start of a word scores higher than one mid-word, so "gp"
/// prefers "Git Panel" over "Toggle Panel";</item>
/// <item>earlier and shorter matches score higher, so a tight hit outranks a
/// coincidental one spread across a long title.</item>
/// </list>
/// Every match returns a positive score, which is what makes it usable as a filter:
/// zero and below means "not a match at all".
/// </remarks>
internal static class FuzzyMatch
{
    // Weights are arbitrary but ordered, so a match that is both consecutive and
    // word-initial beats one that is only one of the two.
    private const int WordStartBonus = 40;
    private const int ConsecutiveBonus = 30;
    private const int MatchScore = 12;
    private const int LeadingGapPenalty = 2;
    private const int InnerGapPenalty = 1;

    /// <summary>
    /// Scores <paramref name="query"/> against <paramref name="text"/>. Higher is a
    /// better match; zero or less means the query is not a subsequence of the text.
    /// </summary>
    public static int Score(string text, string? query)
    {
        if (string.IsNullOrEmpty(query))
            return 1;

        if (string.IsNullOrEmpty(text))
            return 0;

        var score = 0;
        var textIndex = 0;
        var previousMatchIndex = -1;

        foreach (var c in query)
        {
            if (char.IsWhiteSpace(c))
                continue;

            var found = -1;
            for (var i = textIndex; i < text.Length; i++)
            {
                if (char.ToUpperInvariant(text[i]) == char.ToUpperInvariant(c))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
                return 0;

            score += MatchScore;

            if (found == 0 || IsWordBoundary(text[found - 1]))
                score += WordStartBonus;

            if (previousMatchIndex >= 0 && found == previousMatchIndex + 1)
                score += ConsecutiveBonus;
            else
            {
                // Penalise the gap between matches, but only up to a point: a long title
                // is not a better match for having more characters to skip over.
                var gap = found - textIndex;
                score -= gap <= 0 ? 0 : gap * (found == 0 ? LeadingGapPenalty : InnerGapPenalty);
            }

            previousMatchIndex = found;
            textIndex = found + 1;
        }

        // Shorter titles win ties, so "Exit" outranks "Close Workspace" for "e".
        return score - (text.Length / 8);
    }

    /// <summary>Best score across the title and the hidden keywords.</summary>
    public static int ScoreCommand(string title, string? keywords, string? query)
    {
        var score = Score(title, query);
        if (score <= 0 && !string.IsNullOrEmpty(keywords))
            score = Score(keywords, query);

        return score;
    }

    private static bool IsWordBoundary(char c) =>
        c is ' ' or '-' or '_' or '/' or '\\' or '.' or ':' or '&' or '(';
}
