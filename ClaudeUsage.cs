using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexBar;

internal sealed record ClaudeWindow(double UsedPercentage, DateTimeOffset? ResetsAt)
{
    public double Remaining => 100 - UsedPercentage;
    public bool Expired(DateTimeOffset now) => ResetsAt is { } reset && now >= reset;
}

internal sealed record ClaudeReading(DateTimeOffset ReceivedAt, ClaudeWindow? FiveHour,
    ClaudeWindow? SevenDay, string? Error = null)
{
    public bool Stale(DateTimeOffset now) => Error is not null || now - ReceivedAt >= TimeSpan.FromMinutes(10);
}

// Read-only account usage. Claude owns login/renewal; no tokens or usage are written to disk.
internal sealed class ClaudeUsageClient : IDisposable
{
    internal const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    internal const int MaxBytes = 65536;
    private readonly HttpClient http;
    private readonly Func<string> credentials;
    private readonly Func<DateTimeOffset> clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextAutomaticRead;
    private DateTimeOffset notBefore;
    private string? tokenFingerprint;
    public ClaudeReading? Current { get; private set; }

    public ClaudeUsageClient(HttpMessageHandler? handler = null, Func<string>? credentials = null,
        Func<DateTimeOffset>? clock = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CodexBar-MajorCommand/" + EditionVersion.Current);
        this.credentials = credentials ?? ReadCredentials;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal static string ReadCredentials()
    {
        var directory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(directory)) directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        using var stream = File.OpenRead(Path.Combine(directory, ".credentials.json"));
        if (stream.Length > MaxBytes) throw new InvalidDataException();
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxBytes + 1];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        if (length > MaxBytes) throw new InvalidDataException();
        return new string(buffer, 0, length);
    }

    internal static string ReadToken(string json, DateTimeOffset now)
    {
        if (json.Length > MaxBytes) throw new InvalidDataException();
        using var document = JsonDocument.Parse(json);
        var oauth = Property(document.RootElement, "claudeAiOauth");
        var token = Property(oauth, "accessToken").GetString();
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192 || token.Any(c => c <= ' ' || c > '~'))
            throw new InvalidDataException();
        var scopes = Property(oauth, "scopes");
        if (scopes.ValueKind != JsonValueKind.Array || !scopes.EnumerateArray().Any(s =>
            s.ValueKind == JsonValueKind.String && s.GetString() == "user:profile")) throw new InvalidDataException();
        var expiry = Property(oauth, "expiresAt", required: false);
        // Missing expiry is not Unix epoch zero; some Claude credentials omit it.
        if (expiry.ValueKind != JsonValueKind.Undefined &&
            (expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var milliseconds) || DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) <= now))
            throw new InvalidDataException();
        return token;
    }

    internal static ClaudeReading Parse(string json, DateTimeOffset now)
    {
        if (json.Length > MaxBytes) throw new InvalidDataException();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var session = Property(root, "five_hour", required: false);
        var week = Property(root, "seven_day", required: false);
        if (session.ValueKind == JsonValueKind.Undefined && week.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException();
        return new(now, Window(session), Window(week));
    }

    private static ClaudeWindow? Window(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var used = Property(value, "utilization");
        var reset = Property(value, "resets_at");
        if (used.ValueKind == JsonValueKind.Null) return null;
        if (used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) ||
            !double.IsFinite(percent) || percent is < 0 or > 100)
            throw new InvalidDataException();
        // Claude can return explicit usage with no reset time after a window ends.
        if (reset.ValueKind == JsonValueKind.Null) return new(percent, null);
        if (reset.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        var text = reset.GetString();
        if (text is null || !(text.EndsWith('Z') || text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-') ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new InvalidDataException();
        return new(percent, time);
    }

    private static JsonElement Property(JsonElement parent, string name, bool required = true)
    {
        if (parent.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var values = parent.EnumerateObject().Where(p => p.NameEquals(name)).ToArray();
        if (values.Length > 1 || required && values.Length == 0) throw new InvalidDataException();
        return values.Length == 0 ? default : values[0].Value;
    }

    public async Task RefreshAsync(bool manual = false, CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            var now = clock();
            if (now < notBefore || !manual && now < nextAutomaticRead) return;
            notBefore = now.AddMinutes(1);
            nextAutomaticRead = now.AddMinutes(5);
            string token;
            try { token = ReadToken(credentials(), now); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                JsonException or InvalidOperationException or ArgumentException)
            {
                tokenFingerprint = null;
                Current = new(now, null, null, "Sign in to Claude Code, then Refresh.");
                return;
            }
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            // Changed credentials may belong to another account. Do not carry old values across them.
            if (fingerprint != tokenFingerprint) Current = null;
            tokenFingerprint = fingerprint;
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    Current = new(now, null, null, response.StatusCode == HttpStatusCode.Unauthorized
                        ? "Sign in to Claude Code, then Refresh." : "Claude denied usage access.");
                    return;
                }
                if ((int)response.StatusCode == 429)
                {
                    var retry = response.Headers.RetryAfter?.Date ?? now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
                    notBefore = retry > now.AddMinutes(1) ? retry : now.AddMinutes(1);
                    Fail("Claude busy; waiting before retry.", now);
                    return;
                }
                if (!response.IsSuccessStatusCode)
                {
                    Fail("Claude unavailable; will retry.", now);
                    return;
                }
                if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var buffer = new byte[MaxBytes + 1];
                var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, timeout.Token).ConfigureAwait(false);
                if (length > MaxBytes) throw new InvalidDataException();
                Current = Parse(Encoding.UTF8.GetString(buffer, 0, length), clock());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or
                JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                Fail("Claude read failed; will retry.", now);
            }
        }
        finally { gate.Release(); }
    }

    private void Fail(string message, DateTimeOffset now) =>
        Current = Current is { } previous ? previous with { Error = message } : new(now, null, null, message);

    public void Dispose() => http.Dispose();
}
