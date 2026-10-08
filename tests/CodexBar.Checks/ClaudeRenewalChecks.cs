using System.Net;
using System.Text.Json;
using CodexBar;

internal static partial class Checks
{
    private static string RenewableClaudeCredential(DateTimeOffset expiry, string token = "old-token") =>
        JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = token, refreshToken = "fixture-renewal",
            expiresAt = expiry.ToUnixTimeMilliseconds(), scopes = new[] { "user:profile" } } });

    private static bool RunClaudeRenewalFixture(string[] args)
    {
        var mode = Environment.GetEnvironmentVariable("CODEXBAR_CHECK_RENEWAL_MODE");
        if (mode is null || args.FirstOrDefault() != "-p") return false;
        var capture = Environment.GetEnvironmentVariable("CODEXBAR_CHECK_RENEWAL_CAPTURE")!;
        File.WriteAllText(capture, JsonSerializer.Serialize(new { args, pid = Environment.ProcessId,
            config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") }));
        if (mode == "hang") Thread.Sleep(Timeout.Infinite);
        if (mode == "large") Console.Write(new string('x', 70000));
        else { Console.Write("discarded fixture output"); Console.Error.Write("discarded fixture error"); }
        Environment.ExitCode = mode == "fail" ? 1 : 0;
        return true;
    }

    private static async Task CheckClaudeRenewalAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = now;
        var credential = RenewableClaudeCredential(now.AddHours(-1));
        var renewals = 0;
        var tokens = new List<string?>();
        using var handler = new ResetAnnouncementChecks.Handler((request, _) =>
        {
            tokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaudeFixture(clock)) });
        });
        Task<bool> Renew(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            renewals++;
            credential = RenewableClaudeCredential(clock.AddHours(8), "renewed-" + renewals);
            return Task.FromResult(true);
        }
        using var client = new ClaudeUsageClient(handler, () => credential, () => clock, Renew);
        await client.RefreshAsync();
        Check("Expired Claude login renews before usage, then uses the fresh native token", renewals == 1 && tokens.SequenceEqual(new[] { "renewed-1" }) && client.Current?.Error is null);
        await client.RefreshAsync(manual: true);
        Check("Manual refresh cannot repeatedly launch native renewal", renewals == 1 && handler.Calls == 1);
        clock = now.AddHours(8);
        await client.RefreshAsync();
        Check("Claude renewal works across repeated eight-hour expiries", renewals == 2 && tokens.Last() == "renewed-2" && client.Current?.Error is null);
        using var restarted = new ClaudeUsageClient(new ResetAnnouncementChecks.Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaudeFixture(clock)) })), () => credential, () => clock, Renew);
        await restarted.RefreshAsync();
        Check("Restart adopts the credential persisted by Claude without another renewal", renewals == 2 && restarted.Current?.Error is null);

        clock = now;
        credential = RenewableClaudeCredential(now.AddMinutes(5));
        using var proactive = new ClaudeUsageClient(new ResetAnnouncementChecks.Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaudeFixture(now)) })), () => credential, () => clock, Renew);
        await proactive.RefreshAsync();
        Check("Claude attempts renewal within five minutes of expiry", renewals == 3 && proactive.Current?.Error is null);

        credential = RenewableClaudeCredential(now.AddHours(8));
        var authRenewals = 0;
        using var authHandler = new ResetAnnouncementChecks.Handler((request, _) => Task.FromResult(new HttpResponseMessage(
            request.Headers.Authorization?.Parameter == "old-token" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            { Content = new StringContent(ClaudeFixture(now)) }));
        using var rejected = new ClaudeUsageClient(authHandler, () => credential, () => clock, _ =>
        {
            authRenewals++;
            credential = RenewableClaudeCredential(now.AddHours(8), "recovered-token");
            return Task.FromResult(true);
        });
        await rejected.RefreshAsync();
        Check("A 401 triggers one native renewal and a usage retry", authRenewals == 1 && authHandler.Calls == 2 && rejected.Current?.Error is null);

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, (HttpStatusCode)429 })
        {
            var count = 0;
            using var failedHandler = new ResetAnnouncementChecks.Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
            using var failed = new ClaudeUsageClient(failedHandler, () => credential, () => clock, _ => { count++; return Task.FromResult(true); });
            await failed.RefreshAsync();
            Check("Auth retry is bounded and forbidden/rate-limited usage never launches renewal", count == (status == HttpStatusCode.Unauthorized ? 1 : 0) &&
                failedHandler.Calls == (status == HttpStatusCode.Unauthorized ? 2 : 1) && failed.Current is { Error: not null, FiveHour: null });
        }

        foreach (var after in new[] { RenewableClaudeCredential(now.AddHours(-1)), "{}", RenewableClaudeCredential(now.AddHours(8)).Replace("user:profile", "user:inference") })
        {
            var shared = RenewableClaudeCredential(now.AddHours(-1));
            var count = 0;
            using var neverRead = new ResetAnnouncementChecks.Handler((_, _) => throw new InvalidOperationException("Expired token reached HTTP."));
            using var failed = new ClaudeUsageClient(neverRead, () => shared, () => clock, _ => { shared = after; count++; return Task.FromResult(true); });
            await failed.RefreshAsync();
            await failed.RefreshAsync(manual: true);
            Check("Process success cannot hide unchanged expiry, invalid credentials or missing scope", count == 1 && neverRead.Calls == 0 && failed.Current is { Error: not null, FiveHour: null });
        }

        var failedRenewals = 0;
        credential = RenewableClaudeCredential(now.AddHours(-1));
        using var recovering = new ClaudeUsageClient(new ResetAnnouncementChecks.Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaudeFixture(clock)) })), () => credential, () => clock, _ =>
        {
            failedRenewals++;
            if (failedRenewals == 2) credential = RenewableClaudeCredential(clock.AddHours(8));
            return Task.FromResult(failedRenewals == 2);
        });
        await recovering.RefreshAsync(); clock = now.AddMinutes(5); await recovering.RefreshAsync();
        Check("Temporary renewal failure recovers on a later automatic refresh", failedRenewals == 2 && recovering.Current?.Error is null);

        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        credential = RenewableClaudeCredential(clock.AddHours(-1));
        var concurrentRenewals = 0;
        using var cancellable = new ClaudeUsageClient(credentials: () => credential, clock: () => clock, renewCredentials: async ct =>
        {
            concurrentRenewals++; entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return false;
        });
        var inFlight = cancellable.RefreshAsync(cancellationToken: cancellation.Token);
        await entered.Task;
        await cancellable.RefreshAsync(manual: true);
        cancellation.Cancel();
        var cancelled = false;
        try { await inFlight; } catch (OperationCanceledException) { cancelled = true; }
        Check("Concurrent reads share one renewal and shutdown cancels it", concurrentRenewals == 1 && cancelled);

        await CheckNativeClaudeRunnerAsync();
    }

    private static async Task CheckNativeClaudeRunnerAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codexbar-renewal-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var modeBefore = Environment.GetEnvironmentVariable("CODEXBAR_CHECK_RENEWAL_MODE");
        var captureBefore = Environment.GetEnvironmentVariable("CODEXBAR_CHECK_RENEWAL_CAPTURE");
        var configBefore = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            var old = Path.Combine(directory, "Claude", "claude-code", "2.1.9", "payload", "claude.exe");
            var latest = Path.Combine(directory, "Claude", "claude-code", "2.1.10", "payload", "claude.exe");
            foreach (var file in new[] { old, latest }) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "fixture"); }
            Check("Desktop CLI discovery follows the newest installed numeric version", ClaudeCodeRenewal.FindExecutable(directory, directory, "") == latest);
            Check("Unavailable Claude Code is reported without launching Desktop", ClaudeCodeRenewal.FindExecutable(directory, Path.Combine(directory, "absent"), "") is null);
            var standalone = Path.Combine(directory, ".local", "bin", "claude.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(standalone)!); File.WriteAllText(standalone, "fixture");
            Check("Standalone Claude Code takes precedence over bundled CLI", ClaudeCodeRenewal.FindExecutable(directory, directory, "") == standalone);
            var capture = Path.Combine(directory, "capture.json");
            Environment.SetEnvironmentVariable("CODEXBAR_CHECK_RENEWAL_CAPTURE", capture);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(directory, "profile with spaces"));
            foreach (var mode in new[] { "success", "fail", "large", "hang" })
            {
                Environment.SetEnvironmentVariable("CODEXBAR_CHECK_RENEWAL_MODE", mode);
                var result = await ClaudeCodeRenewal.RunAsync(() => Environment.ProcessPath, CancellationToken.None,
                    TimeSpan.FromSeconds(mode == "hang" ? 2 : 5), directory);
                Check("Actual renewal subprocess handles exit, bounded output and timeout: " + mode, result == (mode == "success"));
            }
            using var document = JsonDocument.Parse(File.ReadAllText(capture));
            var args = document.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
            Check("Native invocation uses no shell, fixed status arguments and inherited Claude profile", args.SequenceEqual(new[] {
                "-p", "/status", "--no-session-persistence", "--strict-mcp-config", "--settings", "{\"remoteControlAtStartup\":false}", "--tools", "" }) &&
                document.RootElement.GetProperty("config").GetString() == Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));
            var timeoutPid = document.RootElement.GetProperty("pid").GetInt32();
            Check("Timed-out renewal leaves no helper process running", FixtureExited(timeoutPid));
            File.Delete(capture);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var cancelled = false;
            try { await ClaudeCodeRenewal.RunAsync(() => Environment.ProcessPath, stop.Token, TimeSpan.FromSeconds(30), directory); }
            catch (OperationCanceledException) { cancelled = true; }
            using var stopped = JsonDocument.Parse(File.ReadAllText(capture));
            Check("Shutdown cancellation terminates the actual renewal helper", cancelled && FixtureExited(stopped.RootElement.GetProperty("pid").GetInt32()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXBAR_CHECK_RENEWAL_MODE", modeBefore);
            Environment.SetEnvironmentVariable("CODEXBAR_CHECK_RENEWAL_CAPTURE", captureBefore);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", configBefore);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool FixtureExited(int pid)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return process.WaitForExit(2000); }
        catch (ArgumentException) { return true; }
    }

    private static void CheckLiveClaudeRenewal(string? output)
    {
        // Exercise the client's expiry branch without changing native credentials to manufacture expiry.
        var initialRead = true;
        var nativeCalled = false;
        string Credentials()
        {
            var json = ClaudeUsageClient.ReadCredentials();
            if (!initialRead) return json;
            initialRead = false;
            var document = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            document["claudeAiOauth"]!["expiresAt"] = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
            return document.ToJsonString();
        }
        async Task<bool> Renew(CancellationToken ct)
        {
            nativeCalled = true;
            return await ClaudeCodeRenewal.TryRenewAsync(ct);
        }
        using var client = new ClaudeUsageClient(credentials: Credentials, renewCredentials: Renew);
        Task.Run(() => client.RefreshAsync()).GetAwaiter().GetResult();
        if (client.Current?.Error is { } error) Console.WriteLine("Live Claude renewal unavailable: " + error);
        Check("Live native renewal path rereads a valid credential and retrieves account usage", nativeCalled && client.Current is { Error: null });
        using var form = new WidgetForm(new AppSettings { AlwaysOnTop = false, X = 100, Y = 100 });
        ((NotifyIcon)Get(form, "trayIcon")!).Visible = false;
        InitializeForm(form);
        Set(form, "showClaude", true); Set(form, "claudeReading", client.Current); Invoke(form, "UpdateFaceSize");
        Save(form, output, "claude-live-renewal");
    }
}
