using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Helide.Sessions;

/// <summary>
/// Talks to the OpenCode service that is already running on this machine.
/// </summary>
/// <remarks>
/// Helide runs the user's own opencode, against the user's own data directory, so its
/// sessions land in the same place as every other opencode on the machine and can be
/// opened from a terminal or from another client. The cost of sharing a store is that a
/// pane has to be told which conversation it is on, and that is what this type is for: the
/// service is asked to create the session up front, and the id it returns is the pane's.
/// Attribution is then exact, rather than inferred from whichever session happened to
/// appear first.
/// <para>
/// The connection details come from the service's own <c>service.json</c>, which is the
/// only supported way in. A server started by hand prints its URL but keeps its password
/// to itself, so Helide addresses the background service the TUI would attach to anyway.
/// </para>
/// </remarks>
internal sealed class OpenCodeService
{
    private readonly HttpClient _client;
    private readonly Uri _url;
    private readonly string? _password;

    private OpenCodeService(Uri url, string? password)
    {
        _url = url;
        _password = password;

        _client = new HttpClient
        {
            BaseAddress = url,
            // Session creation is a local call to a server already running on this
            // machine; it should never be slow, and a hung request would hold up a pane.
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>
    /// The running service, or null when there is none to talk to. A missing service is
    /// not an error: the TUI will start one, and the pane simply has no status until the
    /// next launch.
    /// </summary>
    public static OpenCodeService? Locate()
    {
        foreach (var path in StateFileCandidates())
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;

                if (!root.TryGetProperty("url", out var urlElement))
                    continue;

                var url = urlElement.GetString();
                if (string.IsNullOrWhiteSpace(url) ||
                    !Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
                    continue;

                string? password = null;
                if (root.TryGetProperty("password", out var passwordElement) &&
                    passwordElement.ValueKind == JsonValueKind.String)
                {
                    password = passwordElement.GetString();
                }

                return new OpenCodeService(endpoint, password);
            }
            catch (Exception)
            {
                // The file is written while the service starts, so it can be read
                // half-written. Treat that as no service rather than as a failure.
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a session and returns its id, or null when the service cannot be reached.
    /// </summary>
    /// <remarks>
    /// The session is created empty and before the TUI starts, so the pane is handed an id
    /// it already owns. Letting the TUI create its own would mean Helide having to work
    /// out afterwards which new session belonged to it, which is the guessing this exists
    /// to avoid.
    /// </remarks>
    public string? CreateSession()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };

            if (_password is { Length: > 0 } password)
            {
                // The server takes the fixed username "opencode" and its own password.
                var token = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"opencode:{password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
            }

            using var response = _client.SendAsync(request).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return null;

            using var document = JsonDocument.Parse(
                response.Content.ReadAsStringAsync().GetAwaiter().GetResult());

            if (!document.RootElement.TryGetProperty("data", out var data))
                return null;

            return data.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The database the service is using, which is the one every pane reads status from.
    /// </summary>
    public static string DatabasePath => Path.Combine(UserDataDirectory, "opencode", "opencode.db");

    /// <summary>
    /// The user's own OpenCode data directory, honouring XDG_DATA_HOME the same way the
    /// agent does.
    /// </summary>
    public static string UserDataDirectory
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share")
                : xdg;
        }
    }

    private static IEnumerable<string> StateFileCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            yield break;

        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var stateRoot = string.IsNullOrWhiteSpace(stateHome)
            ? Path.Combine(home, ".local", "state")
            : stateHome;

        yield return Path.Combine(stateRoot, "opencode", "service.json");
        yield return Path.Combine(home, ".opencode", "service.json");
    }
}