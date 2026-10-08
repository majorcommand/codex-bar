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
    ClaudeWindow? SevenDay, string? Error = null, ClaudeResetInventory? Resets = null)
{
    public bool Stale(DateTimeOffset now) => Error is not null || now - ReceivedAt >= TimeSpan.FromMinutes(10);
}

internal sealed record ClaudeResetGrant(int Remaining, DateTimeOffset? StartsAt, DateTimeOffset? ExpiresAt,
    bool Paused, bool? UsableNow, string Kind)
{
    public bool Available(DateTimeOffset now) => !Paused && Remaining > 0 &&
        (StartsAt is null || StartsAt <= now) && (ExpiresAt is null || ExpiresAt > now);
}

internal sealed record ClaudeResetInventory(IReadOnlyList<ClaudeResetGrant> Grants)
{
    public IReadOnlyList<ClaudeResetGrant> Available(DateTimeOffset now) => Grants.Where(g => g.Available(now))
        .OrderBy(g => g.ExpiresAt ?? DateTimeOffset.MaxValue).ToArray();
}

internal sealed record ClaudeLogin(string AccessToken, DateTimeOffset? ExpiresAt, bool CanRenew);

// Read-only account usage. Native Claude Code owns credential writes and renewal.
internal sealed class ClaudeUsageClient : IDisposable
{
    internal const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    internal const int MaxBytes = 65536;
    private readonly HttpClient http;
    private readonly Func<string> credentials;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<CancellationToken, Task<bool>> renewCredentials;
    private readonly Func<string?> installedVersion;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextAutomaticRead;
    private DateTimeOffset notBefore;
    private string? tokenFingerprint;
    public ClaudeReading? Current { get; private set; }

    public ClaudeUsageClient(HttpMessageHandler? handler = null, Func<string>? credentials = null,
        Func<DateTimeOffset>? clock = null, Func<CancellationToken, Task<bool>>? renewCredentials = null,
        Func<string?>? installedVersion = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CodexBar-MajorCommand/" + EditionVersion.Current);
        this.credentials = credentials ?? ReadCredentials;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.installedVersion = installedVersion ?? (credentials is null ? ClaudeCodeRenewal.InstalledVersion : () => null);
        // Injected credential fixtures must never launch the user's real Claude installation.
        this.renewCredentials = renewCredentials ?? (credentials is null ? ClaudeCodeRenewal.TryRenewAsync :
            _ => Task.FromResult(false));
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
        var login = ReadLogin(json);
        if (login.ExpiresAt is { } expiry && expiry <= now) throw new InvalidDataException();
        return login.AccessToken;
    }

    internal static ClaudeLogin ReadLogin(string json)
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
        DateTimeOffset? expiresAt = null;
        if (expiry.ValueKind != JsonValueKind.Undefined)
        {
            if (expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var milliseconds))
                throw new InvalidDataException();
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        var refresh = Property(oauth, "refreshToken", required: false);
        if (refresh.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
            throw new InvalidDataException();
        return new(token, expiresAt, refresh.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(refresh.GetString()));
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
        return new(now, Window(session), Window(week), Resets: ResetInventory(root));
    }

