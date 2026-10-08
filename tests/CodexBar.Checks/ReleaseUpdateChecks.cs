using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexBar;

internal static class ReleaseUpdateChecks
{
    private static readonly EditionVersion Beta = EditionVersion.Parse("1.2.0-beta.2")!;
    internal static object Release(string tag, bool? prerelease = null, bool draft = false, bool assets = true,
        string? page = null) => new
    {
        tag_name = tag, draft, prerelease = prerelease ?? tag.Contains("-beta."),
        html_url = page ?? $"{ReleaseUpdateClient.ReleasesUrl}/tag/{tag}",
        published_at = "2026-10-02T09:00:00Z",
        assets = assets ? new[] { new { name = "CodexBar.exe", state = "uploaded",
            browser_download_url = $"https://github.com/majorcommand/codex-bar/releases/download/{tag}/CodexBar.exe" } } : []
    };
    internal static string Releases(params object[] releases) => JsonSerializer.Serialize(releases);

    internal static void Run(Action<string, bool> check)
    {
        check("Edition metadata identifies the stable release", EditionVersion.Current == EditionVersion.Parse("1.3.0"));
        foreach (var invalid in new[] { "", "v1", "1.2", "1.2.3-rc.1", "01.2.3", "1.2.3-beta.01", "1.2.3+source", "999999999999.2.3", "1.2.3\n", "../evil" })
            check("Reject invalid edition version " + JsonSerializer.Serialize(invalid), EditionVersion.Parse(invalid) is null);
        check("Version ordering uses numeric components", EditionVersion.Parse("1.10.0")!.CompareTo(EditionVersion.Parse("1.9.9")) > 0 &&
            EditionVersion.Parse("1.2.0-beta.10")!.CompareTo(EditionVersion.Parse("1.2.0-beta.3")) > 0);
        check("Stable release follows its beta", EditionVersion.Parse("1.2.0")!.CompareTo(Beta) > 0 &&
            EditionVersion.Parse("1.3.0-beta.1")!.CompareTo(EditionVersion.Parse("1.2.0")) > 0);
        check("No releases and current release show no update", ReleaseUpdateClient.Select("[]", Beta) is null &&
            ReleaseUpdateClient.Select(Releases(Release("v1.2.0-beta.2")), Beta) is null);
        check("Beta users receive next beta even when stable latest is older",
            ReleaseUpdateClient.Select(Releases(Release("v1.1.1"), Release("v1.2.0-beta.3")), Beta)?.Version == EditionVersion.Parse("1.2.0-beta.3"));
        check("Stable users ignore beta releases", ReleaseUpdateClient.Select(Releases(Release("v1.3.0-beta.1")), EditionVersion.Parse("1.2.0")!) is null);
        check("Beta users receive stable releases", ReleaseUpdateClient.Select(Releases(Release("v1.2.0")), Beta)?.Version == EditionVersion.Parse("1.2.0"));
        check("Selection chooses highest version rather than API ordering", ReleaseUpdateClient.Select(Releases(
            Release("v1.2.0-beta.10"), Release("v1.2.0-beta.3")), Beta)?.Version == EditionVersion.Parse("1.2.0-beta.10"));
        check("Drafts, mismatched channels and source-only releases are excluded", ReleaseUpdateClient.Select(Releases(
            Release("v1.3.0", draft: true), Release("v1.4.0", prerelease: true), Release("v1.5.0", assets: false)), Beta) is null);
        check("External download pages fail closed", FailsSync(() => ReleaseUpdateClient.Select(
            Releases(Release("v1.2.0-beta.3", page: "https://example.com/evil")), Beta)));
        check("Malformed release metadata is unavailable", FailsSync(() => ReleaseUpdateClient.Select("{}", Beta)) &&
            FailsSync(() => ReleaseUpdateClient.Select("[{}]", Beta)));
        Task.Run(() => ClientChecks(check)).GetAwaiter().GetResult();
    }

