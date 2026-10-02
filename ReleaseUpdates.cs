using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexBar;

// This edition publishes stable x.y.z and x.y.z-beta.n versions.
internal sealed record EditionVersion(int Major, int Minor, int Patch, int? Beta) : IComparable<EditionVersion>
{
    public static EditionVersion Current => Parse(typeof(EditionVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0])
        ?? throw new InvalidOperationException("Invalid MajorCommand edition version.");

    public static EditionVersion? Parse(string? text)
    {
        if (text is null || text.Length > 60) return null;
        var match = Regex.Match(text, @"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-beta\.(0|[1-9][0-9]*))?\z",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var values = new int[4];
        for (var i = 1; i <= 4; i++)
            if (match.Groups[i].Success && !int.TryParse(match.Groups[i].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out values[i - 1])) return null;
        return new(values[0], values[1], values[2], match.Groups[4].Success ? values[3] : null);
    }

    public int CompareTo(EditionVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (Beta is null) return other.Beta is null ? 0 : 1;
        return other.Beta is null ? -1 : Beta.Value.CompareTo(other.Beta.Value);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Beta is { } beta ? $"-beta.{beta}" : "");
}

internal sealed record ReleaseUpdate(EditionVersion Version, string PageUrl);
internal sealed class UpdateReadException(string message) : Exception(message);

internal sealed class ReleaseUpdateClient : IDisposable
{
    public const string ReleasesUrl = "https://github.com/majorcommand/codex-bar/releases";
    private const string Endpoint = "https://api.github.com/repos/majorcommand/codex-bar/releases?per_page=30";
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan timeout;
    private string? cachedJson;
    private string? etag;
    private DateTimeOffset retryAt;

    public ReleaseUpdateClient(HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null,
        TimeSpan? timeout = null)
    {
        http = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CodexBar-MajorCommand/" + EditionVersion.Current);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<ReleaseUpdate?> ReadAsync(EditionVersion current, CancellationToken cancellationToken = default)
    {
        var now = clock();
        if (now < retryAt) throw new UpdateReadException("GitHub requested a retry delay.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        cancellationToken = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return Select(cachedJson ?? throw new UpdateReadException("No cached GitHub release data."), current);
        if ((int)response.StatusCode is 403 or 429 or 503)
        {
            var retry = response.Headers.RetryAfter;
            var wait = retry?.Delta ?? (retry?.Date is { } date ? date - now : TimeSpan.FromMinutes(15));
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
                long.TryParse(resetValues.FirstOrDefault(), out var reset) && reset is >= 0 and <= 253402300799)
            {
                var resetWait = DateTimeOffset.FromUnixTimeSeconds(reset) - now;
                if (resetWait > wait) wait = resetWait;
            }
            retryAt = now + TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, 7 * 86400));
            throw new UpdateReadException("GitHub temporarily unavailable; retry later.");
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/vnd.github+json"))
            throw new UpdateReadException("Unexpected GitHub response type.");
        const int maxBytes = 512 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes) throw new UpdateReadException("GitHub release response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int length;
        while ((length = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + length > maxBytes) throw new UpdateReadException("GitHub release response too large.");
            buffer.Write(chunk, 0, length);
        }
        var json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        var result = Select(json, current);
        cachedJson = json;
        etag = response.Headers.ETag?.ToString();
        return result;
    }

    internal static ReleaseUpdate? Select(string json, EditionVersion current)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new UpdateReadException("Invalid GitHub release list.");
        ReleaseUpdate? newest = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString();
            var version = EditionVersion.Parse(tag);
            if (version is null) continue; // Other tag formats are not releases for this edition.
            var prerelease = release.GetProperty("prerelease").GetBoolean();
            if (prerelease != (version.Beta is not null) || current.Beta is null && prerelease) continue;
            if (version.CompareTo(current) <= 0 || newest is not null && version.CompareTo(newest.Version) <= 0) continue;
            var page = $"{ReleasesUrl}/tag/{tag}";
            if (release.GetProperty("html_url").GetString() != page ||
                !DateTimeOffset.TryParse(release.GetProperty("published_at").GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _)) throw new UpdateReadException("Invalid published release metadata.");
            // Announce only downloadable Windows x64 releases from our repository.
            var assets = release.GetProperty("assets");
            if (assets.ValueKind != JsonValueKind.Array) throw new UpdateReadException("Invalid release assets.");
            var downloadable = assets.EnumerateArray().Any(asset =>
                asset.GetProperty("name").GetString() is "CodexBar.exe" or "CodexBar-MajorCommand-win-x64-portable.zip" &&
                asset.GetProperty("state").GetString() == "uploaded" &&
                asset.GetProperty("browser_download_url").GetString() ==
                    $"https://github.com/majorcommand/codex-bar/releases/download/{tag}/{asset.GetProperty("name").GetString()}");
            if (downloadable) newest = new(version, page);
        }
        return newest;
    }

    public void Dispose() => http.Dispose();
}
