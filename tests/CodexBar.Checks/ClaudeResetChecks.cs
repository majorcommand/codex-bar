using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexBar;

internal static partial class Checks
{
    private static string ClaudeResetFixture(DateTimeOffset now)
    {
        var root = JsonNode.Parse(ClaudeFixture(now))!;
        root["cedar_ember"] = JsonSerializer.SerializeToNode(new
        {
            eligible = true,
            grants = new[] { new { id = "PRIVATE-GRANT", label = "PRIVATE-LABEL", resets_left = 2,
                starts_at = now.AddDays(-1).ToString("O"), ends_at = now.AddDays(15).ToString("O"),
                paused = false, usable_now = true,
                clears = new[] { "five_hour", "seven_day", "seven_day_overage_included" } } }
        });
        return root.ToJsonString();
    }

    private static async Task CheckClaudeResetApiAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var payload = ClaudeResetFixture(now);
        var reading = ClaudeUsageClient.Parse(payload, now);
        var grant = reading.Resets!.Available(now).Single();
        Check("Claude reset inventory counts remaining uses and full scope", grant.Remaining == 2 && grant.Kind == "Full reset" &&
            grant.ExpiresAt == now.AddDays(15) && grant.UsableNow == true);
        Check("Claude grant identifiers and server labels are not retained", !JsonSerializer.Serialize(reading).Contains("PRIVATE"));
        Check("Reset grants do not alter allowance or forecasts", reading.FiveHour?.Remaining == 66 && reading.SevenDay?.Remaining == 85);
        var root = JsonNode.Parse(payload)!;
        root["cedar_ember"]!["grants"] = new JsonArray();
        Check("Known empty Claude inventory differs from missing inventory", ClaudeUsageClient.Parse(root.ToJsonString(), now).Resets?.Available(now).Count == 0 &&
            ClaudeUsageClient.Parse(ClaudeFixture(now), now).Resets is null);
        foreach (var status in new[] { "null", "{}", "[]", "{\"eligible\":false,\"grants\":[]}",
            "{\"eligible\":true,\"eligible\":true,\"grants\":[]}" })
        {
            root["cedar_ember"] = JsonNode.Parse(status);
            var parsed = ClaudeUsageClient.Parse(root.ToJsonString(), now);
            Check("Unavailable optional reset inventory preserves usage", parsed.Resets is null && parsed.FiveHour?.Remaining == 66);
        }
        foreach (var (field, value) in new[] { ("resets_left", "-1"), ("resets_left", "1.5"), ("resets_left", "1001"),
            ("paused", "null"), ("usable_now", "\"yes\""), ("ends_at", "\"2026-10-23T02:00:00\""),
            ("ends_at", "\"invalid\""), ("clears", "[\"five_hour\",\"five_hour\"]"), ("clears", "[42]") })
        {
            root = JsonNode.Parse(payload)!;
            root["cedar_ember"]!["grants"]![0]![field] = JsonNode.Parse(value);
            var parsed = ClaudeUsageClient.Parse(root.ToJsonString(), now);
            Check("Malformed grant field is unavailable without poisoning usage: " + field, parsed.Resets is null && parsed.SevenDay?.Remaining == 85);
        }
        root = JsonNode.Parse(payload)!;
        root["cedar_ember"]!["grants"]![0]!["ends_at"] = now.AddDays(-2).ToString("O");
        Check("Reversed reset validity window is rejected", ClaudeUsageClient.Parse(root.ToJsonString(), now).Resets is null);
        var duplicateGrant = payload.Replace("\"resets_left\":2", "\"resets_left\":2,\"resets_left\":2");
        var duplicateStatus = payload.Replace("\"cedar_ember\":", "\"cedar_ember\":null,\"cedar_ember\":");
        Check("Duplicate inventory fields are unavailable independently of usage", ClaudeUsageClient.Parse(duplicateGrant, now).Resets is null &&
            ClaudeUsageClient.Parse(duplicateStatus, now).Resets is null);
        root = JsonNode.Parse(payload)!;
        ((JsonObject)root["cedar_ember"]!["grants"]![0]!).Remove("usable_now");
        Check("Omitted redemption status is unknown rather than false", ClaudeUsageClient.Parse(root.ToJsonString(), now).Resets!.Grants.Single().UsableNow is null);
        root["cedar_ember"]!["grants"]![0]!["usable_now"] = null;
        Check("Null redemption status preserves independently known credit count", ClaudeUsageClient.Parse(root.ToJsonString(), now).Resets!.Grants.Single() is { Remaining: 2, UsableNow: null });
        foreach (var (scopes, kind) in new[] { (new[] { "five_hour" }, "5-hour reset"), (new[] { "seven_day" }, "Weekly reset"),
            (new[] { "unknown_future_pool" }, "Other reset") })
        {
            root = JsonNode.Parse(payload)!;
            root["cedar_ember"]!["grants"]![0]!["clears"] = JsonSerializer.SerializeToNode(scopes);
            Check("Reset scope remains explicit: " + kind, ClaudeUsageClient.Parse(root.ToJsonString(), now).Resets!.Grants.Single().Kind == kind);
        }
        root = JsonNode.Parse(payload)!;
        var originalGrant = root["cedar_ember"]!["grants"]![0]!.DeepClone();
        root["cedar_ember"]!["grants"] = new JsonArray(Enumerable.Range(0, 201).Select(_ => originalGrant.DeepClone()).ToArray());
        Check("Oversized grant lists are rejected", ClaudeUsageClient.Parse(root.ToJsonString(), now).Resets is null);
        originalGrant["resets_left"] = 1000;
        root["cedar_ember"]!["grants"] = new JsonArray(Enumerable.Range(0, 11).Select(_ => originalGrant.DeepClone()).ToArray());
        Check("Implausible aggregate credit counts are rejected", ClaudeUsageClient.Parse(root.ToJsonString(), now).Resets is null);
        var inventory = new ClaudeResetInventory(new[] { grant, grant with { ExpiresAt = now.AddHours(1), UsableNow = false },
            grant with { Paused = true }, grant with { StartsAt = now.AddHours(1) }, grant with { ExpiresAt = now },
            grant with { Remaining = 0 }, grant with { ExpiresAt = null } });
        Check("Paused, future, expired and spent credits are excluded; nearest expiry sorts first", inventory.Available(now).Count == 3 &&
            inventory.Available(now)[0].ExpiresAt == now.AddHours(1) && inventory.Available(now)[^1].ExpiresAt is null);
        Check("Owned credit remains visible when it cannot currently be redeemed", inventory.Available(now)[0].UsableNow == false);
        Check("Credit disappears exactly at its expiry", !grant.Available(grant.ExpiresAt!.Value));

