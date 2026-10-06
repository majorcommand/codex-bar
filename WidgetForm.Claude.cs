namespace CodexBar;

internal sealed partial class WidgetForm
{
    private const int ClaudeHeight = 264;
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
        DrawClaudeWindow(g, "Five-hour allowance", claudeReading?.FiveHour, 42, now, stale);
        DrawClaudeWindow(g, "Weekly allowance", claudeReading?.SevenDay, 126, now, stale);
        using var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        var received = claudeReading is null ? "Checking Claude account usage…" :
            claudeReading.Error is not null ? claudeReading.Error :
            $"Updated {claudeReading.ReceivedAt.ToLocalTime():ddd d MMM h:mm tt}";
        g.DrawString(received, resetFont, dimBrush, new RectangleF(16, 212, 260, 17), format);
        g.DrawString(claudeReading is { Error: null, FiveHour: null, SevenDay: null }
            ? "No allowance supplied by Claude"
            : "Shared with Claude web and desktop", compactLabelFont, dimBrush, 16, 229);
        g.DrawString("Click for Codex usage · 3/3", resetFont, dimBrush, 16, 246);
    }

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
