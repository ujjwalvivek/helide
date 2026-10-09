using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Helide.Shell;

/// <summary>
/// One directory the folder picker can offer: where it is on disk, and what to
/// search and show it by.
/// </summary>
internal sealed class FolderEntry
{
    public FolderEntry(string fullPath, string relativePath, int depth, string? matchText = null)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        Depth = depth;
        MatchText = matchText ?? relativePath;
        Name = relativePath == "." ? "." : relativePath[(relativePath.LastIndexOf('\\') + 1)..];
    }

    public string FullPath { get; }

    /// <summary>Path from the project root, backslashes, "." for the root itself.</summary>
    public string RelativePath { get; }

    /// <summary>
    /// The folder's own name, last segment of the relative path. This is what a row
    /// shows: the indent already carries the depth, and spelling the whole path out
    /// again on every line restates it and reads as a broken tree.
    /// </summary>
    public string Name { get; }

    /// <summary>Nesting depth, 0 for the project root. Drives the row indent.</summary>
    public int Depth { get; }

    /// <summary>
    /// What the filter matches on. The relative path for everything under the root,
    /// and the project's own name for the root, whose relative path "." would match
    /// nothing the user types.
    /// </summary>
    public string MatchText { get; }
}

/// <summary>
/// Walks a project tree and reports its directories in batches.
/// </summary>
/// <remarks>
/// Run on a caller-provided thread and reported in batches because a tree worth
/// skipping takes long enough to matter: holding every directory until the end
/// leaves the picker blank while it works, while batches make it usable at once
/// and fill in behind the filter.
/// </remarks>
internal static class FolderScan
{
    private const int BatchSize = 128;

    // Build output, dependency trees and VCS metadata. A .NET tree spends most of
    // its directories here and none of them are somewhere a session should be
    // rooted: without this list the picker drowns in paths nobody asked for.
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn",
        "bin", "obj", "node_modules",
        ".vs", ".idea", ".vscode",
        "dist", "target",
    };

    public static void Walk(string root, Action<IReadOnlyList<FolderEntry>> batch, CancellationToken token)
    {
        var buffer = new List<FolderEntry>(BatchSize);

        void Flush()
        {
            if (buffer.Count == 0)
                return;

            // A snapshot, not the live list: the consumer is reached across a
            // thread boundary and may not have drained the previous one yet.
            batch(buffer.ToArray());
            buffer.Clear();
        }

        void Recurse(string directory, string prefix, int depth)
        {
            token.ThrowIfCancellationRequested();

            foreach (var child in Children(directory))
            {
                if (Ignored.Contains(child.Name))
                    continue;

                var relative = prefix.Length == 0 ? child.Name : prefix + "\\" + child.Name;
                buffer.Add(new FolderEntry(child.FullName, relative, depth));

                if (buffer.Count >= BatchSize)
                    Flush();

                // Links are not followed. A junction out of the tree, or back into
                // a part already walked, turns a bounded scan into an unbounded one.
                if (!child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Recurse(child.FullName, relative, depth + 1);
            }
        }

        Recurse(root, string.Empty, 1);
        Flush();
    }

    private static IReadOnlyList<DirectoryInfo> Children(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            return info.Exists ? info.GetDirectories() : Array.Empty<DirectoryInfo>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // One unreadable directory costs its subtree, which the picker can live
            // without. Letting it out instead would abort the whole scan.
            return Array.Empty<DirectoryInfo>();
        }
    }
}
