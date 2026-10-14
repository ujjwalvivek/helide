using System.IO;
using System.Text.Json;
using Helide.Sessions;

namespace Helide.Usage;

/// <summary>
/// Gets Claude's live usage limits by being its status line.
/// </summary>
/// <remarks>
/// Claude Code keeps its limits current from the headers of every API response, but the
/// copy it writes to disk (<c>cachedUsageUtilization</c>) is refreshed only now and then;
/// hours can pass between refreshes while Claude is in use. What Claude does hand out on
/// every update is the JSON its status-line command receives, which carries
/// <c>rate_limits</c> straight from that live state.
/// <para>
/// So the Claude panes Helide starts are given one extra setting: a status line whose
/// command copies that JSON into Helide's state folder and prints nothing, so no line
/// appears in the pane. It goes through Claude's own <c>--settings</c> flag and touches
/// none of the user's files. Someone who has a status line of their own keeps it: the
/// relay is left out, and the figures fall back to the cached copy.
/// </para>
/// <para>
/// The command has to survive whichever shell Claude runs it in -- Git Bash, cmd or
/// PowerShell -- so it is one bare <c>powershell.exe</c> call with no <c>$</c> in it,
/// which none of the three will rewrite.
/// </para>
/// </remarks>
internal static class ClaudeStatusRelay
{
    private const string SettingsName = "claude-status-settings.json";
    private const string RelayName = "claude-status.json";

    public static string RelayFile(string stateDirectory) => Path.Combine(stateDirectory, RelayName);

    /// <summary>
    /// The settings file to start a Claude pane with, or null when the pane should be
    /// left as it is because a status line is already configured for it.
    /// </summary>
    public static string? SettingsFor(string stateDirectory, string paneDirectory)
    {
        if (HasOwnStatusLine(paneDirectory))
            return null;

        try
        {
            Directory.CreateDirectory(stateDirectory);

            var relay = RelayFile(stateDirectory);
            var staging = relay + ".tmp";
            var command =
                "powershell.exe -NoProfile -NonInteractive -Command \""
                + $"[IO.File]::WriteAllText({PowerShellText(staging)}, [Console]::In.ReadToEnd()); "
                + $"Move-Item -Force -LiteralPath {PowerShellText(staging)} -Destination {PowerShellText(relay)}\"";

            var settings = JsonSerializer.Serialize(new
            {
                statusLine = new { type = "command", command },
            }, new JsonSerializerOptions { WriteIndented = true });

            var path = Path.Combine(stateDirectory, SettingsName);
            if (!File.Exists(path) || File.ReadAllText(path) != settings)
                File.WriteAllText(path, settings);

            return path;
        }
        catch (Exception)
        {
            // No relay only means the cached figures are used.
            return null;
        }
    }

    // A single-quoted PowerShell string; a quote inside one is written twice.
    private static string PowerShellText(string text) => "'" + text.Replace("'", "''") + "'";

    // Every place a status line can come from, short of a --settings flag of the user's
    // own. Any of them having one means it is theirs, and it is not replaced.
    private static bool HasOwnStatusLine(string paneDirectory)
    {
        var candidates = new List<string>
        {
            Path.Combine(ClaudeSessions.ConfigDirectory, "settings.json"),
            Path.Combine(ClaudeSessions.ConfigDirectory, "settings.local.json"),
            Path.Combine(paneDirectory, ".claude", "settings.json"),
            Path.Combine(paneDirectory, ".claude", "settings.local.json"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "ClaudeCode", "managed-settings.json"),
        };

        foreach (var file in candidates)
        {
            try
            {
                if (!File.Exists(file))
                    continue;

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("statusLine", out var line)
                    && line.ValueKind == JsonValueKind.Object)
                    return true;
            }
            catch (Exception)
            {
                // A settings file that cannot be read is one Claude cannot read either.
            }
        }

        return false;
    }

    /// <summary>The limits from the last status update any relayed Claude pane made.</summary>
    public static AgentLimits? Read(string stateDirectory)
    {
        var file = RelayFile(stateDirectory);

        try
        {
            if (!File.Exists(file))
                return null;

            var readAt = new DateTimeOffset(File.GetLastWriteTimeUtc(file));

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);

            return document.RootElement.TryGetProperty("rate_limits", out var limits)
                ? Parse(limits, readAt)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static AgentLimits? Parse(JsonElement limits, DateTimeOffset readAt)
    {
        if (limits.ValueKind != JsonValueKind.Object)
            return null;

        var windows = new List<LimitWindow>();

        foreach (var (key, label) in new[] { ("five_hour", "5 hour"), ("seven_day", "week") })
        {
            if (!limits.TryGetProperty(key, out var window)
                || window.ValueKind != JsonValueKind.Object
                || !window.TryGetProperty("used_percentage", out var percent)
                || percent.ValueKind != JsonValueKind.Number)
                continue;

            windows.Add(new LimitWindow(label, Math.Clamp(percent.GetDouble(), 0, 100), ResetsAt(window)));
        }

        return windows.Count == 0 ? null : new AgentLimits(AgentProviders.Claude, readAt, windows);
    }

    // Seconds since the epoch from the headers, or a timestamp; either is accepted.
    private static DateTimeOffset? ResetsAt(JsonElement window)
    {
        if (!window.TryGetProperty("resets_at", out var at))
            return null;

        if (at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var seconds))
            return seconds > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(seconds) : DateTimeOffset.FromUnixTimeSeconds(seconds);

        return at.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(at.GetString(), out var parsed)
            ? parsed
            : null;
    }
}
