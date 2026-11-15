using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Muesli.Windows.Services;

/// <summary>The signed-in ChatGPT session used for meeting notes.</summary>
public sealed record ChatGptCredential(string AccessToken, string AccountId);

public sealed class ChatGptAuthException(string message) : Exception(message);

/// <summary>
/// "Sign in with ChatGPT" for meeting notes, a port of the macOS <c>ChatGPTAuthManager</c>: an
/// OAuth authorization-code flow with PKCE in the user's browser, a one-shot loopback callback on
/// port 1455, and tokens kept in the Windows credential store. Nothing is written to settings or
/// logs, and the token is refreshed shortly before it expires.
/// </summary>
public sealed class ChatGptAuthManager
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    public const string TokenUrl = "https://auth.openai.com/oauth/token";
    public const string RedirectUri = "http://localhost:1455/auth/callback";
    public const string Scopes = "openid profile email offline_access";
    public const int CallbackPort = 1455;
    public const string SecretKey = "chatgpt-auth";
    public static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    private readonly ISecretStore _secrets;
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ChatGptAuthManager(ISecretStore secrets, HttpClient? http = null, Func<DateTimeOffset>? now = null)
    {
        _secrets = secrets;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The app-wide instance the notes pipeline reads; set once at startup.</summary>
    public static ChatGptAuthManager? Shared { get; set; }

    public event EventHandler? Changed;

    public bool IsSignedIn => Load() is { AccessToken.Length: > 0 };

    /// <summary>
    /// Runs the browser sign-in. <paramref name="openBrowser"/> receives the authorize URL; the
    /// method returns once the browser has come back to the loopback callback and the code has
    /// been exchanged, or throws <see cref="ChatGptAuthException"/> with a plain reason.
    /// </summary>
    public async Task SignInAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default)
    {
        var (verifier, challenge) = CreatePkce();
        var state = RandomToken();
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, CallbackPort);
            listener.Start();
        }
        catch (SocketException)
        {
            throw new ChatGptAuthException("Callback port 1455 is already in use");
        }

        string code;
        try
        {
            openBrowser(BuildAuthorizeUri(state, challenge));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CallbackTimeout);
            try
            {
                code = await WaitForCallbackAsync(listener, state, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ChatGptAuthException("Sign-in timed out — no response from browser");
            }
        }
        finally
        {
            listener.Stop();
        }

        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier
        }, "Token exchange failed", cancellationToken).ConfigureAwait(false);
        Save(tokens with { AccountId = AccountIdFromAccessToken(tokens.AccessToken) });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SignOut()
    {
        DeleteStored();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A usable access token, refreshed first when it is about to expire.</summary>
    public async Task<ChatGptCredential> GetValidCredentialAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = Load();
            if (stored is null || stored.AccessToken.Length == 0) throw new ChatGptAuthException("Not signed in to ChatGPT");
            var expiring = stored.ExpiresAtMs > 0 &&
                           DateTimeOffset.FromUnixTimeMilliseconds(stored.ExpiresAtMs) - _now() <= RefreshMargin;
            if (!expiring || stored.RefreshToken.Length == 0) return new ChatGptCredential(stored.AccessToken, stored.AccountId);

            var refreshed = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = stored.RefreshToken
            }, "Token refresh failed", cancellationToken).ConfigureAwait(false);
            var updated = refreshed with
            {
                RefreshToken = refreshed.RefreshToken.Length > 0 ? refreshed.RefreshToken : stored.RefreshToken,
                AccountId = AccountIdFromAccessToken(refreshed.AccessToken) is { Length: > 0 } id ? id : stored.AccountId
            };
            Save(updated);
            return new ChatGptCredential(updated.AccessToken, updated.AccountId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = RandomToken();
        return (verifier, Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))));
    }

    public static Uri BuildAuthorizeUri(string state, string challenge)
    {
        var query = new (string Key, string Value)[]
        {
            ("client_id", ClientId),
            ("redirect_uri", RedirectUri),
            ("response_type", "code"),
            ("scope", Scopes),
            ("state", state),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("id_token_add_organizations", "true"),
            ("codex_cli_simplified_flow", "true"),
            ("originator", "muesli")
        };
        return new Uri(AuthorizeUrl + "?" + string.Join("&", query.Select(pair =>
            $"{pair.Key}={Uri.EscapeDataString(pair.Value)}")));
    }

    /// <summary>The ChatGPT account the token belongs to, read from the access token's claims.</summary>
    public static string AccountIdFromAccessToken(string accessToken)
    {
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2) return "";
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;
            if (StringClaim(root, "chatgpt_account_id") is { Length: > 0 } direct) return direct;
            if (root.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object &&
                StringClaim(auth, "chatgpt_account_id") is { Length: > 0 } nested) return nested;
            if (root.TryGetProperty("organizations", out var organizations) && organizations.ValueKind == JsonValueKind.Array)
            {
                var first = organizations.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object && StringClaim(first, "id") is { Length: > 0 } organization) return organization;
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
        }
        return "";
    }

    /// <summary>Reads the code out of one callback request line, or explains why it is unusable.</summary>
    public static (string? Code, string? Error) ParseCallback(string requestLine, string expectedState)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return (null, null);
        var target = parts[1];
        var queryStart = target.IndexOf('?');
        if (queryStart < 0) return (null, null);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in target[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            values[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
        }
        if (!values.ContainsKey("code") && !values.ContainsKey("error") && !values.ContainsKey("state")) return (null, null);
        if (!values.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            return (null, "OAuth state mismatch — possible CSRF attack");
        return values.TryGetValue("code", out var code) && code.Length > 0
            ? (code, null)
            : (null, "OAuth callback missing authorization code");
    }

    private static async Task<string> WaitForCallbackAsync(TcpListener listener, string state, CancellationToken token)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            var requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split('\n').FirstOrDefault()?.Trim() ?? "";
            var (code, error) = ParseCallback(requestLine, state);
            if (code is null && error is null)
            {
                // A favicon or other stray request from the browser; keep waiting for the callback.
                await RespondAsync(stream, "404 Not Found", "Not found", "", token).ConfigureAwait(false);
                continue;
            }
            if (code is null)
            {
                await RespondAsync(stream, "400 Bad Request", "Sign-in failed", "Security validation failed. Please try again.", token).ConfigureAwait(false);
                throw new ChatGptAuthException(error!);
            }
            await RespondAsync(stream, "200 OK", "Signed in to muesli", "You can close this window.", token).ConfigureAwait(false);
            return code;
        }
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string title, string detail, CancellationToken token)
    {
        var body = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>muesli</title></head>" +
                   $"<body style=\"font-family:Segoe UI,sans-serif;text-align:center;margin-top:18vh\"><h2>{title}</h2><p>{detail}</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task<StoredTokens> PostTokenAsync(Dictionary<string, string> form, string failure, CancellationToken token)
    {
        try
        {
            using var response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new ChatGptAuthException($"{failure}: the sign-in service answered {(int)response.StatusCode}");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var root = document.RootElement;
            var access = StringClaim(root, "access_token") ?? "";
            if (access.Length == 0) throw new ChatGptAuthException($"{failure}: no access token was returned");
            var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt64(out var seconds) ? seconds : 3600;
            return new StoredTokens(access, StringClaim(root, "refresh_token") ?? "",
                _now().AddSeconds(expiresIn).ToUnixTimeMilliseconds(), "");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested)
        {
            throw new ChatGptAuthException($"{failure}: {exception.GetType().Name}");
        }
    }

    private StoredTokens? Load()
    {
        try
        {
            var json = ReadStored();
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new StoredTokens(
                StringClaim(root, "access_token") ?? "",
                StringClaim(root, "refresh_token") ?? "",
                long.TryParse(StringClaim(root, "expires_at"), out var expiresAt) ? expiresAt : 0,
                StringClaim(root, "account_id") ?? "");
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Same blob as macOS keeps in the Keychain: four string values.
    private void Save(StoredTokens tokens) => WriteStored(JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["access_token"] = tokens.AccessToken,
        ["refresh_token"] = tokens.RefreshToken,
        ["expires_at"] = tokens.ExpiresAtMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["account_id"] = tokens.AccountId
    }));

    // A Windows credential holds at most 2560 bytes and the token blob is larger, so it is kept as
    // numbered parts: the head entry holds the part count.
    private const int PartLength = 1000;

    private void WriteStored(string json)
    {
        DeleteStored();
        var parts = (json.Length + PartLength - 1) / PartLength;
        for (var index = 0; index < parts; index++)
        {
            _secrets.Write($"{SecretKey}.{index}", json.Substring(index * PartLength, Math.Min(PartLength, json.Length - index * PartLength)));
        }
        _secrets.Write(SecretKey, parts.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private string? ReadStored()
    {
        if (!int.TryParse(_secrets.Read(SecretKey), out var parts) || parts is <= 0 or > 64) return null;
        var builder = new StringBuilder();
        for (var index = 0; index < parts; index++)
        {
            if (_secrets.Read($"{SecretKey}.{index}") is not { } part) return null;
            builder.Append(part);
        }
        return builder.ToString();
    }

    private void DeleteStored()
    {
        if (int.TryParse(_secrets.Read(SecretKey), out var parts) && parts is > 0 and <= 64)
        {
            for (var index = 0; index < parts; index++) _secrets.Delete($"{SecretKey}.{index}");
        }
        _secrets.Delete(SecretKey);
    }

    private static string? StringClaim(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record StoredTokens(string AccessToken, string RefreshToken, long ExpiresAtMs, string AccountId);
}
