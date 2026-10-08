using System.ComponentModel;
using System.Diagnostics;

namespace CodexBar;

// Delegate renewal to the owner of the shared credential, including Desktop's bundled CLI.
internal static class ClaudeCodeRenewal
{
    internal static string? InstalledVersion()
    {
        try
        {
            var executable = FindExecutable();
            if (executable is null) return null;
            var text = FileVersionInfo.GetVersionInfo(executable).ProductVersion;
            if (!Version.TryParse(text, out var version) || version.Build < 0) return null;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            return null;
        }
    }

    internal static string? FindExecutable(string? userProfile = null, string? roamingData = null, string? path = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roamingData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        path ??= Environment.GetEnvironmentVariable("PATH");
        var standalone = Path.Combine(userProfile, ".local", "bin", "claude.exe");
        if (File.Exists(standalone)) return standalone;
        foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = directory.Trim().Trim('"');
            // WindowsApps aliases can launch the Desktop UI instead of Claude Code.
            if (clean.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(clean)) continue;
            var candidate = Path.Combine(clean, "claude.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var bundled = Path.Combine(roamingData, "Claude", "claude-code");
        if (!Directory.Exists(bundled)) return null;
        // Desktop updates put the CLI in a version/payload directory; do not pin yesterday's version.
        foreach (var version in Directory.EnumerateDirectories(bundled)
            .Select(p => (Path: p, Version: Version.TryParse(Path.GetFileName(p), out var v) ? v : null))
            .Where(p => p.Version is not null).OrderByDescending(p => p.Version))
        {
            var candidates = Directory.EnumerateDirectories(version.Path)
                .Select(p => Path.Combine(p, "claude.exe")).Where(File.Exists).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            // Never guess between different payloads of one installed version.
            if (candidates.Length == 1) return candidates[0];
        }
        return null;
    }

    internal static ProcessStartInfo StartInfo(string executable, string directory)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = directory
        };
        foreach (var argument in new[] { "-p", "/status", "--no-session-persistence", "--strict-mcp-config",
            "--settings", "{\"remoteControlAtStartup\":false}", "--tools", "" }) info.ArgumentList.Add(argument);
        return info;
    }

    public static async Task<bool> TryRenewAsync(CancellationToken cancellationToken) =>
        await RunAsync(() => FindExecutable(), cancellationToken).ConfigureAwait(false);

    internal static async Task<bool> RunAsync(Func<string?> executable, CancellationToken cancellationToken,
        TimeSpan? timeout = null, string? directory = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(25));
        using var process = new Process();
        try
        {
            var file = executable();
            if (file is null) return false;
            directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar", "ClaudeProbe");
            Directory.CreateDirectory(directory);
            process.StartInfo = StartInfo(file, directory);
            if (!process.Start()) return false;
            process.StandardInput.Close();
            // Discard bounded output. Do not retain login metadata, server bodies or tokens in logs.
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token),
                DrainAsync(process.StandardOutput, deadline.Token), DrainAsync(process.StandardError, deadline.Token)).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > 65536) throw new InvalidDataException();
        }
    }
}
