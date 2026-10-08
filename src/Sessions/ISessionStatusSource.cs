namespace Helide.Sessions;

/// <summary>
/// Reports whether a tool behind a terminal pane is mid-turn, so a session that
/// finished while its tab was in the background can be marked.
/// </summary>
/// <remarks>
/// Both bundled agents can answer this properly, which is why this exists rather
/// than a single heuristic. OpenCode serves it over HTTP and Codex over JSON-RPC,
/// so the two implementations share nothing but this shape. A pane whose tool
/// cannot answer falls back to <see cref="QuietPeriodStatusSource"/>, which is a
/// guess, and the difference matters: a minute-long build and a finished reply look
/// identical to a timer.
/// </remarks>
internal interface ISessionStatusSource : IDisposable
{
    /// <summary>True while the agent is working. False when idle, or when unknown.</summary>
    bool IsBusy { get; }

    /// <summary>
    /// Fires when the agent becomes idle. Never fires from a constructor, and must
    /// not fire before <see cref="Start"/>.
    /// </summary>
    event Action? BecameIdle;

    /// <summary>
    /// Begins watching. Called once, after construction. Sources that cannot start
    /// report that through their own <c>Started</c> where it matters, so the caller
    /// can substitute a different implementation.
    /// </summary>
    void Start();
}