    private static async Task ClientChecks(Action<string, bool> check)
    {
        var count = 0;
        var etagSent = false;
        var safeRequest = false;
        using var handler = new ResetAnnouncementChecks.Handler((request, _) =>
        {
            safeRequest = request.RequestUri!.Host == "api.github.com" && request.Headers.Authorization is null &&
                request.Headers.UserAgent.ToString().Contains("CodexBar-MajorCommand/") && request.Method == HttpMethod.Get;
            if (++count > 1)
            {
                etagSent = request.Headers.IfNoneMatch.Any(value => value.Tag == "\"release-fixture\"");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }
            var response = ResetAnnouncementChecks.JsonResponse(Releases(Release("v1.2.0-beta.3")));
            response.Headers.ETag = new EntityTagHeaderValue("\"release-fixture\"");
            return Task.FromResult(response);
        });
        using var client = new ReleaseUpdateClient(handler);
        var first = await client.ReadAsync(Beta);
        check("Real update client makes public, credential-free requests", safeRequest && first is not null);
        check("Conditional release checks reuse validated data", await client.ReadAsync(Beta) == first && etagSent);
        var now = DateTimeOffset.UtcNow;
        var retryCalls = 0;
        using var throttled = new ReleaseUpdateClient(new ResetAnnouncementChecks.Handler((_, _) =>
        {
            if (++retryCalls > 1) return Task.FromResult(ResetAnnouncementChecks.JsonResponse("[]"));
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Reset", now.AddHours(1).ToUnixTimeSeconds().ToString());
            return Task.FromResult(response);
        }), () => now);
        check("Rate-limit errors are observable and immediate retries blocked", await Fails(() => throttled.ReadAsync(Beta)) &&
            await Fails(() => throttled.ReadAsync(Beta)) && retryCalls == 1);
        now = now.AddMinutes(61);
        check("Update check recovers after rate-limit reset", await throttled.ReadAsync(Beta) is null && retryCalls == 2);
        foreach (var factory in new Func<HttpResponseMessage>[]
        {
            () => new(HttpStatusCode.NotModified),
            () => new(HttpStatusCode.ServiceUnavailable),
            () => new(HttpStatusCode.OK) { Content = new StringContent("<html>error</html>") },
            () => ResetAnnouncementChecks.JsonResponse("broken"),
            () => ResetAnnouncementChecks.JsonResponse(new string('x', 512 * 1024 + 1)),
            () => new(HttpStatusCode.OK) { Content = new UnboundedContent() }
        })
        {
            using var failed = new ReleaseUpdateClient(new ResetAnnouncementChecks.Handler((_, _) => Task.FromResult(factory())));
            check("Invalid, offline or oversized update responses fail clearly", await Fails(() => failed.ReadAsync(Beta)));
        }
        using var slow = new ReleaseUpdateClient(new ResetAnnouncementChecks.Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return ResetAnnouncementChecks.JsonResponse("[]");
        }), timeout: TimeSpan.FromMilliseconds(40));
        check("Hung update requests have a deadline", await Fails(() => slow.ReadAsync(Beta)));
        using var cancellation = new CancellationTokenSource();
        using var cancelled = new ReleaseUpdateClient(new ResetAnnouncementChecks.Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return ResetAnnouncementChecks.JsonResponse("[]");
        }));
        var read = cancelled.ReadAsync(Beta, cancellation.Token);
        cancellation.Cancel();
        check("Exit cancels pending update requests", await Fails(() => read));
    }

    private sealed class UnboundedContent : HttpContent
    {
        public UnboundedContent() => Headers.ContentType = new MediaTypeHeaderValue("application/json");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[512 * 1024 + 1]).AsTask();
    }
    private static bool FailsSync(Action action)
    {
        try { action(); return false; }
        catch (Exception ex) when (ex is UpdateReadException or JsonException or KeyNotFoundException or InvalidOperationException) { return true; }
    }
    private static async Task<bool> Fails(Func<Task<ReleaseUpdate?>> action)
    {
        try { await action(); return false; }
        catch (Exception ex) when (ex is UpdateReadException or HttpRequestException or JsonException or OperationCanceledException) { return true; }
    }
}
