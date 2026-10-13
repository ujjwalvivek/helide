using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Helide.Services;

/// <summary>What one git invocation produced.</summary>
internal sealed record GitResult(bool Success, string Output, string Error)
{
    /// <summary>
    /// The text worth showing a person. When git failed its explanation is on stderr,
    /// and when it worked stderr is only progress, so the two cases read opposite streams.
    /// </summary>
    public string Message
    {
        get
        {
            var text = Success || string.IsNullOrWhiteSpace(Error) ? Output : Error;
            if (text.Length > 0)
                return text;

            return Success ? "Done." : "git failed without saying why.";
        }
    }
}

internal sealed record GitBranch(string Name, bool IsCurrent, bool IsRemote);

internal sealed record GitBranchListing(IReadOnlyList<GitBranch> Branches, string? Error);

/// <summary>Where the working tree stands against its upstream, for the strip's readout.</summary>
internal sealed record GitStatusSummary(bool HasUpstream, int Ahead, int Behind, int Changed);

/// <summary>
/// Runs git in a project directory without a window.
/// </summary>
/// <remarks>
/// The git pane is lazygit, and these are for what Helide does on its own: the origin
/// commands in the pane's strip, and the branch picker. They shell out to git rather than
/// typing into lazygit because lazygit's keys mean whatever its current panel says they
/// mean -- a <c>P</c> sent while a commit message was being written would end up in the
/// message.
/// </remarks>
internal static class GitCommands
{
    // Local work answers at once. Anything that talks to a remote is waiting on a network
    // and possibly on a credential helper, so it is given a good deal longer.
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromMinutes(3);

