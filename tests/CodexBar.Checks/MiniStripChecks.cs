using System.Text.Json;
using CodexBar;

internal static partial class Checks
{
    private static void CheckMiniStrip(string? output)
    {
        var now = DateTimeOffset.UtcNow;
        var preferences = new AppSettings { X = 100, Y = 100, ExpandOnHover = true,
            AlwaysOnTop = false, ShowInTaskbar = true, NotifyOnUsageLimitReached = false };
        using var widget = new WidgetForm(preferences);
        foreach (var timer in new[] { "refreshTimer", "topmostTimer", "positionSaveTimer", "hoverTimer" })
            ((System.Windows.Forms.Timer)Get(widget, timer)!).Stop();
        ((NotifyIcon)Get(widget, "trayIcon")!).Visible = false;
        InitializeForm(widget);
        var scale = widget.DeviceDpi / 96f;
        var original = widget.Bounds;
        var menu = (ToolStripMenuItem)widget.ContextMenuStrip!.Items["miniStrip"]!;
        Check("Full widget initially follows taskbar-on preference", widget.ShowInTaskbar);
        Check("Mini strip is optional for existing preferences", !preferences.MiniStrip && !menu.Checked &&
            !JsonSerializer.Deserialize<AppSettings>("{}")!.MiniStrip);
        menu.PerformClick();
        Check("Mini strip menu opens a taskbar-height strip", widget.Size ==
            new Size((int)Math.Round(472 * scale), (int)Math.Round(40 * scale)) && menu.Checked && preferences.MiniStrip);
        Check("Mini mode hides taskbar button without changing the full-widget preference", !widget.ShowInTaskbar && preferences.ShowInTaskbar);
        Invoke(widget, "ApplyTaskbarVisibility", true);
        Check("Restoring from notification area cannot show a mini-mode taskbar button", !widget.ShowInTaskbar);
        var miniTray = (NotifyIcon)Get(widget, "trayIcon")!;
        miniTray.Visible = true;
        Invoke(widget, "ApplyTaskbarVisibility", true);
        Check("Mini mode preserves the notification icon and menu", miniTray.Visible && miniTray.ContextMenuStrip == widget.ContextMenuStrip);
        miniTray.Visible = false;
        Check("Mini strip stays anchored to the previous bottom edge", widget.Bottom == original.Bottom);
        Check("Mini strip has its own live countdown repaint timer", ((System.Windows.Forms.Timer)Get(widget, "miniStripTimer")!).Enabled);
        Save(widget, output, "mini-strip-unavailable");
        MiniStripValue Codex(DateTimeOffset? at = null) => (MiniStripValue)Invoke(widget, "CodexMiniValue", at ?? now)!;
        MiniStripValue Claude(ClaudeWindow? window, DateTimeOffset? at = null) =>
            (MiniStripValue)Invoke(widget, "ClaudeMiniValue", window!, at ?? now)!;
        Check("Missing mini-strip data is unavailable, never full", Codex().Percentage == "—" && Claude(null).Percentage == "—");

        var codex = new UsageSnapshot(28, now.AddDays(4), now, Array.Empty<DateTimeOffset>());
        var reading = new ClaudeReading(now, new(62, now.AddHours(2).AddMinutes(10)), new(39, now.AddDays(3)));
        Set(widget, "snapshot", codex);
        Set(widget, "liveConnected", true);
        Set(widget, "claudeReading", reading);
        Check("Mini strip shows allowance remaining and both independent windows", Codex() is { Percentage: "72%", Countdown: "4.0d" } &&
            Claude(reading.FiveHour) is { Percentage: "38%", Countdown: "2h 10m" } &&
            Claude(reading.SevenDay) is { Percentage: "61%", Countdown: "3.0d" });
        Check("Mini strip countdown advances without a new usage request", Claude(reading.FiveHour, now.AddMinutes(1)).Countdown == "2h 9m");
        Save(widget, output, "mini-strip-usage");
        var bounds = widget.Bounds;
        Invoke(widget, "UpdateFaceSize");
        Invoke(widget, "UpdatePace");
        Check("Usage refresh preserves mini strip geometry", widget.Bounds == bounds);
        EnterBody(widget); HoverTick(widget, true);
        Check("Mini strip never expands on hover", !(bool)Get(widget, "hoverExpanded")! && widget.Bounds == bounds);
        Invoke(widget, "SetAnnouncementTip", false);
        var tip = (string)Get(widget, "lastAnnouncementTip")!;
        var tooltip = (ToolTip)Get(widget, "announcementTip")!;
        Check("Mini mode disables and clears hover tooltips", tip == "" && !tooltip.Active && tooltip.GetToolTip(widget) == "");
        Check("Mini-strip right side has no old hide/close hit areas", (int)Invoke(widget, "WindowControlAt", new PointF(452, 18))! == -1);
        Click(widget, 452, 18);
        Check("Clicking the strip restores the same face and bounds", widget.Bounds == original && !menu.Checked && !preferences.MiniStrip);
        Invoke(widget, "ApplyTaskbarVisibility", true);
        Check("Full-widget restore recovers taskbar preference and hover support", widget.ShowInTaskbar && preferences.ShowInTaskbar && tooltip.Active);
        Check("Restoring the full widget stops mini-strip repainting", !((System.Windows.Forms.Timer)Get(widget, "miniStripTimer")!).Enabled);

        // Every full face must survive entering and leaving mini mode, including the tall details faces.
        for (var page = 0; page < 4; page++)
        {
            var faceBounds = widget.Bounds;
            var claude = (bool)Get(widget, "showClaude")!;
            var details = (bool)Get(widget, "showResetDetails")!;
            var claudeDetails = (bool)Get(widget, "showClaudeResets")!;
            for (var repeat = 0; repeat < 3; repeat++)
            {
                Invoke(widget, "SetMiniStrip", true);
                Invoke(widget, "OnKeyDown", new KeyEventArgs(Keys.Enter));
            }
            Check($"Mini strip restores full face {page + 1} without drift or navigation", widget.Bounds == faceBounds &&
                (bool)Get(widget, "showClaude")! == claude && (bool)Get(widget, "showResetDetails")! == details &&
                (bool)Get(widget, "showClaudeResets")! == claudeDetails);
            Invoke(widget, "ToggleFace");
        }
        Invoke(widget, "SetBarOnly", true);
        Invoke(widget, "SetMiniStrip", true);
        Check("Mini strip can open from the original progress strip", !(bool)Get(widget, "barOnly")! && menu.Checked);
        Invoke(widget, "SetBarOnly", true);
        Check("Original progress strip remains available from mini mode", (bool)Get(widget, "barOnly")! && !menu.Checked &&
            widget.Height == (int)Math.Round(8 * scale));
        Invoke(widget, "SetBarOnly", false);
        Invoke(widget, "SetMiniStrip", true);
        var area = Screen.FromControl(widget).WorkingArea;
        widget.Location = new Point(area.Right - widget.Width, area.Bottom - widget.Height);
        Invoke(widget, "UpdateFaceSize");
        Check("Mini strip remains stable at the bottom-right screen edge", widget.Right == area.Right && widget.Bottom == area.Bottom);
        var clamped = (Point)Invoke(widget, "KeepVisible", new Point(widget.Left, area.Bottom + 200))!;
        Check("Dragging mini strip below taskbar keeps all text vertically visible", clamped.Y == area.Bottom - widget.Height);

        Set(widget, "liveConnected", false);
        Set(widget, "claudeReading", reading with { Error = "Offline" });
        Check("Offline readings are explicitly last known and withhold countdowns", Codex() is { Percentage: "72%*", Countdown: "—" } &&
            Claude(reading.FiveHour) is { Percentage: "38%*", Countdown: "—" });
        Save(widget, output, "mini-strip-offline");
        Set(widget, "liveConnected", true);
        Set(widget, "snapshot", codex with { CapturedAt = now.AddMinutes(-10) });
        Set(widget, "claudeReading", reading with { ReceivedAt = now.AddMinutes(-10) });
        Check("Ten-minute-old readings lose live colour and countdowns", Codex().Percentage == "72%*" &&
            Claude(reading.SevenDay).Percentage == "61%*" && Codex().Color == Claude(reading.SevenDay).Color);
        Set(widget, "snapshot", codex with { ResetsAt = now });
        Set(widget, "claudeReading", reading);
        Check("Passed resets never invent a refill", Codex().Percentage == "—" && Claude(new(100, now)).Percentage == "—");
        Check("Known Claude allowance without reset time is still visible", Claude(new(20, null)) is { Percentage: "80%", Countdown: "—" });
        foreach (var used in new[] { double.NaN, double.PositiveInfinity, -1, 101 })
        {
            Set(widget, "snapshot", codex with { UsedPercent = used });
            Check("Invalid mini allowance is unavailable: " + used, Codex().Percentage == "—" && Claude(new(used, now.AddDays(1))).Percentage == "—");
        }
        Set(widget, "snapshot", codex with { UsedPercent = 0, ResetsAt = now.AddDays(7) });
        Set(widget, "claudeReading", new ClaudeReading(now, new(0, now.AddHours(5)), new(0, now.AddDays(7))));
        Save(widget, output, "mini-strip-full");
        Set(widget, "snapshot", codex with { UsedPercent = 0, ResetsAt = now.AddHours(23).AddMinutes(59) });
        Set(widget, "claudeReading", new ClaudeReading(now, new(0, now.AddHours(4).AddMinutes(59)), new(0, now.AddHours(23).AddMinutes(59))));
        Save(widget, output, "mini-strip-full-hours");
        Set(widget, "snapshot", codex with { UsedPercent = 100 });
        Set(widget, "claudeReading", new ClaudeReading(now, new(100, now.AddMinutes(1)), new(100, now.AddDays(7))));
        Check("Exhausted allowances remain zero with a real reset countdown", Codex().Percentage == "0%" && Claude(new(100, now.AddSeconds(1))) is
            { Percentage: "0%", Countdown: "1m" });
        Save(widget, output, "mini-strip-exhausted");
        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(preferences))!;
        using var restored = new WidgetForm(reloaded, localPreview: true);
        ((NotifyIcon)Get(restored, "trayIcon")!).Visible = false;
        InitializeForm(restored);
        Check("Saved mini mode is restored when opening the widget", (bool)Get(restored, "miniStrip")! && restored.Width == widget.Width);
        Check("Local preview disables startup and notification actions", restored.Text.EndsWith("LOCAL PREVIEW") &&
            !restored.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(i => i.Text!.StartsWith("Start with Windows")).Enabled &&
            !restored.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Send test alert").Enabled);
        Check("Local preview keeps Claude refresh running", ((System.Windows.Forms.Timer)Get(restored, "claudeTimer")!).Enabled);
        CheckMiniStripDpi(output);
    }

