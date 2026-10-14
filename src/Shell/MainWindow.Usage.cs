using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Helide.Sessions;
using Helide.Shell;
using Helide.Usage;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Helide;

// The usage drawer under the agent tabs, and the limit rings beside them.
public partial class MainWindow
{
    // How often the figures are redone while the drawer or the flyout is open. The ledger
    // only reads what was appended since the last pass, so a tick is cheap. A turn that
    // finishes refreshes them at once as well; this catches what has no such signal,
    // such as a turn still running.
    private static readonly TimeSpan UsageInterval = TimeSpan.FromSeconds(10);

    // While neither is open only the rings in the agent header are showing, and they move slowly.
    private static readonly TimeSpan IdleLimitsInterval = TimeSpan.FromMinutes(1);

    // A reading older than this is drawn faded in the agent header: still the last word the
    // agent had, but not to be leaned on. Claude only reads its limits when it starts, so
    // a few hours is its normal age; a day means nobody has started it since.
    private static readonly TimeSpan StaleReading = TimeSpan.FromDays(1);

    // The longest stretch one tick may add to the open-time count. Ticks are a minute
    // apart, so anything longer was the machine asleep, not the project open.
    private static readonly TimeSpan LongestOpenTick = TimeSpan.FromMinutes(2);

    private DispatcherTimer? _usageTimer;
    private int _usageGeneration;
    private string? _usageShownFor;
    private bool _usageScopeLoading;
    private IReadOnlyList<AgentLimits> _usageLimits = [];
    private OpenCodeUsageReader.Billing? _openCodeBilling;
    private DateTime _limitsReadUtc;
    private DateTime _openClockUtc = DateTime.UtcNow;

    private UsageLedger Ledger => UsageLedger.Shared(_stateStore.StateDirectory);

