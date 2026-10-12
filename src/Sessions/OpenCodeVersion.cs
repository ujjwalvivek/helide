using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Helide.Sessions;

/// <summary>
/// Which opencode a pane should run, and whether there is one Helide can run at all.
/// </summary>
/// <remarks>
/// Only OpenCode 2 creates a session under an id it is handed, which is what restoring a
/// conversation and marking a finished turn both rest on. Version 1 answers <c>-s</c>
/// with "Session not found" for any id it did not make itself, so a pane on it either
/// shows that error or has to give up everything the pane is for. It is not started at
/// all: the agent pane shows a notice in its place until opencode is updated.
/// <para>
/// A machine can have both. Version 2 is a different package from version 1, so
/// installing it does not remove the old one, and whichever sits earlier on PATH is what
/// the bare name <c>opencode</c> finds -- often still the old one, which left a pane
/// reporting version 1 on a machine that had just been updated. So every opencode on the
/// machine is asked what it is, and the first version 2 among them is the one used, by
/// its full path. The user is not asked to tidy their PATH for Helide's benefit.
/// </para>
/// <para>
/// Asking means running <c>opencode --version</c>, which through an npm shim is a node
/// start and takes a noticeable fraction of a second. So the answer is fetched once, off
/// the UI thread and ahead of need, and kept until something says it may have changed.
/// </para>
/// </remarks>
internal static class OpenCodeVersion
{
    private static readonly TimeSpan ProbeLimit = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan AnswerLimit = TimeSpan.FromSeconds(10);

    // Only forms that can be started directly, in the order a shell would prefer them.
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat"];

    /// <param name="Modern">Full path of an OpenCode 2, or null when there is none.</param>
    /// <param name="AnyFound">Whether any opencode was found at all.</param>
    private sealed record Answer(string? Modern, bool AnyFound);

    private static Task<Answer>? _answer;

    /// <summary>Starts the question early, so the answer is there when a pane wants it.</summary>
    public static void Prewarm() => _answer ??= Task.Run(Probe);

    /// <summary>
    /// True when the only opencode on the machine is older than version 2. False when
    /// there is a version 2, and also when nobody can tell: an opencode that will not
    /// say what it is gets the benefit of the doubt rather than a notice it may not
    /// deserve.
    /// </summary>
    public static bool IsLegacy() => IsLegacy(Current());

    /// <summary>
    /// What to launch: the full path of the OpenCode 2 that was found, or the bare name
    /// when none was identified and PATH is left to decide.
    /// </summary>
    public static string Executable() => Current()?.Modern ?? "opencode";

    /// <summary>The first question again, asked fresh and without blocking the caller.</summary>
    public static async Task<bool> IsLegacyAsync()
    {
        _answer = Task.Run(Probe);

        try
        {
            return IsLegacy(await _answer);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsLegacy(Answer? answer) => answer is { AnyFound: true, Modern: null };

    private static Answer? Current()
    {
        Prewarm();

        try
        {
            var answer = _answer!;
            return answer.Wait(AnswerLimit) ? answer.Result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Answer Probe()
    {
        var anyVersion = false;

        foreach (var candidate in Candidates())
        {
            if (MajorVersion(candidate) is not { } major)
                continue;

            anyVersion = true;
            if (major >= 2)
                return new Answer(candidate, true);
        }

        return new Answer(null, anyVersion);
    }

    // PATH first and in its own order, so a machine with one opencode behaves exactly as
    // a shell would. Then the places an installer puts it that a PATH taken when Helide
    // started may not include yet.
    private static IEnumerable<string> Candidates()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim().Trim('"'))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencode", "bin"));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            if (directory.Length == 0)
                continue;

            foreach (var extension in Extensions)
            {
                string file;
                try
                {
                    file = Path.GetFullPath(Path.Combine(directory, "opencode" + extension));
                    if (!File.Exists(file))
                        continue;
                }
                catch (Exception)
                {
                    // A malformed PATH entry is skipped, not fatal.
                    continue;
                }

                if (seen.Add(file) && !IsUnfinishedInstall(file))
                    yield return file;

                // One per directory: the forms beside it are the same install.
                break;
            }
        }
    }

    // The npm package ships a few-hundred-byte stand-in named opencode.exe and swaps the
    // real binary in from an install script. An npm that does not run install scripts
    // leaves the stand-in, and starting it makes Windows put up an "Unsupported 16-Bit
    // Application" box -- once for every time it is asked its version. The real one is
    // a couple of hundred megabytes, so size tells them apart without running anything.
    private static bool IsUnfinishedInstall(string file)
    {
        try
        {
            var binary = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? file
                : Path.Combine(
                    Path.GetDirectoryName(file)!,
                    "node_modules", "@opencode", "cli", "bin", "opencode.exe");

            return File.Exists(binary) && new FileInfo(binary).Length < 64 * 1024;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int? MajorVersion(string executable)
    {
        try
        {
            // Through cmd, which is what can start a .cmd shim. /s with the doubled
            // quotes is what lets a path with spaces in it survive cmd's own parsing.
            using var process = Process.Start(new ProcessStartInfo(
                "cmd.exe", $"/d /s /c \"\"{executable}\" --version\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit((int)ProbeLimit.TotalMilliseconds))
            {
                try { process.Kill(true); } catch { }
                return null;
            }

            // "1.19.0" from version 1, "opencode v2.0.26" from version 2.
            var match = Regex.Match(output.Result, @"(\d+)\.\d+");
            return match.Success ? int.Parse(match.Groups[1].Value) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
