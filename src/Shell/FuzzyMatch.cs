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
/// <para>
/// Whether something matches at all is stricter than the scoring: each word typed has to
/// be found inside a word of the text, or as the starts of words that follow each other
/// ("ntp", "newterm" for "New Terminal Pane"). Letters merely appearing in order across
/// a long text, as "yazooo" does in almost any line of keymap, are not a match.
/// </para>
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
        if (string.IsNullOrWhiteSpace(query))
            return 1;

        if (Matches(title, query))
            return Math.Max(Score(title, query), 2);

        if (string.IsNullOrEmpty(keywords) || !Matches(keywords, query))
            return 0;

        // Found only in the hidden text: below every title match, and all equal, since
        // where in a conversation or a keyword list the words fall says nothing about
        // which is the better result.
        return 1;
    }

    // Past this, a text is searched for each word as written, without also trying the
    // starts of its words, which would mean splitting it into words on every key.
    private const int LongText = 4096;

    /// <summary>Whether every word of the query is found in the text: see the remarks.</summary>
    public static bool Matches(string text, string query)
    {
        var words = text.Length > LongText ? [] : Words(text);
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                continue;

            var found = false;
            for (var start = 0; start < words.Count && !found; start++)
                found = StartsOfWords(term, words, start);

            if (!found)
                return false;
        }

        return true;
    }

    // The term, taken as the start of this word followed by the starts of the next ones.
    private static bool StartsOfWords(string term, List<string> words, int index)
    {
        if (term.Length == 0)
            return true;
        if (index >= words.Count)
            return false;

        var word = words[index];
        for (var length = Math.Min(term.Length, word.Length); length >= 1; length--)
        {
            if (string.Compare(term, 0, word, 0, length, StringComparison.OrdinalIgnoreCase) == 0
                && StartsOfWords(term[length..], words, index + 1))
                return true;
        }

        return false;
    }

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var boundary = i == text.Length || IsWordBoundary(text[i]) || text[i] == '+';
            if (boundary)
            {
                if (start >= 0)
                    words.Add(text[start..i]);
                start = -1;
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        return words;
    }

    private static bool IsWordBoundary(char c) =>
        c is ' ' or '-' or '_' or '/' or '\\' or '.' or ':' or '&' or '(';
}