        foreach (var rejected in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Forbidden })
        {
            var urls = new List<string>();
            var headerOk = true;
            using var handler = new ResetAnnouncementChecks.Handler((request, _) =>
            {
                urls.Add(request.RequestUri!.AbsoluteUri);
                headerOk &= request.Method == HttpMethod.Get && request.Content is null && request.Headers.CacheControl is { NoCache: true, NoStore: true } &&
                    request.Headers.Authorization?.Parameter == "fixture-token" && request.Headers.GetValues("anthropic-beta").Single() == "oauth-2025-04-20";
                headerOk &= urls.Count == 1 ? request.Headers.UserAgent.ToString() == "claude-cli/2.1.293 (external, cli)" :
                    request.Headers.UserAgent.ToString().StartsWith("CodexBar-MajorCommand/");
                return Task.FromResult(new HttpResponseMessage(urls.Count == 1 ? rejected : HttpStatusCode.OK)
                    { Content = new StringContent(urls.Count == 1 ? "Optional query unsupported" : ClaudeFixture(now)) });
            });
            using var client = new ClaudeUsageClient(handler, () => ClaudeCredential(now), installedVersion: () => "2.1.293");
            await client.RefreshAsync();
            Check("Optional inventory rejection falls back once to ordinary account usage: " + rejected, headerOk &&
                urls.SequenceEqual(new[] { ClaudeUsageClient.UsageUrl + "?cedar_ember=1", ClaudeUsageClient.UsageUrl }) &&
                client.Current is { Error: null, FiveHour.Remaining: 66, Resets: null });
        }
        foreach (var (status, body) in new[] { (HttpStatusCode.Forbidden, "Requires user:profile"),
            (HttpStatusCode.ServiceUnavailable, "PRIVATE server error"), ((HttpStatusCode)429, "rate limit"),
            (HttpStatusCode.BadRequest, new string('x', 65537)) })
        {
            using var handler = new ResetAnnouncementChecks.Handler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));
            using var client = new ClaudeUsageClient(handler, () => ClaudeCredential(now), installedVersion: () => "2.1.293");
            await client.RefreshAsync();
            Check("Auth, server, throttle and oversized rejection are not hidden by fallback", handler.Calls == 1 && client.Current?.Error is not null && !client.Current.Error.Contains("PRIVATE"));
        }
        foreach (var version in new[] { null, "unknown", "2.1.293\nunsafe" })
        {
            var ordinary = false;
            using var handler = new ResetAnnouncementChecks.Handler((request, _) =>
            {
                ordinary = request.RequestUri!.AbsoluteUri == ClaudeUsageClient.UsageUrl;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaudeFixture(now)) });
            });
            using var client = new ClaudeUsageClient(handler, () => ClaudeCredential(now), installedVersion: () => version);
            await client.RefreshAsync();
            Check("Unknown native version leaves ordinary usage working", ordinary && client.Current?.Error is null);
        }
        var clock = now;
        var credentials = ClaudeCredential(now);
        var statusCode = HttpStatusCode.OK;
        using var successfulHandler = new ResetAnnouncementChecks.Handler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(payload) }));
        using var successful = new ClaudeUsageClient(successfulHandler, () => credentials, () => clock, installedVersion: () => "2.1.293");
        await successful.RefreshAsync();
        Check("Actual client consumes enriched reset inventory", successful.Current?.Resets?.Available(now).Single().Remaining == 2);
        clock = now.AddMinutes(5); statusCode = HttpStatusCode.ServiceUnavailable;
        await successful.RefreshAsync();
        Check("Temporary failure retains credits with a stale timestamp", successful.Current is { Error: not null, Resets: not null } && successful.Current.ReceivedAt == now);
        clock = now.AddMinutes(10); credentials = ClaudeCredential(clock, "another-account");
        await successful.RefreshAsync();
        Check("Changed login clears previous account's credits", successful.Current is { Error: not null, Resets: null, FiveHour: null });

        foreach (var fallbackFirst in new[] { false, true })
        {
            var credential = ClaudeCredential(now).Replace("\"accessToken\"", "\"refreshToken\":\"fixture-refresh\",\"accessToken\"");
            var renewals = 0;
            var calls = 0;
            using var handler = new ResetAnnouncementChecks.Handler((request, _) =>
            {
                calls++;
                var response = fallbackFirst && calls == 1 ? HttpStatusCode.BadRequest : calls == (fallbackFirst ? 2 : 1) ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
                if (response == HttpStatusCode.OK) Check("Renewed request uses reread credential", request.Headers.Authorization?.Parameter == "renewed-token");
                return Task.FromResult(new HttpResponseMessage(response) { Content = new StringContent(response == HttpStatusCode.OK ? payload : "optional query rejected") });
            });
            using var client = new ClaudeUsageClient(handler, () => credential, () => now, _ =>
            {
                renewals++; credential = ClaudeCredential(now, "renewed-token"); return Task.FromResult(true);
            }, () => "2.1.293");
            await client.RefreshAsync();
            Check("Inventory and fallback preserve the single native renewal retry", renewals == 1 && calls == (fallbackFirst ? 3 : 2) && client.Current is { Error: null, Resets: not null });
        }
        using var cancel = new CancellationTokenSource();
        using var cancellationHandler = new ResetAnnouncementChecks.Handler((_, ct) =>
        {
            cancel.Cancel(); ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        });
        using var cancelledClient = new ClaudeUsageClient(cancellationHandler, () => ClaudeCredential(now), installedVersion: () => "2.1.293");
        var cancelled = false;
        try { await cancelledClient.RefreshAsync(cancellationToken: cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check("Enriched read still cancels without a fallback request", cancelled && cancellationHandler.Calls == 1);
    }

    private static void CheckClaudeResetForm(string? output)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = ClaudeResetFixture(now);
        using var client = new ClaudeUsageClient(new ResetAnnouncementChecks.Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) })), () => ClaudeCredential(now), installedVersion: () => "2.1.293");
        using var widget = new WidgetForm(new AppSettings { X = 100, Y = 100, AlwaysOnTop = false }, claudeUsageClient: client);
        ((NotifyIcon)Get(widget, "trayIcon")!).Visible = false;
        InitializeForm(widget);
        Set(widget, "showClaude", true); Set(widget, "showClaudeResets", true);
        ((Task)Invoke(widget, "RefreshClaudeAsync", false)!).GetAwaiter().GetResult();
        var reading = (ClaudeReading)Get(widget, "claudeReading")!;
        Check("Actual reset form refresh sizes and consumes the inventory", widget.Height == (int)Math.Round(292 * widget.DeviceDpi / 96f) && reading.Resets?.Grants.Count == 1);
        Save(widget, output, "claude-resets");
        var bounds = widget.Bounds;
        Click(widget, 215, 17);
        Check("Claude resets collapse toward the bottom", (bool)Get(widget, "barOnly")! && widget.Top == bounds.Top + (int)Math.Round((292 - 17) * widget.DeviceDpi / 96f));
        Click(widget, 40, 4);
        Check("Claude resets restore their original position and page", widget.Bounds == bounds && (bool)Get(widget, "showClaudeResets")!);
        var scale = widget.DeviceDpi / 96f;
        var link = new Point((int)(70 * scale), widget.Height - (int)(22 * scale));
        Check("Manual redemption link is confined to its own hit area", (bool)Invoke(widget, "IsClaudeUsageLink", link)! &&
            !(bool)Invoke(widget, "IsClaudeUsageLink", new Point((int)(240 * scale), link.Y))!);
        // Release outside the link: neither open the browser nor switch faces.
        Invoke(widget, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, link.X, link.Y, 0));
        Invoke(widget, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, link.X, link.Y - (int)(30 * scale), 0));
        Check("Cancelled link press does not cycle pages", (bool)Get(widget, "showClaudeResets")!);
        foreach (var (name, value) in new[] { ("no-credits", reading with { Resets = new(Array.Empty<ClaudeResetGrant>()) }),
            ("credits-unavailable", reading with { Resets = null }), ("stale-resets", reading with { Error = "Claude unavailable; will retry." }),
            ("no-expiry", reading with { Resets = new(new[] { reading.Resets!.Grants.Single() with { ExpiresAt = null, UsableNow = false } }) }),
            ("expired-credits", reading with { Resets = new(new[] { reading.Resets!.Grants.Single() with { ExpiresAt = now } }) }) })
        {
            Set(widget, "claudeReading", value); Invoke(widget, "UpdateFaceSize"); Save(widget, output, "claude-" + name);
        }
        var many = reading with { Resets = new(Enumerable.Range(0, 50).Select(i => reading.Resets!.Grants.Single() with { ExpiresAt = now.AddDays(i + 1) }).ToArray()) };
        Set(widget, "claudeReading", many); Invoke(widget, "UpdateFaceSize");
        Check("Long Claude reset inventory stays within screen height", widget.Height <= Screen.FromControl(widget).WorkingArea.Height);
        Invoke(widget, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 40, 150, -120));
        Check("Long Claude credit lists scroll without changing Codex's offset", (int)Get(widget, "firstVisibleClaudeReset")! == 1 && (int)Get(widget, "firstVisibleExpiration")! == 0);
        Save(widget, output, "claude-long-credits");
        Set(widget, "claudeReading", reading); Invoke(widget, "UpdateFaceSize"); Save(widget, output, "claude-shrunk-credits");
        Check("Shrinking credit list clamps its scroll offset during rendering", (int)Get(widget, "firstVisibleClaudeReset")! == 0);
        Invoke(widget, "ToggleFace");
        var screen = Screen.FromControl(widget).WorkingArea;
        widget.Location = new Point(screen.Right - widget.Width, screen.Bottom - widget.Height);
        var edge = widget.Bounds;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            Click(widget, 40, 70);
            var codexDetailsBottom = widget.Bottom;
            Click(widget, 40, 70);
            Check("Claude usage restores the compact position and bottom alignment at screen edge", widget.Bounds == edge && widget.Bottom == codexDetailsBottom);
            var claudeCompact = widget.Bounds;
            Invoke(widget, "UpdateFaceSize");
            Check("Claude usage refresh preserves its restored bottom alignment", widget.Bounds == claudeCompact);
            Click(widget, 40, 70);
            Check("Claude resets fit inward at the screen edge", (bool)Get(widget, "showClaudeResets")! && screen.Contains(widget.Bounds));
            Click(widget, 40, 70);
        }
        Check("Four-page mouse cycles at screen edge restore the compact anchor", widget.Bounds == edge && !(bool)Get(widget, "showClaude")!);
    }
}