    private void InitializeUsage()
    {
        _usageScopeLoading = true;
        (_state.UsageScope switch
        {
            "session" => StatsScopeSession,
            "lifetime" => StatsScopeLifetime,
            _ => StatsScopeProject,
        }).IsChecked = true;
        _usageScopeLoading = false;

        // Right edges together, just under the header.
        UsageFlyout.CustomPopupPlacementCallback = (popup, target, _) =>
        [
            new System.Windows.Controls.Primitives.CustomPopupPlacement(
                new System.Windows.Point(target.Width - popup.Width, target.Height),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
        ];

        _usageTimer = new DispatcherTimer { Interval = UsageInterval };
        _usageTimer.Tick += (_, _) =>
        {
            AccrueOpenTime();

            if (UsageFlyout.IsOpen || DateTime.UtcNow - _limitsReadUtc >= IdleLimitsInterval)
                RefreshUsageLimits();

            RefreshUsageSummary();
            RefreshContextMeter();
            RefreshUntitledAgents();
        };
        _usageTimer.Start();
        Closed += (_, _) => _usageTimer.Stop();

        ApplyUsageDrawer();
        RefreshUsageLimits();
        WatchClaudeRelay();
    }

    private System.IO.FileSystemWatcher? _claudeRelayWatcher;

    // A relayed Claude pane writes its limits on every status update; the rings follow
    // straight away rather than at the next tick.
    private void WatchClaudeRelay()
    {
        try
        {
            var relay = ClaudeStatusRelay.RelayFile(_stateStore.StateDirectory);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(relay)!);

            _claudeRelayWatcher = new System.IO.FileSystemWatcher(
                System.IO.Path.GetDirectoryName(relay)!, System.IO.Path.GetFileName(relay))
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName,
            };

            // The context meter reads the same file.
            Action relayed = () =>
            {
                RefreshUsageLimits();
                RefreshContextMeter();
            };
            System.IO.FileSystemEventHandler changed = (_, _) =>
                Dispatcher.BeginInvoke(DispatcherPriority.Background, relayed);
            _claudeRelayWatcher.Changed += changed;
            _claudeRelayWatcher.Created += changed;
            _claudeRelayWatcher.Renamed += (_, _) =>
                Dispatcher.BeginInvoke(DispatcherPriority.Background, relayed);
            _claudeRelayWatcher.EnableRaisingEvents = true;

            Closed += (_, _) => _claudeRelayWatcher?.Dispose();
        }
        catch (Exception)
        {
            // Without it the rings still catch up on the timer.
        }
    }

    private UsageScope CurrentUsageScope() =>
        StatsScopeSession.IsChecked == true ? UsageScope.Session
        : StatsScopeLifetime.IsChecked == true ? UsageScope.Lifetime
        : UsageScope.Project;

    private static string AgentBrushKey(string agent) => agent switch
    {
        AgentProviders.Claude => "AgentClaudeBrush",
        AgentProviders.Codex => "AgentCodexBrush",
        _ => "AgentOpenCodeBrush",
    };

    // ---- open time ----------------------------------------------------------------

    // Called before every workspace write and on every tick, so the count is never more
    // than a minute behind what is on disk.
    private void AccrueOpenTime()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _openClockUtc;
        _openClockUtc = now;

        if (_currentProject is null || !_ownsProject || elapsed <= TimeSpan.Zero)
            return;

        _workspace.OpenSeconds += Math.Min(elapsed.TotalSeconds, LongestOpenTick.TotalSeconds);
    }

    // A project just loaded starts its clock now, not when the last one did.
    private void StartOpenClock() => _openClockUtc = DateTime.UtcNow;

    // ---- the drawer ---------------------------------------------------------------

    private void AgentStatsButton_Click(object sender, RoutedEventArgs e)
    {
        _state.UsageDrawerOpen = !_state.UsageDrawerOpen;
        _stateStore.Save(_state);
        ApplyUsageDrawer();
    }

    private void ApplyUsageDrawer()
    {
        var open = _state.UsageDrawerOpen;
        AgentStatsDrawer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AgentStatsButton.ToolTip = open ? "Hide usage" : "Show usage";

        if (open)
            AgentStatsButton.SetResourceReference(ForegroundProperty, "MauveBrush");
        else
            AgentStatsButton.ClearValue(ForegroundProperty);

        if (open)
            RefreshUsageSummary();
    }

    private void StatsScope_Checked(object sender, RoutedEventArgs e)
    {
        if (_usageScopeLoading)
            return;

        _state.UsageScope = CurrentUsageScope() switch
        {
            UsageScope.Session => "session",
            UsageScope.Lifetime => "lifetime",
            _ => "project",
        };
        _stateStore.Save(_state);
        RefreshUsageSummary();
    }

    // Four across while they fit, two by two in a narrow pane.
    private void AgentStatsTiles_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var columns = e.NewSize.Width < 320 ? 2 : 4;
        if (AgentStatsTiles.Columns != columns)
            AgentStatsTiles.Columns = columns;
    }

    // The session scope follows the selected tab.
    private void OnAgentTabsChanged()
    {
        if (CurrentUsageScope() == UsageScope.Session)
            RefreshUsageSummary();

        UpdateAgentEditsStrip();
        UpdateEditorFileActions();
        RefreshContextMeter();
    }

    private async void RefreshUsageSummary()
    {
        if (AgentStatsDrawer.Visibility != Visibility.Visible || _currentProject is not { } project)
            return;

        var scope = CurrentUsageScope();
        var session = _activeAgentSession;
        var key = scope + "|" + project + "|" + session?.Type + "|" + session?.SessionId;
        var generation = ++_usageGeneration;

        UpdateOpenTime(scope);

        if (scope == UsageScope.Session && string.IsNullOrEmpty(session?.SessionId))
        {
            ShowUsageSummary(null);
            AgentStatsStatus.Text = session is null ? "no tab open" : "nothing said yet";
            _usageShownFor = key;
            return;
        }

        // A different scope gets a visible "reading"; the same one refreshing does not,
        // or the drawer would flicker every minute.
        if (key != _usageShownFor)
        {
            ShowUsageSummary(null);
            AgentStatsStatus.Text = "reading…";
        }

        UsageSummary summary;
        try
        {
            summary = await Ledger.SummarizeAsync(scope, project, session?.Type, session?.SessionId);
        }
        catch (Exception)
        {
            if (generation == _usageGeneration)
                AgentStatsStatus.Text = "couldn't read";
            return;
        }

        if (generation != _usageGeneration)
            return;

        _usageShownFor = key;
        AgentStatsStatus.Text = summary.Agents.Count == 0 ? "no usage on record" : string.Empty;
        ShowUsageSummary(summary);
    }

    private void UpdateOpenTime(UsageScope scope)
    {
        // Open time belongs to a project; across every project it would mean nothing.
        AgentStatsOpenTime.Visibility = scope == UsageScope.Lifetime ? Visibility.Collapsed : Visibility.Visible;
        AgentStatsOpenTimeText.Text = "open " + UsageFormat.Duration(TimeSpan.FromSeconds(_workspace.OpenSeconds));
    }

    private void ShowUsageSummary(UsageSummary? summary)
    {
        AgentStatsSplit.Children.Clear();
        AgentStatsSplit.ColumnDefinitions.Clear();
        AgentStatsLegend.Children.Clear();

        if (summary is null || summary.Agents.Count == 0)
        {
            StatTokens.Text = StatPrompts.Text = StatTime.Text = StatCost.Text = "–";
            StatTokensTile.ToolTip = null;
            StatCostTile.ToolTip = null;
            return;
        }

        StatTokens.Text = UsageFormat.Tokens(summary.Tokens);
        StatPrompts.Text = UsageFormat.Count(summary.Prompts);
        StatTime.Text = UsageFormat.Duration(summary.AgentTime);

        // "+": at least this much, since some of it could not be priced.
        StatCost.Text = summary.Cost == 0 && summary.UnpricedTokens > 0
            ? "unpriced"
            : UsageFormat.Cost(summary.Cost) + (summary.UnpricedTokens > 0 ? "+" : string.Empty);

        StatTokensTile.ToolTip = TokenBreakdown(summary);
        StatCostTile.ToolTip = CostExplanation(summary);

        var total = Math.Max(1, summary.Tokens);
        // An agent with prompts but no tokens -- every turn refused, say -- has no share
        // of the bar to show.
        foreach (var agent in summary.Agents.Where(agent => agent.Tokens > 0).OrderByDescending(agent => agent.Tokens))
        {
            var share = (double)agent.Tokens / total;

            AgentStatsSplit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 0.0001), GridUnitType.Star) });
            var segment = new Rectangle();
            segment.SetResourceReference(Shape.FillProperty, AgentBrushKey(agent.Agent));
            Grid.SetColumn(segment, AgentStatsSplit.ColumnDefinitions.Count - 1);
            AgentStatsSplit.Children.Add(segment);

            AgentStatsLegend.Children.Add(LegendEntry(agent, share));
        }
    }

    private FrameworkElement LegendEntry(AgentUsage agent, double share)
    {
        var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, AgentBrushKey(agent.Agent));

        var label = SmallText($"{AgentProviders.LabelFor(agent.Agent).ToLowerInvariant()} {share:P0}", "TextSubtleBrush");

        var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
        entry.Children.Add(dot);
        entry.Children.Add(label);
        entry.ToolTip =
            $"{AgentProviders.LabelFor(agent.Agent)}: {UsageFormat.Tokens(agent.Tokens)} tokens, "
            + $"{UsageFormat.Count(agent.Prompts)} prompts, {UsageFormat.Duration(agent.AgentTime)}";
        return entry;
    }

    private static string TokenBreakdown(UsageSummary summary)
    {
        var all = new TokenCounts();
        foreach (var counts in summary.Agents.SelectMany(agent => agent.Models.Values))
            all.Add(counts);

        var lines = new List<string>
        {
            $"input {UsageFormat.Tokens(all.Input)} · output {UsageFormat.Tokens(all.Output)}",
            $"cache read {UsageFormat.Tokens(all.CacheRead)} · cache write {UsageFormat.Tokens(all.CacheWrite)}",
            string.Empty,
        };

        var models = summary.Agents
            .SelectMany(agent => agent.Models)
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Model: group.Key, Tokens: group.Sum(pair => pair.Value.Total)))
            .OrderByDescending(model => model.Tokens)
            .ToList();

        foreach (var (model, tokens) in models.Take(6))
            lines.Add($"{model}  {UsageFormat.Tokens(tokens)}");

        if (models.Count > 6)
            lines.Add($"and {models.Count - 6} more");

        return string.Join("\n", lines);
    }

    private static string CostExplanation(UsageSummary summary)
    {
        var lines = new List<string>
        {
            "What these tokens would cost at API list prices.",
            "Usage covered by a subscription or a free model is priced too,",
            "so this is not what you were charged.",
        };

        if (summary.UnpricedTokens > 0)
        {
            lines.Add(string.Empty);
            lines.Add(!summary.HasPriceCatalog
                ? "Prices come from OpenCode's model catalogue, which isn't on this machine."
                : $"{UsageFormat.Tokens(summary.UnpricedTokens)} tokens are on models with no known price and are left out.");
        }

        return string.Join("\n", lines);
    }

    // ---- the limit rings in the agent header, and their flyout -------------------

    private async void RefreshUsageLimits()
    {
        if (!_state.AiEnabled)
        {
            UsagePill.Visibility = Visibility.Collapsed;
            UsageFlyout.IsOpen = false;
            return;
        }

        try
        {
            _limitsReadUtc = DateTime.UtcNow;
            var (limits, billing) = await Task.Run(() =>
            {
                var read = UsageLimits.Read(_stateStore.StateDirectory);
                var spent = AgentProviders.IsInstalled(AgentProviders.OpenCode)
                    ? OpenCodeUsageReader.ReadBilling()
                    : null;
                return (read, spent);
            });

            _usageLimits = limits;
            _openCodeBilling = billing;
        }
        catch (Exception)
        {
            // Keep the last readings; they carry their age.
        }

        RenderUsagePill();
        if (UsageFlyout.IsOpen)
            RenderUsageFlyout();
    }

    private void RenderUsagePill()
    {
        UsagePillRings.Children.Clear();

        if (!_state.AiEnabled || _usageLimits.Count == 0)
        {
            UsagePill.Visibility = Visibility.Collapsed;
            return;
        }

        var now = DateTimeOffset.Now;
        var tips = new List<string>();

        foreach (var limits in _usageLimits)
        {
            var percent = limits.TightestPercent(now);
            // The size and weight of the glyphs beside it. A ring is narrower than a glyph,
            // so its cell is too, or the group reads as gaps between dots.
            var ring = new UsageRing
            {
                Width = 11,
                Height = 11,
                Stroke = 1.5,
                Percent = percent,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = now - limits.ReadAt > StaleReading ? 0.5 : 1,
            };
            ring.SetResourceReference(UsageRing.RingBrushProperty, SeverityBrushKey(percent, limits.Agent));
            if (UnseenAlert(limits.Agent, percent))
                Pulse(ring);
            ring.SetResourceReference(UsageRing.TrackBrushProperty, "PressedBrush");
            UsagePillRings.Children.Add(new Grid { Width = 18, Children = { ring } });

            tips.Add($"{AgentProviders.LabelFor(limits.Agent)} {percent:0}%");
        }

        UsagePill.ToolTip = "Usage limits: " + string.Join(" · ", tips);
        UsagePill.Visibility = Visibility.Visible;
    }

    private static int AlertLevel(double percent) => percent >= 95 ? 95 : percent >= 80 ? 80 : 0;

    // Whether the agent has crossed a warning level since its ring was last clicked. A
    // level that has dropped -- the window reset -- is forgotten, so crossing it again
    // pulses again.
    private bool UnseenAlert(string agent, double percent)
    {
        var level = AlertLevel(percent);
        var seen = _state.UsageAlertsSeen.GetValueOrDefault(agent);

        if (level < seen)
        {
            _state.UsageAlertsSeen[agent] = level;
            _stateStore.Save(_state);
        }

        return level > seen;
    }

    // Clicking the rings is having seen them.
    private void AcknowledgeUsageAlerts()
    {
        var now = DateTimeOffset.Now;
        var changed = false;
        foreach (var limits in _usageLimits)
        {
            var level = AlertLevel(limits.TightestPercent(now));
            if (_state.UsageAlertsSeen.GetValueOrDefault(limits.Agent) != level)
            {
                _state.UsageAlertsSeen[limits.Agent] = level;
                changed = true;
            }
        }

        if (!changed)
            return;

        _stateStore.Save(_state);
        RenderUsagePill();
    }

    // A dip and back, every two seconds, until the warning has been seen.
    private static void Pulse(UIElement ring)
    {
        var pulse = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(2),
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };
        pulse.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pulse.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.2, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350))));
        pulse.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));
        pulse.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2))));
        ring.BeginAnimation(UIElement.OpacityProperty, pulse);
    }

    // An agent's own colour while there is room, then the warning colours.
    private static string SeverityBrushKey(double percent, string agent) => percent switch
    {
        >= 95 => "DangerBrush",
        >= 80 => "WarningBrush",
        _ => AgentBrushKey(agent),
    };

    private void UsagePill_Click(object sender, RoutedEventArgs e)
    {
        if (UsageFlyout.IsOpen)
        {
            UsageFlyout.IsOpen = false;
            return;
        }

        RenderUsageFlyout();
        UsageFlyout.IsOpen = true;
        AcknowledgeUsageAlerts();
        RefreshUsageLimits();
    }

    private void RenderUsageFlyout()
    {
        UsageFlyoutRows.Children.Clear();
        var now = DateTimeOffset.Now;

        foreach (var limits in _usageLimits)
        {
            var section = FlyoutSection(limits.Agent, UsageFormat.Age(limits.ReadAt, now));

            foreach (var window in limits.Windows)
                section.Children.Add(FlyoutWindow(limits.Agent, window, now));

            UsageFlyoutRows.Children.Add(section);
        }

        // OpenCode keeps no limit on disk. What it does keep is what paid providers
        // charged, which is only worth showing if something ever was.
        if (_openCodeBilling is { } billing)
        {
            var section = FlyoutSection(
                AgentProviders.OpenCode,
                billing.EverBilled ? "billed usage" : "no billable account found");

            if (billing.EverBilled)
            {
                section.Children.Add(BillingRow("", "today", billing.Today));
                section.Children.Add(BillingRow("", "this month", billing.Month));
            }

            UsageFlyoutRows.Children.Add(section);
        }
    }

    private static FrameworkElement BillingRow(string glyph, string label, decimal amount)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        var value = SmallText("$" + amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), "TextBrush");
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);

        var name = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new GlyphIcon
        {
            Glyph = glyph,
            GlyphSize = 9,
            Weight = 0.4,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(GlyphIcon.ForegroundProperty, "TextSubtleBrush");
        name.Children.Add(icon);
        name.Children.Add(SmallText(label, "TextSubtleBrush"));
        row.Children.Add(name);

        return row;
    }

    // An agent's heading -- its colour, its name, and how old the reading is -- with a
    // rule above every heading but the first.
    private StackPanel FlyoutSection(string agent, string note)
    {
        var first = UsageFlyoutRows.Children.Count == 0;
        var section = new StackPanel { Margin = new Thickness(0, first ? 0 : 10, 0, 0) };

        if (!first)
        {
            var rule = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 10) };
            rule.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
            section.Children.Add(rule);
        }

        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };

        var age = SmallText(note, "TextSubtleBrush");
        DockPanel.SetDock(age, Dock.Right);
        heading.Children.Add(age);

        var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, AgentBrushKey(agent));
        heading.Children.Add(dot);

        var name = SmallText(AgentProviders.LabelFor(agent), "TextBrush");
        name.FontWeight = FontWeights.SemiBold;
        heading.Children.Add(name);

        section.Children.Add(heading);
        return section;
    }

    private FrameworkElement FlyoutWindow(string agent, LimitWindow window, DateTimeOffset now)
    {
        var reset = window.HasReset(now);
        var percent = window.EffectivePercent(now);

        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var top = new DockPanel();
        var value = SmallText(reset ? "reset" : $"{window.Percent:0}%", percent >= 80 ? SeverityBrushKey(percent, agent) : "TextBrush");
        DockPanel.SetDock(value, Dock.Right);
        top.Children.Add(value);

        var label = new StackPanel { Orientation = Orientation.Horizontal };
        var glyph = new GlyphIcon
        {
            // A clock for a window measured in hours, a calendar for days and longer.
            Glyph = window.Label.Contains("hour") || window.Label.Contains("min") ? "" : "",
            GlyphSize = 9,
            Weight = 0.4,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(GlyphIcon.ForegroundProperty, "TextSubtleBrush");
        label.Children.Add(glyph);
        label.Children.Add(SmallText(window.Label, "TextSubtleBrush"));
        top.Children.Add(label);
        row.Children.Add(top);

        var bar = new Grid { Height = 4, Margin = new Thickness(0, 5, 0, 0) };
        bar.SetResourceReference(Panel.BackgroundProperty, "InteractiveBrush");
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(percent, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - percent, GridUnitType.Star) });
        if (percent > 0)
        {
            var fill = new Border();
            fill.SetResourceReference(Border.BackgroundProperty, SeverityBrushKey(percent, agent));
            bar.Children.Add(fill);
        }
        row.Children.Add(bar);

        var caption = reset
            ? "reset since this was read"
            : window.ResetsAt is { } at
                ? "resets in " + UsageFormat.Duration(at - now)
                : null;

        if (caption is not null)
        {
            var text = SmallText(caption, "TextFaintBrush");
            text.Margin = new Thickness(0, 3, 0, 0);
            row.Children.Add(text);
        }

        // Claude's figures only come back from a Claude that is running: its status line,
        // or the copy it saves when it starts.
        if (reset && agent == AgentProviders.Claude)
        {
            var hint = SmallText("Start a Claude session to refresh usage.", "TextSubtleBrush");
            hint.Margin = new Thickness(0, 3, 0, 0);
            hint.TextWrapping = TextWrapping.Wrap;
            row.Children.Add(hint);
        }

        return row;
    }

    private static TextBlock SmallText(string text, string brushKey)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "UiMonoFontFamily");
        block.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeSmall");
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }
}
