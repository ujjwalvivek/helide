using System.IO;
using System.Net.Http;
using System.Windows.Threading;

namespace Helide.Sessions;

/// <summary>
/// Reads idle notifications from the HTTP server that the OpenCode TUI runs against
/// itself, on the port Helide pinned with --port.
/// </summary>
internal sealed class OpenCodeStatusSource : ISessionStatusSource
{
    private readonly int _port;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dispatcher _dispatcher;

    public OpenCodeStatusSource(int port, Dispatcher dispatcher)
    {
        _port = port;
        _dispatcher = dispatcher;
    }

    public bool IsBusy { get; private set; }

    // The conversation this pane is actually on, learned from its own event stream.
    // Without it session capture could only guess the freshest thread.
    public string? SessionId { get; private set; }

    public event Action? BecameIdle;

    public void Start() => _ = Task.Run(ListenAsync);

    private static string? ExtractSessionId(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal))
            return null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(line[5..]);
            return FindSessionId(document.RootElement);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? FindSessionId(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if ((property.Name is "sessionID" or "sessionId" or "session_id") &&
                    property.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
                    property.Value.GetString() is { Length: > 0 } value)
                    return value;

                var nested = FindSessionId(property.Value);
                if (nested is not null)
                    return nested;
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindSessionId(item);
                if (nested is not null)
                    return nested;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task ListenAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await client.GetAsync(
                $"http://127.0.0.1:{_port}/event",
                HttpCompletionOption.ResponseHeadersRead,
                _stop.Token);

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(_stop.Token);
            using var reader = new StreamReader(stream);

            while (!_stop.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_stop.Token);
                if (line is null)
                    return;

                var sessionId = ExtractSessionId(line);
                if (sessionId is not null)
                    SessionId = sessionId;

                // Any event means the agent did something.
                IsBusy = true;

                if (!line.Contains("session.idle", StringComparison.Ordinal))
                    continue;

                _ = _dispatcher.BeginInvoke(new Action(() =>
                {
                    IsBusy = false;
                    BecameIdle?.Invoke();
                }));
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (Exception)
        {
            // Server not reachable, moved, or not OpenCode; the pane keeps working.
        }
    }
}