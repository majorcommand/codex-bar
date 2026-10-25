using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CodexBar;

internal sealed class AppSettings
{
    public double Opacity { get; set; } = 0.9;
    public bool AlwaysOnTop { get; set; } = true;
    public bool ShowInTaskbar { get; set; }
    public bool ExpandOnHover { get; set; }
    public bool MiniStrip { get; set; }
    public AnnouncementPlacement ResetAnnouncementPlacement { get; set; } = AnnouncementPlacement.Bottom;
    public bool WindowsStartupInitialized { get; set; }
    public int RefreshSeconds { get; set; } = 15;
    public bool NotifyOnUsageLimitReached { get; set; } = true;
    public string? UsageLimitNotificationEmail { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUser { get; set; }
    [JsonIgnore]
    public string? SmtpPassword { get; set; }
    public string? ProtectedSmtpPassword { get; set; }
    public string? SmtpFromAddress { get; set; }
    public DateTimeOffset? LastUsageLimitNotificationResetAt { get; set; }
    public double? LastObservedWeeklyUsedPercent { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    [JsonIgnore]
    private string? lastProtectedPassword;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexBar", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

            if (!string.IsNullOrEmpty(settings.ProtectedSmtpPassword))
            {
                settings.SmtpPassword = Unprotect(settings.ProtectedSmtpPassword);
                if (settings.SmtpPassword is null)
                {
                    settings.ProtectedSmtpPassword = null;
                    settings.Save();
                }
                else
                {
                    settings.lastProtectedPassword = settings.SmtpPassword;
                }
            }
            else
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty(nameof(SmtpPassword), out var legacyPassword) &&
                    legacyPassword.ValueKind == JsonValueKind.String)
                {
                    settings.SmtpPassword = legacyPassword.GetString();
                    settings.Save();
                }
            }

            return settings;
        }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        string? temporaryPath = null;
        try
        {
            if (!string.Equals(SmtpPassword, lastProtectedPassword, StringComparison.Ordinal))
            {
                ProtectedSmtpPassword = string.IsNullOrEmpty(SmtpPassword) ? null : Protect(SmtpPassword);
                lastProtectedPassword = SmtpPassword;
            }

            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));

            if (File.Exists(SettingsPath))
                File.Replace(temporaryPath, SettingsPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, SettingsPath);
        }
        catch { /* A display preference should never crash the widget. */ }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string Protect(string value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(
                plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string? Unprotect(string value)
    {
        try
        {
            var protectedData = Convert.FromBase64String(value);
            var plaintext = ProtectedData.Unprotect(
                protectedData, optionalEntropy: null, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (FormatException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // Apply once so a later explicit Off choice is never reset on launch.
    public bool ApplyStartupDefault(Action enableStartup)
    {
        if (WindowsStartupInitialized) return false;
        enableStartup();
        WindowsStartupInitialized = true;
        return true;
    }

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("CodexBar") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value)
                key.SetValue("CodexBar", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("CodexBar", false);
        }
    }
}
