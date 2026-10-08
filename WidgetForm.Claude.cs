namespace CodexBar;

internal sealed partial class WidgetForm
{
    private const int ClaudeHeight = 138;
    private bool showClaude;
    private bool showClaudeResets;
    private int firstVisibleClaudeReset;
    private IReadOnlyList<ClaudeResetGrant> AvailableClaudeResets =>
        claudeReading?.Resets?.Available(DateTimeOffset.UtcNow) ?? Array.Empty<ClaudeResetGrant>();
    private int ClaudeResetHeight => Math.Max(270, 244 + AvailableClaudeResets.Count * 48);
    private int VisibleClaudeResetRows => Math.Max(1, (LogicalHeight - 244) / 48);
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
        if (showClaude)
        {
            if (showClaudeResets) UpdateFaceSize();
            Invalidate();
        }
    }

    private void DrawClaude(Graphics g)
    {
        var now = DateTimeOffset.UtcNow;
        var stale = claudeReading?.Stale(now) == true;
        g.DrawRectangle(borderPen, .5f, .5f, WidgetWidth - 1, LogicalHeight - 1);
        g.FillEllipse(claudeReading is { Error: null } && !stale ? statusOkBrush : statusOfflineBrush, 10, 19, 6, 6);
        var status = claudeReading is null ? "CHECKING" : claudeReading.Error is not null ? "OFFLINE" : stale ? "STALE" :
            showClaudeResets ? "RESETS" : "USAGE";
        g.DrawString($"CLAUDE · {status} · {(showClaudeResets ? "4/4" : "3/4")}", titleFont, dimBrush, 20, 14);
        DrawWindowControls(g);
        if (showClaudeResets) { DrawClaudeResets(g, now, stale); return; }
        var forecast = ClaudeWeeklyPace(now);
        using var centered = new StringFormat(StringFormat.GenericTypographic)
        { Alignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        DrawClaudeValue(g, claudeReading?.FiveHour, now, stale, "5-hour\nleft", 12, centered);
        DrawClaudeValue(g, claudeReading?.SevenDay, now, stale, "Weekly\nleft", 99, centered);
        using var forecastBrush = new SolidBrush(ClaudeForecastColor(forecast));
        DrawPercentage(g, ClaudeForecastPercent(forecast), compactPercentFont, forecastBrush, new RectangleF(186, 39, 90, 36), centered);
        g.DrawString("Est. left\nat reset", compactLabelFont, dimBrush, new RectangleF(186, 75, 90, 29), centered);
        var week = claudeReading?.SevenDay;
        var known = week is not null && !week.Expired(now);
        DrawProgress(g, known ? week!.Remaining : 0, known && !stale ? RemainingColor(week!.Remaining) : dimBrush.Color);
        var caption = stale ? "Last known · forecast unavailable" : ClaudeForecastText(forecast);
        g.DrawString(caption, compactLabelFont, stale ? dimBrush : forecastBrush, new RectangleF(16, 117, 258, 16), centered);
    }

    private void DrawClaudeValue(Graphics g, ClaudeWindow? window, DateTimeOffset now, bool stale,
        string label, float left, StringFormat centered)
    {
        var available = window is not null && !window.Expired(now);
        using var brush = new SolidBrush(available && !stale ? RemainingColor(window!.Remaining) : dimBrush.Color);
        DrawPercentage(g, available ? $"{window!.Remaining:0}%" : "—", compactPercentFont, brush,
            new RectangleF(left, 39, 84, 36), centered);
        g.DrawString(label, compactLabelFont, dimBrush, new RectangleF(left, 75, 84, 29), centered);
    }

    private void DrawClaudeResets(Graphics g, DateTimeOffset now, bool stale)
    {
        DrawClaudeResetWindow(g, "5-hour reset", claudeReading?.FiveHour, 42, now, stale);
        DrawClaudeResetWindow(g, "Weekly reset", claudeReading?.SevenDay, 100, now, stale);
        g.DrawLine(borderPen, 20, 155, WidgetWidth - 20, 155);
        g.DrawString(stale ? "Reset credits · last known" : "Available reset credits", bodyFont, dimBrush, 20, 164);
        var grants = AvailableClaudeResets;
        if (claudeReading?.Resets is null)
            g.DrawString("Credits unavailable · check Claude", resetFont, dimBrush, 20, 194);
        else if (grants.Count == 0)
            g.DrawString(stale ? "No unexpired credits in last reading" : "No reset credits available", resetFont, dimBrush, 20, 194);
        else
        {
            firstVisibleClaudeReset = Math.Clamp(firstVisibleClaudeReset, 0, Math.Max(0, grants.Count - VisibleClaudeResetRows));
            for (var i = firstVisibleClaudeReset; i < Math.Min(grants.Count, firstVisibleClaudeReset + VisibleClaudeResetRows); i++)
            {
                var grant = grants[i];
                var top = 190 + (i - firstVisibleClaudeReset) * 48;
                var use = grant.UsableNow switch { true => "", false => " · not usable now", null => " · availability unknown" };
                g.DrawString($"{grant.Kind} ×{grant.Remaining}{use}", resetFont, dimBrush, 20, top);
                var date = grant.ExpiresAt is { } expiry ? $"Expires {expiry.ToLocalTime():ddd'.' MMM d} · {expiry.ToLocalTime():h:mm tt}" : "Expiry unavailable";
                DrawResetRow(g, date, stale ? "—" : grant.ExpiresAt is { } end ? ClaudeCountdown(end - now) : "—", top + 20);
            }
        }
        using var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        var updated = claudeReading is null ? "Checking Claude account…" : claudeReading.Error ??
            $"{(stale ? "Last read" : "Updated")} {claudeReading.ReceivedAt.ToLocalTime():ddd d MMM h:mm tt}";
        g.DrawString(updated, compactLabelFont, dimBrush, new RectangleF(20, LogicalHeight - 47, 250, 16), format);
        g.DrawString("Open Claude usage ↗", announcementSourceFont, dimBrush, 20, LogicalHeight - 28);
        if (grants.Count > VisibleClaudeResetRows)
            g.DrawString("Scroll", compactLabelFont, dimBrush, 234, LogicalHeight - 28);
    }

    private void DrawClaudeResetWindow(Graphics g, string label, ClaudeWindow? window, float top, DateTimeOffset now, bool stale)
    {
        g.DrawString(label, bodyFont, dimBrush, 20, top);
        var date = window?.ResetsAt is { } reset ? $"{reset.ToLocalTime():ddd'.' MMM d} · {reset.ToLocalTime():h:mm tt}" : "Time unavailable";
        var countdown = stale ? "Offline" : window?.ResetsAt is not { } end ? "Unknown" : end <= now ? "Expired" : ClaudeCountdown(end - now);
        DrawResetRow(g, date, countdown, top + 22);
    }

    private bool IsClaudeUsageLink(Point point)
    {
        var logical = LogicalPoint(point);
        return !barOnly && showClaude && showClaudeResets && logical.X >= 20 && logical.X <= 190 &&
            logical.Y >= LogicalHeight - 29 && logical.Y <= LogicalHeight - 12;
    }

    private void OpenClaudeUsage()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://claude.ai/settings/usage") { UseShellExecute = true }); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CodexBar: could not open Claude usage: {ex.GetType().Name}");
            trayIcon.ShowBalloonTip(3500, AppBranding.DisplayName, "Could not open claude.ai/settings/usage in your browser.", ToolTipIcon.Warning);
        }
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

    private static string ClaudeCountdown(TimeSpan remaining) => remaining.TotalDays >= 1
        ? $"{remaining.TotalDays:0.0}d" : remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m" : $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
}