    public static async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool talksToRemote = false)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        // There is no terminal here to answer a username prompt, so one that git raised
        // would just sit until the timeout. With this it fails at once and says why.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        }
        catch (Win32Exception exception)
        {
            // 2 is ERROR_FILE_NOT_FOUND: nothing called git on PATH.
            return new GitResult(
                false,
                string.Empty,
                exception.NativeErrorCode == 2
                    ? "git was not found. Install Git for Windows and make sure it is on PATH."
                    : exception.Message);
        }

        using (process)
        {
            // Closed straight away. Nothing here has anything to say to git, and a prompt
            // that slips past the setting above then reads end-of-file instead of waiting.
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(talksToRemote ? RemoteTimeout : LocalTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // It exited between the timeout and the kill, which is the outcome wanted.
                }

                return new GitResult(false, string.Empty, "git did not finish in time and was stopped.");
            }

            return new GitResult(process.ExitCode == 0, (await output).Trim(), (await error).Trim());
        }
    }

    /// <summary>
    /// Local branches, the checked-out one first and the rest most recently used first,
    /// then the remote branches that have no local counterpart.
    /// </summary>
    public static async Task<GitBranchListing> ListBranchesAsync(string workingDirectory)
    {
        var result = await RunAsync(
            workingDirectory,
            ["for-each-ref", "--sort=-committerdate", "--format=%(refname)%09%(HEAD)", "refs/heads", "refs/remotes"]);

        if (!result.Success)
            return new GitBranchListing([], result.Message);

        const string headsPrefix = "refs/heads/";
        const string remotesPrefix = "refs/remotes/";

        var local = new List<GitBranch>();
        var remote = new List<GitBranch>();

        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            var reference = parts[0];

            if (reference.StartsWith(headsPrefix, StringComparison.Ordinal))
            {
                var isCurrent = parts.Length > 1 && parts[1].Trim() == "*";
                local.Add(new GitBranch(reference[headsPrefix.Length..], isCurrent, false));
            }
            else if (reference.StartsWith(remotesPrefix, StringComparison.Ordinal))
            {
                var name = reference[remotesPrefix.Length..];

                // origin/HEAD points at another branch; it is not one of its own.
                if (!name.EndsWith("/HEAD", StringComparison.Ordinal))
                    remote.Add(new GitBranch(name, false, true));
            }
        }

        // A remote branch that already has a local one is that same branch: listing both
        // would offer two rows that do the same thing.
        var localNames = local.Select(branch => branch.Name).ToHashSet(StringComparer.Ordinal);
        var remoteOnly = remote.Where(branch => !localNames.Contains(branch.Name[(branch.Name.IndexOf('/') + 1)..]));

        return new GitBranchListing(
            [.. local.OrderBy(branch => !branch.IsCurrent), .. remoteOnly],
            null);
    }

    /// <summary>The branch the current one tracks, or null when it tracks nothing.</summary>
    public static async Task<string?> UpstreamAsync(string workingDirectory)
    {
        var result = await RunAsync(
            workingDirectory,
            ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"]);

        return result.Success && result.Output.Length > 0 ? result.Output : null;
    }

    /// <summary>"origin" when there is one, otherwise the first remote, otherwise null.</summary>
    public static async Task<string?> PreferredRemoteAsync(string workingDirectory)
    {
        var result = await RunAsync(workingDirectory, ["remote"]);
        if (!result.Success)
            return null;

        var remotes = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return remotes.Contains("origin", StringComparer.Ordinal) ? "origin" : remotes.FirstOrDefault();
    }

    /// <summary>
    /// Ahead and behind counts and the number of changed paths, from one read-only call.
    /// Null when git could not say.
    /// </summary>
    public static async Task<GitStatusSummary?> StatusAsync(string workingDirectory)
    {
        // --no-optional-locks: this runs on a timer beside lazygit, and a status that
        // refreshes the index would take a lock lazygit's own commands then wait on.
        var result = await RunAsync(
            workingDirectory,
            ["--no-optional-locks", "status", "--porcelain=v2", "--branch"]);

        if (!result.Success)
            return null;

        var hasUpstream = false;
        var ahead = 0;
        var behind = 0;
        var changed = 0;

        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                // "# branch.ab +2 -1"
                var parts = line.Split(' ');
                if (parts.Length >= 4
                    && int.TryParse(parts[2], out var plus)
                    && int.TryParse(parts[3], out var minus))
                {
                    hasUpstream = true;
                    ahead = Math.Abs(plus);
                    behind = Math.Abs(minus);
                }
            }
            else if (!line.StartsWith('#'))
            {
                changed++;
            }
        }

        return new GitStatusSummary(hasUpstream, ahead, behind, changed);
    }

    /// <summary>
    /// The https address of the remote this branch pushes to, or null when the remote is
    /// not on the web (a local path, for one).
    /// </summary>
    public static async Task<string?> RemoteWebUrlAsync(string workingDirectory)
    {
        var upstream = await UpstreamAsync(workingDirectory);
        var slash = upstream?.IndexOf('/') ?? -1;
        var remote = slash > 0 ? upstream![..slash] : await PreferredRemoteAsync(workingDirectory);
        if (remote is null)
            return null;

        var result = await RunAsync(workingDirectory, ["remote", "get-url", remote]);
        return result.Success ? ToWebUrl(result.Output) : null;
    }

    private static string? ToWebUrl(string remote)
    {
        remote = remote.Trim();

        // A drive path or a UNC path is a remote on this machine's own disk.
        if (remote.Length == 0 || remote.StartsWith(@"\\", StringComparison.Ordinal)
            || Regex.IsMatch(remote, @"^[A-Za-z]:[\\/]"))
            return null;

        // git@host:owner/repo.git, which is not a URL and has no scheme.
        var scp = Regex.Match(remote, @"^(?:[^@/\s]+@)?([^:/\s]+):(?!//)(.+)$");
        if (scp.Success && !remote.Contains("://", StringComparison.Ordinal))
            return $"https://{scp.Groups[1].Value}/{TrimGit(scp.Groups[2].Value)}";

        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "ssh" or "git")
            return $"https://{uri.Host}{TrimGit(uri.AbsolutePath)}";

        return null;
    }

    private static string TrimGit(string path)
    {
        path = path.TrimEnd('/');
        return path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
    }
}
