namespace Helide.Usage;

/// <summary>
/// Token counts for one model, split the way every price list splits them.
/// </summary>
/// <remarks>
/// The three agents count differently -- Codex folds cached input into its input figure,
/// Claude and OpenCode keep it apart, OpenCode keeps reasoning apart from output -- so each
/// reader converts to this shape on the way in. <see cref="Input"/> is input that was not
/// read from cache, and <see cref="Output"/> includes reasoning, since reasoning is billed
/// as output everywhere.
/// </remarks>
internal sealed class TokenCounts
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }

    // What the agent itself recorded as spent, for tokens it billed. Only OpenCode stores
    // this, and only for paid providers; it is used in place of a computed figure, never
    // added to one.
    public decimal StoredCost { get; set; }

    // Tokens that StoredCost already accounts for, so they are not priced a second time.
    public long StoredCostTokens { get; set; }

    public long Total => Input + Output + CacheRead + CacheWrite;

    public void Add(TokenCounts other)
    {
        Input += other.Input;
        Output += other.Output;
        CacheRead += other.CacheRead;
        CacheWrite += other.CacheWrite;
        StoredCost += other.StoredCost;
        StoredCostTokens += other.StoredCostTokens;
    }

    public TokenCounts Clone() => new()
    {
        Input = Input,
        Output = Output,
        CacheRead = CacheRead,
        CacheWrite = CacheWrite,
        StoredCost = StoredCost,
        StoredCostTokens = StoredCostTokens,
    };
}

/// <summary>Which conversations a figure covers.</summary>
internal enum UsageScope
{
    Session,
    Project,
    Lifetime,
}

/// <summary>What one agent did within a scope.</summary>
internal sealed class AgentUsage
{
    public required string Agent { get; init; }

    // Keyed by "provider/model" for OpenCode and by the bare model id for the other two,
    // which only ever talk to their own provider.
    public Dictionary<string, TokenCounts> Models { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Prompts { get; set; }
    public TimeSpan AgentTime { get; set; }

    public long Tokens => Models.Values.Sum(counts => counts.Total);

    public void Add(string model, TokenCounts counts)
    {
        if (!Models.TryGetValue(model, out var existing))
            Models[model] = counts.Clone();
        else
            existing.Add(counts);
    }
}

/// <summary>Everything the stats drawer shows for one scope.</summary>
internal sealed class UsageSummary
{
    public required UsageScope Scope { get; init; }
    public List<AgentUsage> Agents { get; } = [];

    public long Tokens => Agents.Sum(agent => agent.Tokens);
    public int Prompts => Agents.Sum(agent => agent.Prompts);
    public TimeSpan AgentTime => TimeSpan.FromTicks(Agents.Sum(agent => agent.AgentTime.Ticks));

    // What the tokens would have cost at API list prices. Tokens on a model with no known
    // price are left out of it and counted in UnpricedTokens instead, so a figure is never
    // made up as zero.
    public decimal Cost { get; set; }
    public long UnpricedTokens { get; set; }

    // Whether there was a price list to go by at all.
    public bool HasPriceCatalog { get; set; }
}

/// <summary>One usage-limit window as an agent last reported it.</summary>
/// <param name="Label">A short name for the window: "5 hour", "week".</param>
/// <param name="Percent">How much of it was used at the time of the reading, 0 to 100.</param>
/// <param name="ResetsAt">When the window rolls over, if the agent said.</param>
internal sealed record LimitWindow(string Label, double Percent, DateTimeOffset? ResetsAt)
{
    /// <summary>Whether the window has rolled over since it was read.</summary>
    public bool HasReset(DateTimeOffset now) => ResetsAt is { } at && at <= now;

    /// <summary>
    /// The share to show now: the reading, or nothing if the window has reset since. A
    /// reset window is known to be empty, which is more than the old number says.
    /// </summary>
    public double EffectivePercent(DateTimeOffset now) => HasReset(now) ? 0 : Percent;
}

/// <summary>The limits one agent last reported, and when it reported them.</summary>
internal sealed record AgentLimits(string Agent, DateTimeOffset ReadAt, IReadOnlyList<LimitWindow> Windows)
{
    public double TightestPercent(DateTimeOffset now) =>
        Windows.Count == 0 ? 0 : Windows.Max(window => window.EffectivePercent(now));
}
