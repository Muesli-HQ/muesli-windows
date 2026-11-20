using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Muesli.Windows.Services;

namespace Muesli.Windows.ComputerUse;

public sealed class ChatGptAuthException(string message) : Exception(message);

/// <summary>
/// ChatGPT account sign-in, the same OAuth PKCE flow as macOS <c>ChatGPTAuthManager</c>: a browser
/// sign-in that redirects to a loopback listener on port 1455, then refresh-token renewal. Tokens
/// live in the Windows Credential Manager (split across entries: a credential holds 2.5 KB and a
/// token is larger).
/// </summary>
public sealed class ChatGptAuth(ISecretStore secrets, HttpClient http, Action<Uri>? openBrowser = null)
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const string RedirectUri = "http://localhost:1455/auth/callback";
    private const string SecretKey = "chatgpt-auth";
    private const int ChunkLength = 1000;

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Action<Uri> _openBrowser = openBrowser ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));

    public sealed record Tokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string AccountId);

    public bool IsSignedIn => Load() is not null;

    /// <summary>Opens the browser and waits (up to 5 minutes) for the user to finish signing in.</summary>
    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));

        using var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:1455/");
        try { listener.Start(); }
        catch (HttpListenerException) { throw new ChatGptAuthException("Port 1455 is in use by another app. Close it and try again."); }

        _openBrowser(AuthorizationUri(challenge, state));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var stop = timeout.Token.Register(listener.Stop);
        string code;
        while (true)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) when (timeout.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new ChatGptAuthException("Sign-in timed out. Try again.");
            }
            if (context.Request.Url?.AbsolutePath != "/auth/callback")
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }
            var query = context.Request.QueryString;
            var ok = query["state"] == state && !string.IsNullOrEmpty(query["code"]);
            await Respond(context, ok ? "Signed in to Muesli" : "Sign-in failed",
                ok ? "You can close this tab and return to Muesli." : "Return to Muesli and try again.");
            if (!ok) throw new ChatGptAuthException(query["error_description"] ?? "Sign-in was not completed.");
            code = query["code"]!;
            break;
        }

        Save(await RequestTokens(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = ClientId, ["code"] = code,
            ["redirect_uri"] = RedirectUri, ["code_verifier"] = verifier,
        }, null, cancellationToken));
    }

    public void SignOut()
    {
        var count = int.TryParse(secrets.Read(SecretKey), out var parts) ? parts : 0;
        for (var index = 0; index < count; index++) secrets.Delete($"{SecretKey}/{index}");
        secrets.Delete(SecretKey);
    }

    /// <summary>A valid access token and ChatGPT account id, refreshed when it is about to expire.</summary>
    public async Task<(string Token, string AccountId)> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            var tokens = Load() ?? throw new ChatGptAuthException("Sign in to ChatGPT in Muesli settings to use Computer Use.");
            if (tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return (tokens.AccessToken, tokens.AccountId);
            if (tokens.RefreshToken.Length == 0) throw new ChatGptAuthException("Your ChatGPT sign-in expired. Sign in again.");
            tokens = await RequestTokens(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = tokens.RefreshToken,
            }, tokens.RefreshToken, cancellationToken);
            Save(tokens);
            return (tokens.AccessToken, tokens.AccountId);
        }
        finally { _refresh.Release(); }
    }

    internal static Uri AuthorizationUri(string challenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = ClientId, ["redirect_uri"] = RedirectUri,
            ["scope"] = "openid profile email offline_access", ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true", ["codex_cli_simplified_flow"] = "true", ["state"] = state, ["originator"] = "muesli",
        };
        return new Uri(AuthorizeUrl + "?" + string.Join("&", query.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}")));
    }

    private async Task<Tokens> RequestTokens(Dictionary<string, string> form, string? previousRefresh, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (previousRefresh is not null && (int)response.StatusCode is 400 or 401) SignOut();
            throw new ChatGptAuthException(previousRefresh is null
                ? $"ChatGPT sign-in failed ({(int)response.StatusCode})."
                : "Your ChatGPT sign-in expired. Sign in again.");
        }
        var json = JsonNode.Parse(body)!.AsObject();
        var access = json["access_token"]?.GetValue<string>() ?? throw new ChatGptAuthException("ChatGPT did not return an access token.");
        var expiresIn = json["expires_in"]?.GetValue<double>() ?? 3600;
        return new Tokens(access, json["refresh_token"]?.GetValue<string>() ?? previousRefresh ?? "",
            DateTimeOffset.UtcNow.AddSeconds(expiresIn), AccountId(access));
    }

    /// <summary>The ChatGPT account id claim of an access token (same claim paths as Codex).</summary>
    internal static string AccountId(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return "";
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var claims = JsonNode.Parse(Convert.FromBase64String(payload))!.AsObject();
            return claims["chatgpt_account_id"]?.GetValue<string>()
                ?? claims["https://api.openai.com/auth"]?["chatgpt_account_id"]?.GetValue<string>()
                ?? claims["organizations"]?[0]?["id"]?.GetValue<string>()
                ?? "";
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException) { return ""; }
    }

    private Tokens? Load()
    {
        if (!int.TryParse(secrets.Read(SecretKey), out var count) || count <= 0) return null;
        var json = string.Concat(Enumerable.Range(0, count).Select(index => secrets.Read($"{SecretKey}/{index}")));
        try { return JsonSerializer.Deserialize<Tokens>(json); }
        catch (JsonException) { return null; }
    }

    private void Save(Tokens tokens)
    {
        SignOut();
        var json = JsonSerializer.Serialize(tokens);
        var parts = json.Chunk(ChunkLength).Select(chunk => new string(chunk)).ToArray();
        for (var index = 0; index < parts.Length; index++) secrets.Write($"{SecretKey}/{index}", parts[index]);
        secrets.Write(SecretKey, parts.Length.ToString());
    }

    private static async Task Respond(HttpListenerContext context, string title, string message)
    {
        var html = $"<!doctype html><meta charset=utf-8><title>{title}</title><body style=\"font-family:Segoe UI,system-ui;display:grid;place-items:center;height:100vh;margin:0\"><div style=\"text-align:center\"><h2>{title}</h2><p>{message}</p></div>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
