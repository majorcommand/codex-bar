using System.Globalization;

namespace CodexBar;

internal sealed record MiniStripValue(string Percentage, string Countdown, Color Color);

internal sealed partial class WidgetForm
{
    private const int MiniStripWidth = 472;
    private const int MiniStripHeight = 40;
    private bool miniStrip;
    private Point? miniRestoreLocation;
    private readonly System.Windows.Forms.Timer miniStripTimer;
    private readonly Font miniValueFont = new("Segoe UI Semibold", 17f, FontStyle.Regular, GraphicsUnit.Pixel);

    private void SavePreferences()
    {
        if (!isolatedPreferences) settings.Save();
    }

    private void SetMiniStrip(bool enabled)
    {
        if (miniStrip == enabled) return;
        if (enabled)
        {
            SetBarOnly(false);
            CancelHover();
            miniRestoreLocation = Location;
            var bottom = Bottom;
            miniStrip = true;
            UpdateFaceSize();
            Location = FullyVisibleLocation(new Point(Left, bottom - Height), Size, Screen.FromControl(this).WorkingArea);
            miniStripTimer.Start();
        }
        else
        {
            var restore = miniRestoreLocation ?? Location;
            miniStrip = false;
            miniStripTimer.Stop();
            UpdateFaceSize();
            Location = KeepVisible(restore);
            miniRestoreLocation = null;
        }
        settings.MiniStrip = enabled;
        ApplyTaskbarVisibility(Visible);
        ((ToolStripMenuItem)trayIcon.ContextMenuStrip!.Items["miniStrip"]!).Checked = enabled;
        SavePreferences();
        pointerInHoverBody = false;
        suppressHoverUntilReentry = true;
        SetAnnouncementTip(false);
        RecordVisibility(enabled ? "mini-strip-open" : "mini-strip-restore");
        Invalidate();
    }

    private MiniStripValue CodexMiniValue(DateTimeOffset now)
    {
        if (snapshot is not { } reading || !double.IsFinite(reading.UsedPercent) ||
            reading.UsedPercent is < 0 or > 100)
            return new("—", "—", dimBrush.Color);
        if (reading.ResetsAt <= now)
            return new("—", "—", dimBrush.Color);
        var stale = !liveConnected || now - reading.CapturedAt >= TimeSpan.FromMinutes(10);
        return MiniValue(100 - reading.UsedPercent, reading.ResetsAt, stale, now);
    }

    private MiniStripValue ClaudeMiniValue(ClaudeWindow? window, DateTimeOffset now)
    {
        if (window is null || !double.IsFinite(window.UsedPercentage) || window.UsedPercentage is < 0 or > 100)
            return new("—", "—", dimBrush.Color);
        if (window.Expired(now))
            return new("—", "—", dimBrush.Color);
        return MiniValue(window.Remaining, window.ResetsAt, claudeReading!.Stale(now), now);
    }

    private MiniStripValue MiniValue(double remaining, DateTimeOffset? reset, bool stale, DateTimeOffset now)
    {
        var percentage = remaining.ToString("0", CultureInfo.InvariantCulture) + "%" + (stale ? "*" : "");
        var countdown = !stale && reset is { } end ? ClaudeCountdown(end - now) : "—";
        return new(percentage, countdown, stale ? dimBrush.Color : RemainingColor(remaining));
    }

    private void DrawMiniStrip(Graphics g)
    {
        var now = DateTimeOffset.UtcNow;
        g.DrawRectangle(borderPen, .5f, .5f, MiniStripWidth - 1, MiniStripHeight - 1);
        DrawMiniGroup(g, "Codex", CodexMiniValue(now), 10, 138);
        DrawMiniGroup(g, "Claude 5h", ClaudeMiniValue(claudeReading?.FiveHour, now), 160, 154);
        DrawMiniGroup(g, "Week", ClaudeMiniValue(claudeReading?.SevenDay, now), 328, 134);
        g.DrawLine(borderPen, 152, 10, 152, 30);
        g.DrawLine(borderPen, 320, 10, 320, 30);
    }

    private void DrawMiniGroup(Graphics g, string label, MiniStripValue value, float left, float width)
    {
        var saved = g.Save();
        g.SetClip(new RectangleF(left, 1, width, MiniStripHeight - 2));
        using var format = new StringFormat(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
        void Token(string text, Font font, Brush brush, float gap)
        {
            var size = g.MeasureString(text, font, int.MaxValue, format);
            g.DrawString(text, font, brush, new RectangleF(left, 0, size.Width + 2, MiniStripHeight), format);
            left += size.Width + gap;
        }
        Token(label, titleFont, dimBrush, 4);
        using var percentBrush = new SolidBrush(value.Color);
        Token(value.Percentage, miniValueFont, percentBrush, 4);
        Token("· " + value.Countdown, resetFont, dimBrush, 0);
        g.Restore(saved);
    }
}
