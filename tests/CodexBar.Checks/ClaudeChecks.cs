using System.Text.Json;
using CodexBar;

internal static partial class Checks
{
    // Keep the existing Codex layout/hover checks focused on their two original faces.
    // The third face and complete navigation cycle are exercised separately below.
    private static void NextCodexFace(WidgetForm form)
    {
        Invoke(form, "ToggleFace");
        if ((bool)Get(form, "showClaude")!) Invoke(form, "ToggleFace");
    }

    private static void CheckClaude(string? output)
    {
        Task.Run(CheckClaudeApiAsync).GetAwaiter().GetResult();
        CheckClaudeFormRefresh();
        var now = DateTimeOffset.UtcNow;
        var reading = new ClaudeReading(now, new ClaudeWindow(23.5, now.AddHours(3)), new ClaudeWindow(41.2, now.AddDays(4)));

        using var widget = new WidgetForm(new AppSettings { X = 100, Y = 100, AlwaysOnTop = false,
            ExpandOnHover = true, NotifyOnUsageLimitReached = false });
        foreach (var timer in new[] { "refreshTimer", "topmostTimer", "positionSaveTimer", "hoverTimer", "claudeTimer" })
            ((System.Windows.Forms.Timer)Get(widget, timer)!).Stop();
        ((NotifyIcon)Get(widget, "trayIcon")!).Visible = false;
        widget.CreateControl();
        var overview = widget.Bounds;
        // Real mouse and keyboard handlers: Codex -> reset details -> Claude -> Codex.
        Click(widget, 40, 70);
        Check("First click opens Codex details", (bool)Get(widget, "showResetDetails")!);
        Click(widget, 40, 70);
        Check("Second click opens Claude even when Codex is unavailable", (bool)Get(widget, "showClaude")! && !(bool)Get(widget, "showResetDetails")!);
        Check("Claude page has its own height", widget.Height == (int)Math.Round(264 * widget.DeviceDpi / 96f));
        Save(widget, output, "claude-setup");
        Set(widget, "claudeReading", reading);
        Save(widget, output, "claude-usage");
        EnterBody(widget);
        HoverTick(widget, true);
        Check("Claude does not expand on hover", !(bool)Get(widget, "hoverExpanded")!);
        Set(widget, "claudeReading", reading with { ReceivedAt = now.AddHours(-1) });
        Save(widget, output, "claude-stale");
        Set(widget, "claudeReading", reading with { FiveHour = new(100, now.AddSeconds(-1)) });
        Save(widget, output, "claude-reset-passed");
        Set(widget, "claudeReading", reading with { FiveHour = new(0, null) });
        Save(widget, output, "claude-reset-time-unavailable");
        Set(widget, "claudeReading", new ClaudeReading(now, null, null));
        Save(widget, output, "claude-no-limits");
        Set(widget, "claudeReading", reading);
        var claudeBounds = widget.Bounds;
        Invoke(widget, "SetBarOnly", true);
        Check("Collapsed Claude bar uses Claude weekly allowance", (bool)Get(widget, "showClaude")! &&
            RenderPixel(widget, 20, 3) == (Color)typeof(WidgetForm).GetMethod("RemainingColor", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, new object[] { reading.SevenDay!.Remaining })!);
        Invoke(widget, "SetBarOnly", false);
        Check("Collapsed Claude restores same face and position", widget.Bounds == claudeBounds && (bool)Get(widget, "showClaude")!);
        Invoke(widget, "ChangeAnnouncementPlacement", AnnouncementPlacement.Crown);
        Check("Claude never shows Codex announcements or a crown", !(bool)Invoke(widget, "get_CrownAnnouncements")! && widget.Bounds == claudeBounds);
        Save(widget, output, "claude-crown-preference");
        Invoke(widget, "ChangeAnnouncementPlacement", AnnouncementPlacement.Bottom);
        Invoke(widget, "OnKeyDown", new KeyEventArgs(Keys.Enter));
        Check("Third page returns to Codex overview and original geometry", !(bool)Get(widget, "showClaude")! &&
            !(bool)Get(widget, "showResetDetails")! && widget.Bounds == overview);
        for (var i = 0; i < 3; i++)
        {
            Invoke(widget, "ToggleFace"); Invoke(widget, "ToggleFace"); Invoke(widget, "ToggleFace");
        }
        Check("Three-page cycles do not drift", widget.Bounds == overview);
    }
}
