using System.Net;
using System.Text.Json;
using CodexBar;

internal static partial class Checks
{
    private static string ClaudeFixture(DateTimeOffset now, double used = 34) => JsonSerializer.Serialize(new
    {
        five_hour = new { utilization = used, resets_at = now.AddHours(3).ToString("O") },
        seven_day = new { utilization = 15, resets_at = now.AddDays(6).ToString("O") }
    });

    private static string ClaudeCredential(DateTimeOffset now, string token = "fixture-token", string scope = "user:profile") =>
        JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = token, scopes = new[] { scope }, expiresAt = now.AddHours(8).ToUnixTimeMilliseconds() } });

    private static async Task CheckClaudeApiAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = now;
        var credential = ClaudeCredential(now);
        var payload = ClaudeFixture(now);
        var reading = ClaudeUsageClient.Parse(payload, now);
        Check("Claude account utilization converts to remaining", reading.FiveHour?.Remaining == 66 && reading.SevenDay?.Remaining == 85);
        Check("Claude timestamps respect timezone and reset boundary", reading.FiveHour!.ResetsAt == now.AddHours(3) &&
            reading.FiveHour.Expired(now.AddHours(3)) && !reading.FiveHour.Expired(now));
        Check("Claude stale boundary and read failures are explicit", !reading.Stale(now.AddMinutes(9)) && reading.Stale(now.AddMinutes(10)) &&
            (reading with { Error = "failure" }).Stale(now));
        Check("Null and independently absent Claude windows are unavailable", ClaudeUsageClient.Parse("{\"five_hour\":null}", now) is { FiveHour: null, SevenDay: null });
        var afterReset = payload.Replace("\"utilization\":34", "\"utilization\":0")
            .Replace(JsonSerializer.Serialize(now.AddHours(3).ToString("O")), "null");
        Check("Claude reset transition preserves explicit usage and the other allowance", ClaudeUsageClient.Parse(afterReset, now) is
            { FiveHour.Remaining: 100, FiveHour.ResetsAt: null, SevenDay.Remaining: 85 });
        Check("Missing reset time is not treated as an expired window", !new ClaudeWindow(0, null).Expired(now));
        foreach (var bad in new[] { "{}", "[]", "{", "{\"five_hour\":{}}", "{\"five_hour\":null,\"five_hour\":null}",
            payload.Replace("34", "101"), payload.Replace("34", "-1"),
            "{\"five_hour\":{\"utilization\":0,\"resets_at\":\"2026-10-06T12:00:00\"}}", new string('x', 65537) })
        {
            var rejected = false;
            try { ClaudeUsageClient.Parse(bad, now); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { rejected = true; }
            Check("Unknown, ambiguous, invalid and oversized Claude usage rejected", rejected);
        }
        Check("Profile scope token accepted", ClaudeUsageClient.ReadToken(credential, now) == "fixture-token");
        Check("Missing token expiry is not falsely treated as 1970", ClaudeUsageClient.ReadToken(
            "{\"claudeAiOauth\":{\"accessToken\":\"fixture\",\"scopes\":[\"user:profile\"]}}", now) == "fixture");
        foreach (var bad in new[] { "{}", "{\"claudeAiOauth\":null}", ClaudeCredential(now, ""), ClaudeCredential(now, "bad\ntoken"),
            ClaudeCredential(now, scope: "user:inference"), ClaudeCredential(now.AddDays(-1)),
            "{\"claudeAiOauth\":{\"accessToken\":\"fixture\",\"accessToken\":\"ambiguous\",\"scopes\":[\"user:profile\"]}}" })
        {
            var rejected = false;
            try { ClaudeUsageClient.ReadToken(bad, now); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or ArgumentException) { rejected = true; }
            Check("Missing, expired, ambiguous or insufficient Claude credentials rejected", rejected);
        }
        HttpStatusCode status = HttpStatusCode.OK;
        var correctRequest = true;
        using var handler = new ResetAnnouncementChecks.Handler((request, _) =>
        {
            correctRequest &= request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == ClaudeUsageClient.UsageUrl &&
                request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "fixture-token" &&
                request.Headers.GetValues("anthropic-beta").Single() == "oauth-2025-04-20" && request.Content is null;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(payload) });
        });
        using var client = new ClaudeUsageClient(handler, () => credential, () => clock);
        await client.RefreshAsync();
        Check("Real Claude client sends fixed-origin authenticated read", correctRequest && client.Current?.FiveHour?.Remaining == 66);
        await client.RefreshAsync(); await client.RefreshAsync(manual: true);
        Check("Manual refresh cannot hammer Claude", handler.Calls == 1);
        clock = now.AddMinutes(1);
        await client.RefreshAsync();
        Check("Automatic Claude polling waits five minutes", handler.Calls == 1);
        await client.RefreshAsync(manual: true);
        Check("Manual read allowed after a minute", handler.Calls == 2);
        var captured = client.Current!.ReceivedAt;
        clock = now.AddMinutes(6); status = HttpStatusCode.ServiceUnavailable;
        await client.RefreshAsync();
        Check("Temporary outage preserves reading with original timestamp and stale marker", client.Current is { Error: not null, FiveHour.Remaining: 66 } && client.Current.ReceivedAt == captured);
        clock = now.AddMinutes(11); status = HttpStatusCode.Unauthorized;
        await client.RefreshAsync();
        Check("Rejected login clears account readings", client.Current is { Error: not null, FiveHour: null, SevenDay: null });
        clock = now.AddMinutes(16); status = HttpStatusCode.OK;
        await client.RefreshAsync();
        Check("Claude usage recovers after renewed login", client.Current is { Error: null, FiveHour.Remaining: 66 });
        credential = ClaudeCredential(clock, "another-account"); clock = now.AddMinutes(21); status = HttpStatusCode.ServiceUnavailable;
        await client.RefreshAsync();
        Check("Credential changes never expose previous account readings", client.Current is { Error: not null, FiveHour: null });

        using var throttledHandler = new ResetAnnouncementChecks.Handler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new(System.TimeSpan.FromMinutes(20));
            return Task.FromResult(response);
        });
        using var throttled = new ClaudeUsageClient(throttledHandler, () => ClaudeCredential(clock), () => clock);
        await throttled.RefreshAsync(); clock = clock.AddMinutes(10);
        await throttled.RefreshAsync(manual: true);
        Check("Claude Retry-After applies to automatic and manual reads", throttledHandler.Calls == 1 && throttled.Current?.Error is not null);
        clock = clock.AddMinutes(11); await throttled.RefreshAsync();
        Check("Claude rate-limit backoff eventually permits retry", throttledHandler.Calls == 2);

        foreach (var failure in new[] { HttpStatusCode.Redirect, HttpStatusCode.Forbidden })
        {
            using var failed = new ClaudeUsageClient(new ResetAnnouncementChecks.Handler((_, _) =>
                Task.FromResult(new HttpResponseMessage(failure) { Content = new StringContent("DO NOT DISPLAY RAW SERVER BODY") })), () => ClaudeCredential(now));
            await failed.RefreshAsync();
            Check("Redirect/forbidden fails without displaying response bodies", failed.Current?.Error is not null && !failed.Current.Error.Contains("RAW"));
        }
        foreach (var invalid in new[] { "{}", "broken", new string('x', 65537) })
        {
            using var failed = new ClaudeUsageClient(new ResetAnnouncementChecks.Handler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(invalid) })), () => ClaudeCredential(now));
            await failed.RefreshAsync();
            Check("Malformed and oversized HTTP response produces unavailable state", failed.Current is { Error: not null, FiveHour: null });
        }
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pendingHandler = new ResetAnnouncementChecks.Handler(async (_, ct) =>
        {
            pending.SetResult(); await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cancellable = new ClaudeUsageClient(pendingHandler, () => ClaudeCredential(now));
        var first = cancellable.RefreshAsync(cancellationToken: cancellation.Token);
        await pending.Task;
        await cancellable.RefreshAsync();
        Check("Concurrent Claude refreshes share one in-flight request", pendingHandler.Calls == 1);
        cancellation.Cancel();
        var cancelled = false;
        try { await first; } catch (OperationCanceledException) { cancelled = true; }
        Check("Claude read cancels on shutdown", cancelled);
    }

    private static void CheckClaudeFormRefresh()
    {
        var now = DateTimeOffset.UtcNow;
        var payload = ClaudeFixture(now);
        using var formClient = new ClaudeUsageClient(new ResetAnnouncementChecks.Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) })), () => ClaudeCredential(now));
        using var form = new WidgetForm(new AppSettings { AlwaysOnTop = false }, claudeUsageClient: formClient);
        ((NotifyIcon)Get(form, "trayIcon")!).Visible = false;
        ((Task)Invoke(form, "RefreshClaudeAsync", false)!).GetAwaiter().GetResult();
        Check("Actual form refresh consumes the API reading", Get(form, "claudeReading") is ClaudeReading { Error: null, SevenDay.Remaining: 85 });
    }

    private static void CheckLiveClaude(string? output)
    {
        using var client = new ClaudeUsageClient();
        client.RefreshAsync().GetAwaiter().GetResult();
        var reading = client.Current;
        if (reading?.Error is { } error) Console.WriteLine("Live Claude unavailable: " + error);
        Check("Live Claude account read succeeds", reading is { Error: null } && (reading.FiveHour is not null || reading.SevenDay is not null));
        Console.WriteLine($"Live Claude: five-hour remaining={reading!.FiveHour?.Remaining:0.#}%; weekly remaining={reading.SevenDay?.Remaining:0.#}%; checked={reading.ReceivedAt:O}");
        using var form = new WidgetForm(new AppSettings { AlwaysOnTop = false, X = 100, Y = 100 });
        ((NotifyIcon)Get(form, "trayIcon")!).Visible = false;
        InitializeForm(form);
        Set(form, "showClaude", true); Set(form, "claudeReading", reading); Invoke(form, "UpdateFaceSize");
        Save(form, output, "claude-live-account");
    }
}
