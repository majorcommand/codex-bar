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
        Task.Run(CheckClaudeRenewalAsync).GetAwaiter().GetResult();
        CheckClaudeFormRefresh();
        CheckClaudeForecast(output);
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
        Check("Claude page has its own height", widget.Height == (int)Math.Round(274 * widget.DeviceDpi / 96f));
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

    private static void CheckClaudeForecast(string? output)
    {
        var now = DateTimeOffset.UtcNow;
        using var widget = new WidgetForm(new AppSettings { X = 100, Y = 100, AlwaysOnTop = false });
        ((NotifyIcon)Get(widget, "trayIcon")!).Visible = false;
        widget.CreateControl();
        Set(widget, "showClaude", true);
        Invoke(widget, "UpdateFaceSize");
        var reading = new ClaudeReading(now, new(99, now.AddHours(1)), new(18, now.AddDays(5)));
        UsagePace? Forecast(ClaudeReading? value, DateTimeOffset? at = null)
        {
            Set(widget, "claudeReading", value!);
            return (UsagePace?)Invoke(widget, "ClaudeWeeklyPace", at ?? now);
        }
        string Text(UsagePace? value) => (string)Invoke(widget, "ClaudeForecastText", (object)value!)!;
        string Percent(UsagePace? value) => (string)Invoke(widget, "ClaudeForecastPercent", (object)value!)!;
        Color ColorOf(UsagePace? value) => (Color)Invoke(widget, "ClaudeForecastColor", (object)value!)!;
        var sustainable = Forecast(reading);
        Check("Claude weekly forecast reuses the Codex calculation exactly", sustainable == UsagePace.Calculate(18, now.AddDays(5), now) &&
            Near(sustainable?.AveragePerDay, 9) && Near(sustainable?.RemainingAtReset, 37));
        Check("Claude sustainable pace is green with a separate forecast percentage", Text(sustainable) == "On track" && Percent(sustainable) == "37%" &&
            ColorOf(sustainable) == Color.FromArgb(46, 220, 112));
        Save(widget, output, "claude-forecast-on-track");
        Check("Claude cached forecast does not improve during idle time", Forecast(reading, now.AddMinutes(9)) == sustainable);
        Check("Five-hour exhaustion does not change the weekly forecast", Forecast(reading with { FiveHour = new(100, now.AddHours(1)) }) == sustainable);
        var over = Forecast(reading with { SevenDay = new(50, now.AddDays(5)) });
        Check("Claude over-limit pace uses the same signed projection", Near(over?.RemainingAtReset, -75) && Text(over) == "Over weekly pace" && Percent(over) == "-75%" &&
            ColorOf(over) == ColorOf(UsagePace.Calculate(100, now.AddDays(5), now)));
        Save(widget, output, "claude-forecast-over-limit");
        var near = Forecast(reading with { SevenDay = new(100d * 2 / 7, now.AddDays(5)) });
        Check("Claude near-zero forecast never promises room to spare", Text(near) == "Near limit" && Percent(near) == "0%" && ColorOf(near) == ColorOf(over));
        var exhausted = Forecast(reading with { SevenDay = new(100, now.AddDays(5)) });
        Check("Claude exhausted weekly allowance remains explicit", Text(exhausted) == "Limit reached");
        Save(widget, output, "claude-forecast-exhausted");
        var zero = Forecast(reading with { SevenDay = new(0, now.AddDays(5)) });
        Check("Claude zero usage projects full remaining allowance", Text(zero) == "On track" && Percent(zero) == "100%");
        var early = Forecast(reading with { SevenDay = new(10, now.AddDays(7).AddMinutes(-1)) });
        Check("Claude early use forecasts immediately like Codex", early?.RemainingAtReset < 0 && Text(early) == Text(over));
        foreach (var unavailable in new ClaudeReading?[] { null, reading with { ReceivedAt = now.AddMinutes(-10) },
            reading with { Error = "Claude read failed; will retry." }, reading with { SevenDay = null },
            reading with { SevenDay = new(18, null) }, reading with { SevenDay = new(18, now) },
            reading with { SevenDay = new(18, now.AddDays(8)) }, reading with { SevenDay = new(18, now.AddDays(7)) } })
        {
            var forecast = Forecast(unavailable);
            Check("Unavailable, stale, expired and exact-start Claude forecasts cannot appear on track", Text(forecast) == "Unavailable" && Percent(forecast) == "—" && ColorOf(forecast) != ColorOf(sustainable));
        }
        Forecast(reading with { Error = "Claude read failed; will retry." });
        Save(widget, output, "claude-forecast-unavailable");
        Check("Claude forecast recovers on the next valid weekly reading", Forecast(reading) == sustainable);
        Check("A new Claude weekly window uses its own observed pace", Near(Forecast(reading with { SevenDay = new(5, now.AddDays(6)) })?.RemainingAtReset, 65));
        using var bitmap = new Bitmap(290, 274);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        foreach (var text in new[] { Text(sustainable), Text(zero), Text(over), Text(near), Text(exhausted), Text(null) })
            Check("Claude forecast text fits the available width: " + text, graphics.MeasureString(text, font).Width < 260);
        Check("Claude forecast percentage avoids negative zero", Percent(UsagePace.Calculate(100.1 * 2 / 7, now.AddDays(5), now)) == "0%");
        Forecast(reading with { SevenDay = new(10, now.AddDays(7).AddMinutes(-1)) });
        Save(widget, output, "claude-forecast-large-shortfall");
    }
}