    private static void CheckMiniStripDpi(string? output)
    {
        // Deliver DPI notifications only to this isolated, unshown form; never change Windows display settings.
        using var widget = new WidgetForm(new AppSettings { MiniStrip = true, AlwaysOnTop = false,
            NotifyOnUsageLimitReached = false, X = 100, Y = 100 });
        ((NotifyIcon)Get(widget, "trayIcon")!).Visible = false;
        InitializeForm(widget);
        var now = DateTimeOffset.UtcNow;
        Set(widget, "snapshot", new UsageSnapshot(0, now.AddHours(23).AddMinutes(59), now, Array.Empty<DateTimeOffset>()));
        Set(widget, "liveConnected", true);
        Set(widget, "claudeReading", new ClaudeReading(now, new(0, now.AddHours(4).AddMinutes(59)), new(0, now.AddHours(23).AddMinutes(59))));
        var suggestedBounds = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try
        {
            foreach (var dpi in new[] { 144, 96 })
            {
                var width = (int)Math.Round(472 * dpi / 96f);
                var height = (int)Math.Round(40 * dpi / 96f);
                System.Runtime.InteropServices.Marshal.Copy(new[] { 100, 100, 100 + width, 100 + height }, 0, suggestedBounds, 4);
                var message = Message.Create(widget.Handle, 0x02E0, new IntPtr(dpi | dpi << 16), suggestedBounds);
                Invoke(widget, "WndProc", message);
                Check($"Mini strip handles a {dpi}-DPI notification with correct size and hidden taskbar", widget.DeviceDpi == dpi &&
                    widget.ClientSize == new Size(width, height) && !widget.ShowInTaskbar);
                Save(widget, output, $"mini-strip-dpi-{dpi}");
            }
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(suggestedBounds); }
    }
}
