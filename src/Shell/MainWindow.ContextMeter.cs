using System.Windows;
using Helide.Sessions;
using Helide.Usage;

namespace Helide;

// The lip under the agent tab strip: how full the selected tab's context window is.
public partial class MainWindow
{
    private bool _contextMeterReading;

    // Hidden while the tab has no conversation or nothing on record says how full it is.
    private async void RefreshContextMeter()
    {
        if (_contextMeterReading)
            return;

        var session = _activeAgentSession;
        if (session is not { SessionId: { Length: > 0 } id })
        {
            ContextMeter.Visibility = Visibility.Collapsed;
            return;
        }

        _contextMeterReading = true;
        ContextFill? fill;
        try
        {
            var state = _stateStore.StateDirectory;
            fill = await Task.Run(() => ContextUse.Read(session.Type, id, state));
        }
        finally
        {
            _contextMeterReading = false;
        }

        // The tab changed while it was read: the next refresh is for that one.
        if (!ReferenceEquals(session, _activeAgentSession))
            return;

        if (fill is null)
        {
            ContextMeter.Visibility = Visibility.Collapsed;
            return;
        }

        var percent = fill.Percent;
        ContextMeterFilled.Width = new GridLength(percent, GridUnitType.Star);
        ContextMeterEmpty.Width = new GridLength(100 - percent, GridUnitType.Star);
        ContextMeterBar.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, SeverityBrushKey(percent, session.Type));

        var model = fill.Model is { Length: > 0 } named ? $" · {named}" : string.Empty;
        ContextMeter.ToolTip = string.Join("\n",
            $"Context · {session.TypeLabel}{model}",
            $"{UsageFormat.Tokens(fill.Used)} of {UsageFormat.Tokens(fill.Window)} tokens · {percent:0}% used",
            $"{UsageFormat.Tokens(Math.Max(0, fill.Window - fill.Used))} left",
            $"From {fill.Source}");
        ContextMeter.Visibility = Visibility.Visible;
    }
}
