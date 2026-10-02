using System.Globalization;
using System.Net;
using System.Text.Json;

namespace CodexBar;

internal enum AnnouncementPlacement { Bottom, Crown }

internal sealed record ScheduledReset(DateTimeOffset? ScheduledFor, bool Banked);
internal sealed record ResetWatch(int? Chance, string Window, DateTimeOffset ExpiresAt);

internal sealed record ResetAnnouncements(ScheduledReset? Scheduled, ResetWatch? Watch)
{
    public bool IsActive(DateTimeOffset now) => Scheduled is not null || Watch?.ExpiresAt > now;

    public string Label(DateTimeOffset now)
    {
        if (Scheduled is { } scheduled)
        {
            if (scheduled.ScheduledFor is not { } target) return "Reset Announced";
            var wait = target - now;
            // A passed deadline is not evidence that the reset was executed.
            if (wait <= TimeSpan.Zero) return "Reset Announced · pending";
            var time = wait.TotalHours >= 24 ? $"{Math.Ceiling(wait.TotalDays):0}d"
                : wait.TotalHours >= 1 ? $"{Math.Ceiling(wait.TotalHours):0}h"
                : $"{Math.Max(1, Math.Ceiling(wait.TotalMinutes)):0}m";
            return $"Reset Announced in ~{time}";
        }
        if (Watch is { } watch && watch.ExpiresAt > now)
            return watch.Chance is { } chance ? $"Possible reset · {chance}%" : "Possible reset";
        return "No reset announced";
    }

    public string Details(DateTimeOffset now)
    {
        if (Scheduled is { } scheduled)
        {
            var kind = scheduled.Banked ? "Banked reset credit" : "Regular reset";
            var timing = scheduled.ScheduledFor is { } target
                ? $"Reported for/by {target.ToLocalTime():ddd, MMM d · h:mm tt}." : "No time announced.";
            return $"{kind} announced, as reported by the tracker. {timing}\nAwaiting confirmation; your account's limits remain authoritative.";
        }
        if (Watch is { } watch && watch.ExpiresAt > now)
            return $"Possible reset: {watch.Window}. " +
                (watch.Chance is { } chance ? $"Tracker estimate: {chance}%. " : "") +
                "This is an AI-classified forecast, not an official commitment.";
        return "The tracker reports no active upcoming reset announcement or forecast.";
    }

    public static ResetAnnouncements Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("meta").GetProperty("api_version").GetString() != "v1")
            throw new FormatException("Unsupported reset announcement API version.");
        var data = root.GetProperty("data");
        var scheduled = data.GetProperty("scheduled_reset");
        var watch = data.GetProperty("active_watch");
        ScheduledReset? announced = null;
        ResetWatch? forecast = null;
        if (scheduled.ValueKind != JsonValueKind.Null)
        {
            if (scheduled.GetProperty("status").GetString() != "scheduled")
                throw new FormatException("Unknown reset announcement status.");
            var type = scheduled.GetProperty("reset_type").GetString();
            if (type is not ("regular" or "banked")) throw new FormatException("Unknown reset type.");
            var time = scheduled.GetProperty("scheduled_for");
            announced = new ScheduledReset(time.ValueKind == JsonValueKind.Null ? null : Timestamp(time), type == "banked");
        }
        if (watch.ValueKind != JsonValueKind.Null)
        {
            if (watch.GetProperty("level").GetString() is not ("elevated" or "strong"))
                throw new FormatException("Unknown reset forecast level.");
            var chanceValue = watch.GetProperty("reset_chance_percent");
            int? chance = chanceValue.ValueKind == JsonValueKind.Null ? null : chanceValue.GetInt32();
            if (chance is < 0 or > 100) throw new FormatException("Invalid reset forecast probability.");
            var window = watch.GetProperty("forecast_window").GetString();
            if (string.IsNullOrWhiteSpace(window) || window.Length > 160 || window.Any(char.IsControl))
                throw new FormatException("Invalid reset forecast window.");
            var expiry = Timestamp(watch.GetProperty("expires_at"));
            if (expiry <= Timestamp(watch.GetProperty("observed_at")))
                throw new FormatException("Invalid reset forecast expiry.");
            forecast = new ResetWatch(chance, window, expiry);
        }
        return new ResetAnnouncements(announced, forecast);
    }

    private static DateTimeOffset Timestamp(JsonElement value)
    {
        var text = value.GetString();
        // Require an explicit UTC/offset zone; never interpret remote timestamps as PC-local time.
        if (text is null || !(text.EndsWith('Z') ||
            text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-') ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new FormatException("Invalid reset announcement timestamp.");
        return time;
    }
}

internal sealed class AnnouncementReadException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed class ResetAnnouncementClient : IDisposable
{
    public const string SourceUrl = "https://codex-resets.com/";
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> clock;
    private ResetAnnouncements? cached;
    private string? etag;
    private DateTimeOffset retryAt;

    public ResetAnnouncementClient(HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CodexBar-MajorCommand/" + EditionVersion.Current);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ResetAnnouncements> ReadAsync(CancellationToken cancellationToken = default)
    {
        var now = clock();
        if (now < retryAt) throw new AnnouncementReadException("Reset tracker retry delayed.", retryAt - now);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        cancellationToken = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, SourceUrl + "api/v1/status");
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return cached ?? throw new AnnouncementReadException("Reset tracker returned no cached data.");
        if ((int)response.StatusCode is 429 or 503)
        {
            var retry = response.Headers.RetryAfter;
            var wait = retry?.Delta ?? (retry?.Date is { } date ? date - now : TimeSpan.FromMinutes(5));
            if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
            retryAt = now + wait;
            throw new AnnouncementReadException("Reset tracker temporarily unavailable.", wait);
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new AnnouncementReadException("Reset tracker returned an unexpected content type.");
        const int maxBytes = 128 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new AnnouncementReadException("Reset tracker response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int length;
        while ((length = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + length > maxBytes)
                throw new AnnouncementReadException("Reset tracker response is too large.");
            buffer.Write(chunk, 0, length);
        }
        var result = ResetAnnouncements.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        cached = result;
        etag = response.Headers.ETag?.ToString();
        return result;
    }

    public void Dispose() => http.Dispose();
}
