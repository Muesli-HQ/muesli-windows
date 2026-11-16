using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Muesli.Windows.Services;

public sealed class OpenRouterAuthException(string message) : Exception(message);

/// <summary>
/// "Connect OpenRouter" (macOS <c>OpenRouterAuthManager</c>): the browser asks the user to approve
/// access, OpenRouter calls back to a one-off loopback address, and the code is exchanged for an
/// ordinary API key the user controls on openrouter.ai. There are no refresh tokens. The caller
/// stores the returned key where a pasted key goes; this class keeps nothing and logs nothing.
/// </summary>
public sealed class OpenRouterAuthFlow(HttpClient? http = null)
{
    public const string AuthorizationEndpoint = "https://openrouter.ai/auth";
    public const string KeyExchangeEndpoint = "https://openrouter.ai/api/v1/auth/keys";
    public static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(600);

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _http = http ?? SharedHttp;

    public enum CallbackKind { AuthorizationCode, ProviderError, MissingCode, Invalid, Unrelated }

    public readonly record struct Callback(CallbackKind Kind, string? Value = null);

    /// <summary>Runs the whole flow and returns the new API key.</summary>
    public async Task<string> SignInAsync(
        Action<Uri> openBrowser, CancellationToken cancellationToken = default, TimeSpan? callbackTimeout = null)
    {
        var (verifier, challenge) = CreatePkce();
        // A random port and a random path: only the browser tab we opened knows where to call back.
        var path = "/muesli/openrouter/oauth/" + RandomToken();
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
        }
        catch (SocketException)
        {
            throw new OpenRouterAuthException("Could not start the OpenRouter sign-in callback server");
        }

        string code;
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                openBrowser(BuildAuthorizationUri(new Uri($"http://localhost:{port}{path}"), challenge));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new OpenRouterAuthException("Could not open the browser for OpenRouter sign-in");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(callbackTimeout ?? CallbackTimeout);
            try
            {
                code = await WaitForCallbackAsync(listener, path, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new OpenRouterAuthException("OpenRouter sign-in timed out — no response from the browser");
            }
        }
        finally
        {
            listener.Stop();
        }

        return await ExchangeAsync(code, verifier, cancellationToken).ConfigureAwait(false);
    }

    public static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = RandomToken();
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public static Uri BuildAuthorizationUri(Uri callback, string challenge) => new(
        $"{AuthorizationEndpoint}?callback_url={Uri.EscapeDataString(callback.AbsoluteUri)}" +
        $"&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256");

    /// <summary>The page on openrouter.ai where the user can revoke this key (macOS <c>manageKeyURL</c>).</summary>
    public static Uri ManageKeyUri(string apiKey) => new(
        "https://openrouter.ai/keys/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))));

    /// <summary>
    /// Reads the first line of a request to the callback listener. Anything not addressed to this
    /// sign-in's path (a favicon, a port scanner) is unrelated and does not end the wait.
    /// </summary>
    public static Callback ParseCallback(string httpRequest, string expectedPath)
    {
        var requestLine = httpRequest.Split('\n', 2)[0].Trim();
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return new Callback(CallbackKind.Invalid);
        var target = parts[1];
        var queryStart = target.IndexOf('?');
        var path = queryStart < 0 ? target : target[..queryStart];
        if (!string.Equals(path, expectedPath, StringComparison.Ordinal)) return new Callback(CallbackKind.Unrelated);
        if (parts[0] != "GET") return new Callback(CallbackKind.Invalid);

        string? code = null, error = null;
        foreach (var pair in (queryStart < 0 ? "" : target[(queryStart + 1)..]).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? "" : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            if (name == "code") code ??= value;
            else if (name == "error") error ??= value;
        }

        if (!string.IsNullOrEmpty(code)) return new Callback(CallbackKind.AuthorizationCode, code);
        return string.IsNullOrEmpty(error)
            ? new Callback(CallbackKind.MissingCode)
            : new Callback(CallbackKind.ProviderError, error);
    }

    public async Task<string> ExchangeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, KeyExchangeEndpoint);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["code_challenge_method"] = "S256"
        }), Encoding.UTF8, "application/json");

        string payload;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new OpenRouterAuthException($"OpenRouter key exchange failed (HTTP {(int)response.StatusCode})");
            payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new OpenRouterAuthException("OpenRouter key exchange failed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OpenRouterAuthException("OpenRouter key exchange timed out");
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String &&
                key.GetString()?.Trim() is { Length: > 0 } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }

        throw new OpenRouterAuthException("OpenRouter key exchange returned an invalid response");
    }

    private static async Task<string> WaitForCallbackAsync(TcpListener listener, string path, CancellationToken token)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            var callback = ParseCallback(Encoding.ASCII.GetString(buffer, 0, read), path);
            switch (callback.Kind)
            {
                case CallbackKind.AuthorizationCode:
                    await RespondAsync(stream, "200 OK", "OpenRouter connected to muesli", "You can close this window.", token).ConfigureAwait(false);
                    return callback.Value!;
                case CallbackKind.Unrelated:
                    await RespondAsync(stream, "404 Not Found", "Not found", "", token).ConfigureAwait(false);
                    continue;
                case CallbackKind.ProviderError:
                    await RespondAsync(stream, "400 Bad Request", "Sign-in cancelled", "You can close this window.", token).ConfigureAwait(false);
                    // Only a short fixed set of characters from the provider's reason reaches the UI.
                    throw new OpenRouterAuthException($"OpenRouter sign-in was not approved ({Sanitize(callback.Value)})");
                case CallbackKind.MissingCode:
                    await RespondAsync(stream, "400 Bad Request", "Sign-in failed", "Please try again.", token).ConfigureAwait(false);
                    throw new OpenRouterAuthException("OpenRouter callback did not include an authorization code");
                default:
                    await RespondAsync(stream, "400 Bad Request", "Sign-in failed", "Please try again.", token).ConfigureAwait(false);
                    throw new OpenRouterAuthException("Received an invalid OpenRouter sign-in callback");
            }
        }
    }

    private static string Sanitize(string? reason)
    {
        var kept = new string((reason ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ' ').Take(60).ToArray());
        return kept.Length == 0 ? "no reason given" : kept;
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string title, string detail, CancellationToken token)
    {
        var body = "<!doctype html><html><head><meta charset=\"utf-8\"><title>muesli</title></head>" +
                   $"<body style=\"font-family:Segoe UI,sans-serif;text-align:center;margin-top:18vh\"><h2>{title}</h2><p>{detail}</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