    private static ClaudeResetInventory? ResetInventory(JsonElement root)
    {
        // Optional inventory must not invalidate independently valid usage windows.
        // Never retain grant IDs/labels or guess a count from malformed inventory.
        try
        {
            var status = Property(root, "cedar_ember", required: false);
            if (status.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (Property(status, "eligible").ValueKind != JsonValueKind.True) return null;
            var grants = Property(status, "grants");
            if (grants.ValueKind != JsonValueKind.Array || grants.GetArrayLength() > 200) return null;
            var result = new List<ClaudeResetGrant>();
            foreach (var grant in grants.EnumerateArray())
            {
                var remaining = Property(grant, "resets_left");
                if (!remaining.TryGetInt32(out var count) || count is < 0 or > 1000) return null;
                var paused = Property(grant, "paused");
                if (paused.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                var usable = Property(grant, "usable_now", required: false);
                if (usable.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False)) return null;
                var start = OptionalResetTime(Property(grant, "starts_at", required: false));
                var end = OptionalResetTime(Property(grant, "ends_at", required: false));
                if (start is not null && end is not null && end <= start) return null;
                var clears = Property(grant, "clears");
                if (clears.ValueKind != JsonValueKind.Array || clears.GetArrayLength() > 20) return null;
                var names = clears.EnumerateArray().Select(value => value.GetString()).ToArray();
                if (names.Any(name => string.IsNullOrWhiteSpace(name)) || names.Distinct().Count() != names.Length) return null;
                var fiveHour = names.Contains("five_hour");
                var week = names.Contains("seven_day");
                var kind = fiveHour && week ? "Full reset" : fiveHour ? "5-hour reset" : week ? "Weekly reset" : "Other reset";
                result.Add(new(count, start, end, paused.GetBoolean(), usable.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? usable.GetBoolean() : null, kind));
            }
            if (result.Sum(g => g.Remaining) > 10000) return null;
            return new(result);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static DateTimeOffset? OptionalResetTime(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        var text = value.GetString();
        if (text is null || !(text.EndsWith('Z') || text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-') ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) throw new InvalidDataException();
        return parsed;
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
            ClaudeLogin login;
            try { login = ReadLogin(credentials()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                JsonException or InvalidOperationException or ArgumentException)
            {
                tokenFingerprint = null;
                Current = new(now, null, null, "Sign in to Claude Code, then Refresh.");
                return;
            }
            var renewed = false;
            if (login.CanRenew && login.ExpiresAt is { } expiry && expiry <= now.AddMinutes(5))
            {
                renewed = true;
                var latest = await RenewAsync(cancellationToken).ConfigureAwait(false);
                if (latest is null)
                {
                    Current = new(now, null, null, "Claude renewal failed; retrying.");
                    return;
                }
                login = latest;
            }
            if (login.ExpiresAt is { } expired && expired <= clock())
            {
                Current = new(now, null, null, login.CanRenew
                    ? "Claude renewal failed; retrying." : "Sign in to Claude Code, then Refresh.");
                return;
            }
            AdoptLogin(login);
            var version = installedVersion();
            var includeInventory = version is not null && System.Text.RegularExpressions.Regex.IsMatch(version,
                @"\A[0-9]+\.[0-9]+\.[0-9]+\z");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var token = login.AccessToken;
                using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl + (includeInventory ? "?cedar_ember=1" : ""));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                if (includeInventory) request.Headers.UserAgent.ParseAdd($"claude-cli/{version} (external, cli)");
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (includeInventory && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
                    {
                        var rejection = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                        if (!Encoding.UTF8.GetString(rejection).Contains("user:profile", StringComparison.Ordinal))
                        {
                            includeInventory = false;
                            continue;
                        }
                    }
                    if (response.StatusCode == HttpStatusCode.Unauthorized && !renewed && login.CanRenew)
                    {
                        renewed = true;
                        var latest = await RenewAsync(cancellationToken).ConfigureAwait(false);
                        if (latest is not null)
                        {
                            login = latest;
                            AdoptLogin(login);
                            continue;
                        }
                    }
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        Current = new(now, null, null, response.StatusCode == HttpStatusCode.Unauthorized
                            ? "Claude login rejected; sign in again." : "Claude denied usage access.");
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
                    var buffer = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                    Current = Parse(Encoding.UTF8.GetString(buffer), clock());
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or
                    JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
                {
                    Fail("Claude read failed; will retry.", now);
                    return;
                }
            }
            Fail("Claude read failed; will retry.", now);
        }
        finally { gate.Release(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxBytes) throw new InvalidDataException();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxBytes + 1];
        var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (length > MaxBytes) throw new InvalidDataException();
        return buffer[..length];
    }

    private void AdoptLogin(ClaudeLogin login)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(login.AccessToken)));
        // Changed credentials may belong to another account. Do not carry old values across them.
        if (fingerprint != tokenFingerprint) Current = null;
        tokenFingerprint = fingerprint;
    }

    private async Task<ClaudeLogin?> RenewAsync(CancellationToken cancellationToken)
    {
        try
        {
            await renewCredentials(cancellationToken).ConfigureAwait(false);
            // Process success is not proof of renewal. Only the reread native credential counts.
            var latest = ReadLogin(credentials());
            return latest.ExpiresAt is { } expiry && expiry <= clock() ? null : latest;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            return null;
        }
    }

    private void Fail(string message, DateTimeOffset now) =>
        Current = Current is { } previous ? previous with { Error = message } : new(now, null, null, message);

    public void Dispose() => http.Dispose();
}
