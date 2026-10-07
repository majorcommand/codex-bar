namespace CodexBar;

internal sealed partial class WidgetForm
{
    private const int ClaudeHeight = 274;
    private bool showClaude;
    private ClaudeReading? claudeReading;
    private readonly System.Windows.Forms.Timer claudeTimer;

    private readonly ClaudeUsageClient claudeClient;
    private readonly CancellationTokenSource claudeCancellation = new();

    private async Task RefreshClaudeAsync(bool manual = false)
    {
        if (exiting || IsDisposed || Disposing) return;
        try { await claudeClient.RefreshAsync(manual, claudeCancellation.Token); }
        catch (OperationCanceledException) { return; }
        if (exiting || IsDisposed || Disposing) return;
        claudeReading = claudeClient.Current;
        if (showClaude) Invalidate();
    }

    private void DrawClaude(Graphics g)
    {
        var now = DateTimeOffset.UtcNow;
        var stale = claudeReading?.Stale(now) == true;
        g.DrawRectangle(borderPen, .5f, .5f, WidgetWidth - 1, LogicalHeight - 1);
        g.FillEllipse(claudeReading is { Error: null } && !stale ? dimBrush : statusOfflineBrush, 10, 19, 6, 6);
        g.DrawString("CLAUDE · " + (claudeReading is null ? "CHECKING" :
            claudeReading.Error is not null ? "OFFLINE" : stale ? "STALE" : "CONNECTED"),
            titleFont, dimBrush, 20, 14);
        DrawWindowControls(g);
        DrawClaudeWindow(g, "5-hour remaining", claudeReading?.FiveHour, 42, now, stale);
        DrawClaudeWindow(g, "Weekly remaining", claudeReading?.SevenDay, 126, now, stale);
        using var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        var forecast = ClaudeWeeklyPace(now);
        g.DrawString("Est. left at reset", compactFont, dimBrush, 16, 210);
        using var forecastBrush = new SolidBrush(ClaudeForecastColor(forecast));
        g.DrawString(ClaudeForecastText(forecast), compactLabelFont, forecastBrush, 16, 230);
        using var right = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
        DrawPercentage(g, ClaudeForecastPercent(forecast), compactPercentFont, forecastBrush, new RectangleF(176, 204, 98, 34), right);
        var received = claudeReading is null ? "Checking Claude account usage…" :
            claudeReading.Error is not null ? claudeReading.Error :
            $"Updated {claudeReading.ReceivedAt.ToLocalTime():ddd d MMM h:mm tt}";
        g.DrawString(received, resetFont, dimBrush, new RectangleF(16, 254, 260, 17), format);
    }

    private UsagePace? ClaudeWeeklyPace(DateTimeOffset now)
    {
        if (claudeReading is not { SevenDay.ResetsAt: { } reset } reading ||
            reading.Stale(now) || reading.SevenDay.Expired(now)) return null;
        // Use the observation time, as Codex does. Idle time must not improve a cached forecast.
        return UsagePace.Calculate(reading.SevenDay.UsedPercentage, reset, reading.ReceivedAt);
    }

    private string ClaudeForecastText(UsagePace? forecast)
    {
        if (forecast?.Exhausted == true) return "Limit reached";
        if (forecast?.RemainingAtReset is not { } projected) return "Unavailable";
        if (Math.Round(projected) == 0) return "Near limit";
        return projected < 0 ? "Over weekly pace" : "On track";
    }

    private string ClaudeForecastPercent(UsagePace? forecast)
    {
        if (forecast?.RemainingAtReset is not { } projected) return forecast?.Exhausted == true ? "0%" : "—";
        var rounded = Math.Round(Math.Min(projected, 100));
        return $"{(rounded == 0 ? 0 : rounded):0}%";
    }

    private Color ClaudeForecastColor(UsagePace? forecast) =>
        forecast?.Exhausted == true || forecast?.RemainingAtReset is { } projected && (projected < 0 || Math.Round(projected) == 0) ? RemainingColor(0) :
        forecast?.RemainingAtReset is not null ? RemainingColor(100) : dimBrush.Color;

    private void DrawClaudeWindow(Graphics g, string label, ClaudeWindow? window,
        float top, DateTimeOffset now, bool stale)
    {
        var expired = window?.Expired(now) == true;
        var available = window is not null && !expired;
        g.DrawString(label, compactFont, dimBrush, 16, top);
        using var brush = new SolidBrush(available && !stale ? RemainingColor(window!.Remaining) : dimBrush.Color);
        using var right = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(available ? $"{window!.Remaining:0}%" : "—", compactPercentFont, brush,
            new RectangleF(176, top - 6, 98, 34), right);
        g.FillRectangle(trackBrush, 16, top + 28, 258, 4);
        if (available) g.FillRectangle(brush, 16, top + 28, (float)(258 * window!.Remaining / 100), 4);
        var reset = window?.ResetsAt is { } resetAt ?
            $"Reset {resetAt.ToLocalTime():ddd d MMM · h:mm tt}" : "Reset time unavailable";
        g.DrawString(reset, resetFont, dimBrush, 16, top + 39);
        var countdown = expired ? "Reset time passed · awaiting reading" : !available ? "No current account reading" :
            window!.ResetsAt is { } countdownAt ?
                $"{ClaudeCountdown(countdownAt - now)} until reset{(stale ? " · last known" : "")}" :
                $"Waiting for Claude reset time{(stale ? " · last known" : "")}";
        g.DrawString(countdown, compactLabelFont, dimBrush, 16, top + 56);
    }

    private static string ClaudeCountdown(TimeSpan remaining) => remaining.TotalDays >= 1
        ? $"{remaining.TotalDays:0.0}d" : remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m" : $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
}
