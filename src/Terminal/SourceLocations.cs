using System.IO;
using System.Text.RegularExpressions;

namespace Helide.Terminal;

/// <summary>A place in a file that some output pointed at.</summary>
/// <param name="Line">One-based.</param>
/// <param name="Column">One-based, or null when the output gave none.</param>
/// <param name="Context">The line of output it was found on, to tell several apart.</param>
internal sealed record SourceLocation(string Path, int Line, int? Column, string Context);

/// <summary>
/// Finds file locations in compiler, linter, test and stack-trace output.
/// </summary>
/// <remarks>
/// Three shapes cover nearly every tool: <c>file(12,5)</c> from MSBuild and the C#
/// compiler, <c>file:12:5</c> from gcc, rustc, tsc, eslint, go and most else, and
/// Python's <c>File "file", line 12</c>. A match only counts when the file exists, so
/// times, URLs and version numbers that happen to look like one fall away.
/// </remarks>
internal static class SourceLocations
{
    // A path: an optional drive, then anything that is not whitespace or punctuation a
    // path cannot hold, ending in a file extension.
    private const string PathPattern = @"(?<path>(?:[A-Za-z]:)?[^\s:()""'<>|*?,\[\]]*\.[A-Za-z0-9]{1,10})";

    private static readonly Regex[] Shapes =
    [
        new(PathPattern + @"\((?<line>\d+)(?:,(?<col>\d+))?\)", RegexOptions.Compiled),
        new(PathPattern + @":(?<line>\d+)(?::(?<col>\d+))?", RegexOptions.Compiled),
        new(@"File ""(?<path>[^""]+)"", line (?<line>\d+)", RegexOptions.Compiled),
    ];

    /// <summary>Every location in the text that names a file that exists, in order, once each.</summary>
    /// <param name="baseDirectories">Where a relative path is looked for, first match wins.</param>
    public static IReadOnlyList<SourceLocation> Find(string text, IReadOnlyList<string> baseDirectories)
    {
        var found = new List<SourceLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            foreach (var shape in Shapes)
            {
                foreach (Match match in shape.Matches(line))
                {
                    if (!int.TryParse(match.Groups["line"].Value, out var number) || number <= 0)
                        continue;

                    int? column = int.TryParse(match.Groups["col"].Value, out var col) && col > 0 ? col : null;

                    if (Resolve(match.Groups["path"].Value, baseDirectories) is not { } path)
                        continue;

                    if (seen.Add($"{path}|{number}"))
                        found.Add(new SourceLocation(path, number, column, line));
                }
            }
        }

        return found;
    }

    private static string? Resolve(string candidate, IReadOnlyList<string> baseDirectories)
    {
        try
        {
            if (Path.IsPathRooted(candidate))
                return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;

            foreach (var directory in baseDirectories)
            {
                var full = Path.GetFullPath(Path.Combine(directory, candidate));
                if (File.Exists(full))
                    return full;
            }
        }
        catch (Exception)
        {
            // Not a path after all.
        }

        return null;
    }
}
