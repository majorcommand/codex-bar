using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CodexBar;

internal sealed partial class WidgetForm : Form
{
    private const int WidgetWidth = 290;
    private const int WidgetHeight = 114;
    private const int BarHeight = 8;
    private const int AnnouncementHeight = 24;
    private const int TitleHeight = 30;
    private const int ExpandedUsageHeight = 236;
    private const int DetailsMinimumHeight = 176;
    private const int ResetRowsTop = 122;
    private const int ResetRowHeight = 24;
    private const int DetailsFooterSpace = 28;
    private static readonly Color NormalBackground = Color.FromArgb(8, 10, 9);
    private static readonly Color WarningBackground = Color.FromArgb(132, 38, 48);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);
    private const double FullAvailabilityUsedPercent = 0.001;
    private static readonly TimeSpan NotificationDeliveryTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResetTimeMatchTolerance = TimeSpan.FromHours(1);
    private readonly AppSettings settings;
    private readonly VisibilityLog? visibilityLog;
    private string? lastVisibilityState;
    private bool diagnosticWarningShown;
    private readonly CodexAppServerClient liveClient = new();
    private readonly ResetAnnouncementClient announcementClient;
    private readonly ReleaseUpdateClient updateClient;
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly System.Windows.Forms.Timer updateTimer;
    private readonly EditionVersion editionVersion = EditionVersion.Current;
    private ReleaseUpdate? availableUpdate;
    private bool updateRefreshing;
    private string? notifiedUpdate;
    private DateTimeOffset nextManualUpdateCheck;
    private readonly CancellationTokenSource announcementCancellation = new();
    private readonly System.Windows.Forms.Timer announcementTimer;
    private readonly ToolTip announcementTip = new();
    private readonly bool isolatedPreferences;
    private ResetAnnouncements? announcements;
    private DateTimeOffset? announcementCheckedAt;
    private bool announcementFailed;
    private bool announcementRefreshing;
    private string? lastAnnouncementTip;
    private Size? regionSize;
    private bool regionCrown;
    private float regionScale;
    private readonly Icon appIcon;
    private readonly NotifyIcon trayIcon;
    private string trayNumber = "—";
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly System.Windows.Forms.Timer positionSaveTimer;
    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly System.Windows.Forms.Timer topmostTimer;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly Pen borderPen = new(Color.FromArgb(42, 58, 49));
    private readonly Pen controlPen = new(Color.FromArgb(130, 150, 138), 1.2f);
    private readonly Font titleFont = new("Segoe UI Semibold", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font percentFont = new("Segoe UI Semibold", 100f / 3, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font resetFont = new("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font announcementSourceFont = new("Segoe UI", 11f, FontStyle.Underline, GraphicsUnit.Pixel);
    private readonly SolidBrush announcementBrush = new(Color.FromArgb(255, 220, 70));
    private readonly Font compactFont = new("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font compactPercentFont = new("Segoe UI Semibold", 80f / 3, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font compactLabelFont = new("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font expandedPercentFont = new("Segoe UI Semibold", 51f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font expandedValueFont = new("Segoe UI Semibold", 20f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font expandedForecastFont = new("Segoe UI Semibold", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font bodyFont = new("Segoe UI", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font dateFont = new("Segoe UI Semibold", 16f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly SolidBrush dimBrush = new(Color.FromArgb(143, 160, 150));
    private readonly SolidBrush statusOkBrush = new(Color.FromArgb(84, 235, 120));
    private readonly SolidBrush statusOfflineBrush = new(Color.FromArgb(239, 128, 128));
    private readonly SolidBrush textBrush = new(Color.FromArgb(225, 235, 229));
    private readonly SolidBrush trackBrush = new(Color.FromArgb(31, 39, 34));
    private readonly SolidBrush usageBrush = new(Color.FromArgb(46, 220, 112));
    private readonly SolidBrush countdownBrush = new(Color.FromArgb(46, 220, 112));
    private readonly SolidBrush forecastWarningBrush = new(WarningBackground);
    private readonly SolidBrush forecastWarningTextBrush = new(Color.Black);
    private UsageSnapshot? snapshot;
    private bool liveConnected;
    private string? readError;
    private bool exiting;
    private bool showResetDetails;
    private UsagePace? pace;
    private bool abovePace;
    private DateTimeOffset? paceReset;
    private Point? mouseDownScreen;
    private Point dragOrigin;
    private bool dragging;
    private bool controlPress;
    private bool titlePress;
    private bool announcementPress;
    private int firstVisibleExpiration;
    private bool hoverExpanded;
    private bool barOnly;
    private Point? barRestoreLocation;
    private bool expandOnNextHoverTick;
    private bool pointerInHoverBody;
    private bool suppressHoverUntilReentry;
    // Resizing may fit the larger face inward; retain where the compact face belongs.
    private Point? compactAnchor;

    // Drawing uses logical pixels so fonts and hit areas scale together with DPI.
    private float UiScale => DeviceDpi / 96f;
    private int LogicalWidth => (int)Math.Round(ClientSize.Width / UiScale);
    private int ActiveAnnouncementHeight => showClaude ? 0 : AnnouncementHeight;
    private bool CrownAnnouncements => !barOnly && !showClaude && settings.ResetAnnouncementPlacement == AnnouncementPlacement.Crown;
    private int ContentTop => CrownAnnouncements ? AnnouncementHeight : 0;
    private Rectangle ProgressTrack => new(16, barOnly ? 2 : hoverExpanded ? 110 : WidgetHeight - 15,
        WidgetWidth - 32, !barOnly && hoverExpanded ? 6 : 4);
    private int LogicalHeight => (int)Math.Round(ClientSize.Height / UiScale) - (barOnly ? 0 : ActiveAnnouncementHeight);
    private int VisibleExpirationRows => Math.Max(1, (LogicalHeight - ResetRowsTop - DetailsFooterSpace) / ResetRowHeight);
    private PointF LogicalPoint(Point point) => new(point.X / UiScale, point.Y / UiScale - ContentTop);
    private RectangleF AnnouncementBounds => CrownAnnouncements
        ? new RectangleF(45, 0, WidgetWidth - 90, AnnouncementHeight)
        : new RectangleF(12, LogicalHeight, WidgetWidth - 24, AnnouncementHeight);
    private RectangleF AnnouncementSourceBounds => new(AnnouncementBounds.Right - 50,
        AnnouncementBounds.Top, 50, AnnouncementHeight);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowStyle(IntPtr windowHandle, int index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr windowHandle, uint relationship);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect bounds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr windowHandle, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    public WidgetForm(AppSettings? preferences = null, VisibilityLog? diagnostics = null,
        ResetAnnouncementClient? resetAnnouncementsClient = null, ReleaseUpdateClient? releaseUpdateClient = null,
        ClaudeUsageClient? claudeUsageClient = null)
    {
        // Injected preferences keep isolated checks away from the user's log.
        visibilityLog = diagnostics ?? (preferences is null ? new VisibilityLog() : null);
        settings = preferences ?? AppSettings.Load();
        isolatedPreferences = preferences is not null;
        announcementClient = resetAnnouncementsClient ?? new ResetAnnouncementClient();
        updateClient = releaseUpdateClient ?? new ReleaseUpdateClient();
        claudeClient = claudeUsageClient ?? new ClaudeUsageClient();
        if (!Enum.IsDefined(settings.ResetAnnouncementPlacement))
            settings.ResetAnnouncementPlacement = AnnouncementPlacement.Bottom;
        Exception? startupError = null;
        if (preferences is null)
        {
            try
            {
                if (settings.ApplyStartupDefault(() => AppSettings.StartsWithWindows = true)) settings.Save();
            }
            catch (Exception ex) { startupError = ex; }
        }
        Text = "CodexBar — MajorCommand Edition";
        FormBorderStyle = FormBorderStyle.None;
        ApplyTaskbarVisibility(true);
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(WidgetWidth, WidgetHeight + AnnouncementHeight);
        UpdateWidgetRegion();
        BackColor = NormalBackground;
        DoubleBuffered = true;
        TopMost = settings.AlwaysOnTop;
        Opacity = Math.Clamp(settings.Opacity, 0.5, 1.0);
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? CreateTrayIcon();
        Icon = appIcon;

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = settings.X.HasValue && settings.Y.HasValue
            ? KeepVisible(new Point(settings.X.Value, settings.Y.Value))
            : new Point(area.Right - Width - 24, area.Bottom - Height - 24);

        trayIcon = new NotifyIcon
        {
            Icon = CreateUsageTrayIcon(trayNumber),
            Text = "CodexBar — loading weekly usage",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        trayIcon.DoubleClick += (_, _) => ShowWidget();
        ContextMenuStrip = trayIcon.ContextMenuStrip;
        if (startupError is not null) ReportStartupError(startupError);

        refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Clamp(settings.RefreshSeconds, 5, 300) * 1_000
        };
        refreshTimer.Tick += async (_, _) => await RefreshUsageAsync();
        refreshTimer.Start();

        claudeTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        claudeTimer.Tick += async (_, _) => await RefreshClaudeAsync();
        if (!isolatedPreferences)
        {
            claudeTimer.Start();
        }

        positionSaveTimer = new System.Windows.Forms.Timer { Interval = 400 };
        positionSaveTimer.Tick += (_, _) =>
        {
            positionSaveTimer.Stop();
            SavePositionNow();
        };

        hoverTimer = new System.Windows.Forms.Timer();
        hoverTimer.Tick += (_, _) =>
        {
            hoverTimer.Stop();
            if (!Visible) CancelHover();
            else ProcessHoverTick(IsHoverBody(PointToClient(Cursor.Position)),
                ContextMenuStrip?.Visible == true);
        };

        topmostTimer = new System.Windows.Forms.Timer { Interval = 2_000 };
        topmostTimer.Tick += (_, _) =>
        {
            ObserveVisibility("poll");
            if (visibilityLog?.LastError is not null && !diagnosticWarningShown)
            {
                diagnosticWarningShown = true;
                trayIcon.ShowBalloonTip(4500, "CodexBar",
                    "Could not save the visibility log. Check access to the local CodexBar settings folder.", ToolTipIcon.Warning);
            }
            MaintainAlwaysOnTop(Visible, ContextMenuStrip?.Visible == true, NeedsTopmostRepair, ApplyAlwaysOnTopState);
        };
        topmostTimer.Start();

        announcementTimer = new System.Windows.Forms.Timer { Interval = 300_000 };
        announcementTimer.Tick += async (_, _) => await RefreshAnnouncementsAsync();
        if (!isolatedPreferences) announcementTimer.Start();
        updateTimer = new System.Windows.Forms.Timer { Interval = 86_400_000 };
        updateTimer.Tick += async (_, _) => await RefreshUpdatesAsync();
        if (!isolatedPreferences) updateTimer.Start();
        Shown += async (_, _) => await Task.WhenAll(RefreshUsageAsync(), RefreshAnnouncementsAsync(), RefreshUpdatesAsync(), RefreshClaudeAsync());
        LocationChanged += (_, _) => QueuePositionSave();
        FormClosing += OnFormClosing;
        KeyPreview = true;
        RecordVisibility("session-start", $"build={typeof(WidgetForm).Assembly.ManifestModule.ModuleVersionId}");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RecordVisibility("handle-created");
        ApplyAlwaysOnTopState();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        RecordVisibility("handle-destroying", $"recreating={RecreatingHandle}");
        base.OnHandleDestroyed(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        RecordVisibility("managed-visibility-changed");
        if (Visible) ApplyAlwaysOnTopState();
        else if (hoverTimer is not null) CancelHover();
    }

    protected override void WndProc(ref Message m)
    {
        var observed = RecordVisibilityMessage(m.Msg, m.WParam, m.LParam);
        base.WndProc(ref m);
        if (observed) ObserveVisibility("after-native-message");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    private bool RecordVisibilityMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        if (visibilityLog is null) return false;
        switch (message)
        {
            case 0x0018: // WM_SHOWWINDOW, before default processing changes visibility.
                var reason = lParam.ToInt64() switch
                {
                    0 => "ShowWindow/unspecified-caller", 1 => "owner-minimized",
                    2 => "other-window-maximized", 3 => "owner-restored",
                    4 => "other-window-restored-or-minimized", _ => "unknown"
                };
                RecordVisibility("WM_SHOWWINDOW", $"show={wParam != IntPtr.Zero};reason={reason};rawReason={lParam}");
                return true;
            case 0x0047 when lParam != IntPtr.Zero: // WM_WINDOWPOSCHANGED
                var position = Marshal.PtrToStructure<NativeWindowPosition>(lParam);
                if ((position.Flags & 0x00C0) == 0) return false; // SWP_SHOWWINDOW | SWP_HIDEWINDOW
                RecordVisibility("WM_WINDOWPOSCHANGED", $"show={(position.Flags & 0x0040) != 0};hide={(position.Flags & 0x0080) != 0};flags=0x{position.Flags:X}");
                return true;
            case 0x0010: // WM_CLOSE
            case 0x0011: // WM_QUERYENDSESSION
            case 0x0016: // WM_ENDSESSION
            case 0x0218: // WM_POWERBROADCAST (numeric sleep/resume status only)
                RecordVisibility("native-lifecycle-message", $"message=0x{message:X};wParam={wParam};lParam={lParam}");
                return true;
            case 0x0112 when (wParam.ToInt64() & 0xFFF0) is 0xF020 or 0xF030 or 0xF060 or 0xF120:
                RecordVisibility("WM_SYSCOMMAND", $"command=0x{wParam.ToInt64() & 0xFFF0:X}");
                return true;
            default:
                return false;
        }
    }

    private string VisibilityState()
    {
        var handle = IsHandleCreated ? Handle : IntPtr.Zero;
        var owner = handle != IntPtr.Zero ? GetWindow(handle, 4) : IntPtr.Zero;
        return $"managedVisible={Visible};windowState={WindowState};enabled={Enabled};exiting={exiting};" +
            $"alwaysOnTop={settings?.AlwaysOnTop};showInTaskbar={ShowInTaskbar};barOnly={barOnly};" +
            $"bounds={Left},{Top},{Width},{Height};widget=[{NativeVisibilityState(handle)}];owner=[{NativeVisibilityState(owner)}]";
    }

    private static string NativeVisibilityState(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "no-handle";
        var cloak = DwmGetWindowAttribute(handle, 14, out var value, sizeof(int)) == 0 ? value.ToString() : "unavailable";
        return $"handle={handle};visible={IsWindowVisible(handle)};" +
            $"minimized={(GetWindowStyle(handle, -16) & 0x20000000) != 0};" +
            $"topmost={(GetWindowStyle(handle, -20) & 8) != 0};cloaked={cloak}";
    }

    private void RecordVisibility(string eventName, string detail = "")
    {
        if (visibilityLog is not null) visibilityLog.Write(eventName, $"{detail};{VisibilityState()}");
    }

    private void ObserveVisibility(string source)
    {
        if (visibilityLog is null) return;
        var state = VisibilityState();
        if (state == lastVisibilityState) return;
        if (visibilityLog.Write("state-change", $"source={source};{state}")) lastVisibilityState = state;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateFaceSize();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.ScaleTransform(UiScale, UiScale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // A prior tray-icon text draw can make GDI+ SystemDefault render jagged text.
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if (barOnly)
        {
            DrawBarOnly(g);
            return;
        }
        if (showClaude)
        {
            DrawClaude(g);
            return;
        }
        DrawAnnouncement(g);
        g.TranslateTransform(0, ContentTop);

        var titleY = 14f;
        var codexHeight = g.MeasureString("CODEX", titleFont).Height;
        var dotY = titleY + (codexHeight - 6f) / 2f;
        var statusBrush = liveConnected ? statusOkBrush : statusOfflineBrush;
        g.FillEllipse(statusBrush, 10, dotY, 6, 6);

        using var titleFormat = new StringFormat { FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter };
        var title = showResetDetails ? "CODEX  ·  RESET DETAILS" : OverviewTitle();
        g.DrawString(title, titleFont, dimBrush,
            new RectangleF(20, titleY, LogicalWidth - 114, 18), titleFormat);
        DrawWindowControls(g);

        if (snapshot is null)
        {
            var message = readError ?? "Waiting for usage…";
            if (showResetDetails || hoverExpanded)
            {
                g.DrawString(message, bodyFont, textBrush, 20, 60);
                g.DrawString("Open Codex and sign in, then refresh.", resetFont, dimBrush, 20, 94);
                DrawFooter(g);
            }
            else
            {
                g.DrawString("—%", percentFont, textBrush, 14, 33);
                g.DrawString(message, compactFont, textBrush, 108, 34);
                g.DrawString("Open Codex & sign in", compactFont, dimBrush, 108, 50);
                g.DrawString("Refresh via tray menu", compactFont, dimBrush, 108, 66);
                DrawProgress(g, 0, Color.FromArgb(45, 62, 52));
            }
            return;
        }

        var remainingPercent = 100 - snapshot.UsedPercent;
        if (showResetDetails) DrawResetSchedule(g, snapshot);
        else if (hoverExpanded) DrawExpandedUsage(g, remainingPercent);
        else DrawUsageOverview(g, remainingPercent);
        if (showResetDetails) DrawFooter(g);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        UpdateHoverTarget(IsHoverBody(PointToClient(Cursor.Position)));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetAnnouncementTip(false);
        UpdateHoverTarget(false);
    }

    private bool IsHoverBody(Point point) => !barOnly && IsInsideWidget(point) &&
        LogicalPoint(point).Y > TitleHeight && LogicalPoint(point).Y < LogicalHeight;

    private bool IsInsideWidget(Point point) => ClientRectangle.Contains(point) &&
        (Region?.IsVisible(point) ?? true);

    private bool IsAnnouncement(Point point) => !barOnly && !showClaude && IsInsideWidget(point) &&
        AnnouncementBounds.Contains(point.X / UiScale, point.Y / UiScale);

    private bool IsAnnouncementSource(Point point) => IsAnnouncement(point) &&
        AnnouncementSourceBounds.Contains(point.X / UiScale, point.Y / UiScale);

    private void UpdateHoverTarget(bool inBody)
    {
        var wasInBody = pointerInHoverBody;
        pointerInHoverBody = inBody;
        if (!inBody)
        {
            suppressHoverUntilReentry = false;
            if (wasInBody || (hoverExpanded && !hoverTimer.Enabled))
            {
                hoverTimer.Stop();
                if (hoverExpanded) QueueHover(false);
            }
        }
        else if (hoverExpanded) hoverTimer.Stop();
        else if (!wasInBody && !suppressHoverUntilReentry) QueueHover(true);
    }

    private void QueueHover(bool expand)
    {
        hoverTimer.Stop();
        if (!settings.ExpandOnHover || showResetDetails || showClaude || barOnly) return;
        if (expand && (!pointerInHoverBody || suppressHoverUntilReentry)) return;
        expandOnNextHoverTick = expand;
        hoverTimer.Interval = expand ? 350 : 450;
        hoverTimer.Start();
    }

    private void ProcessHoverTick(bool pointerInBody, bool menuVisible)
    {
        if (!settings.ExpandOnHover || showResetDetails || showClaude || barOnly)
        {
            CancelHover();
            return;
        }
        // Keep the geometry stable while dragging or choosing a menu item.
        if (mouseDownScreen is not null || menuVisible)
        {
            QueueHover(expandOnNextHoverTick);
            return;
        }
        if (expandOnNextHoverTick && pointerInBody && !suppressHoverUntilReentry) SetHoverExpanded(true);
        else if (!expandOnNextHoverTick && !pointerInBody) SetHoverExpanded(false);
    }

    private void SetHoverExpanded(bool expanded)
    {
        if (hoverExpanded == expanded) return;
        if (expanded) compactAnchor = Location;
        hoverExpanded = expanded;
        UpdateFaceSize();
        if (!expanded) compactAnchor = null;
        Invalidate();
    }

    private void CancelHover()
    {
        hoverTimer.Stop();
        SetHoverExpanded(false);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !IsInsideWidget(e.Location)) return;
        hoverTimer.Stop();
        var point = LogicalPoint(e.Location);
        announcementPress = IsAnnouncement(e.Location);
        controlPress = WindowControlAt(point) >= 0;
        titlePress = !barOnly && (CrownAnnouncements && point.Y < 0 ||
            point.Y >= 0 && point.Y <= TitleHeight && !controlPress);
        dragging = false;
        mouseDownScreen = PointToScreen(e.Location);
        dragOrigin = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (mouseDownScreen is not { } start)
        {
            UpdateHoverTarget(IsHoverBody(e.Location));
            SetAnnouncementTip(IsAnnouncement(e.Location));
            var point = LogicalPoint(e.Location);
            Cursor = barOnly ? Cursors.SizeAll : IsAnnouncementSource(e.Location) ? Cursors.Hand
                : CrownAnnouncements && point.Y < 0 ||
                    point.Y >= 0 && point.Y <= TitleHeight && point.X < LogicalWidth - 90
                    ? Cursors.SizeAll : Cursors.Default;
            return;
        }
        if (controlPress) return;
        var current = PointToScreen(e.Location);
        var threshold = SystemInformation.DragSize;
        if (!dragging && (Math.Abs(current.X - start.X) > threshold.Width / 2 ||
            Math.Abs(current.Y - start.Y) > threshold.Height / 2))
        {
            suppressHoverUntilReentry = true;
            if (titlePress && hoverExpanded)
            {
                // Keep the title under the pointer when deliberately starting a drag.
                var titleLocation = Location;
                CancelHover();
                Location = KeepVisible(titleLocation);
                dragOrigin = Location;
            }
            dragging = true;
        }
        if (dragging)
        {
            var previous = Location;
            Location = KeepVisible(new Point(
                dragOrigin.X + current.X - start.X, dragOrigin.Y + current.Y - start.Y));
            if (compactAnchor is { } anchor)
                compactAnchor = new Point(anchor.X + Location.X - previous.X,
                    anchor.Y + Location.Y - previous.Y);
            if (barRestoreLocation is { } restore)
                barRestoreLocation = new Point(restore.X + Location.X - previous.X,
                    restore.Y + Location.Y - previous.Y);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || mouseDownScreen is null) return;
        var wasDragging = dragging;
        var wasControl = controlPress;
        var wasTitle = titlePress;
        var wasAnnouncement = announcementPress;
        var start = LogicalPoint(PointToClient(mouseDownScreen.Value));
        var point = LogicalPoint(e.Location);
        mouseDownScreen = null;
        dragging = false;
        Capture = false;
        pointerInHoverBody = IsHoverBody(e.Location);
        if (wasDragging)
        {
            suppressHoverUntilReentry = true;
            hoverTimer.Stop();
        }
        if (!IsInsideWidget(e.Location) || wasDragging)
        {
            if (hoverExpanded) QueueHover(false);
            return;
        }
        if (wasControl)
        {
            var control = WindowControlAt(point);
            if (control >= 0 && control == WindowControlAt(start))
            {
                if (control == 0) SetBarOnly(true);
                else HideToTray(control == 2 ? "close-button" : "minimize-button");
            }
            return;
        }
        if (barOnly)
        {
            SetBarOnly(false);
            return;
        }
        if (wasAnnouncement)
        {
            if (IsAnnouncementSource(e.Location)) OpenAnnouncementSource();
            if (hoverExpanded) QueueHover(false);
            return;
        }
        if (wasTitle)
        {
            if (hoverExpanded) QueueHover(false);
            return;
        }
        ToggleFace();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture) return;
        if (dragging) suppressHoverUntilReentry = true;
        mouseDownScreen = null;
        dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            if (barOnly) SetBarOnly(false);
            else ToggleFace();
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (barOnly || !showResetDetails || snapshot is null) return;
        firstVisibleExpiration = Math.Clamp(firstVisibleExpiration - Math.Sign(e.Delta),
            0, Math.Max(0, snapshot.ResetExpirations.Count - VisibleExpirationRows));
        Invalidate();
    }

    private void ToggleFace()
    {
        if (barOnly) SetBarOnly(false);
        CancelHover();
        if (!showResetDetails && !showClaude) compactAnchor = Location;
        if (showResetDetails) { showResetDetails = false; showClaude = true; }
        else if (showClaude) showClaude = false;
        else showResetDetails = true;
        firstVisibleExpiration = 0;
        UpdateFaceSize();
        if (!showResetDetails && !showClaude) compactAnchor = null;
        Invalidate();
        pointerInHoverBody = IsHoverBody(PointToClient(Cursor.Position));
        if (!showResetDetails && !showClaude && Visible && pointerInHoverBody)
            QueueHover(true);
    }

    private void UpdateFaceSize()
    {
        var screen = Screen.FromControl(this);
        var wasFullyVisible = screen.WorkingArea.Contains(Bounds);
        var wantedHeight = showClaude ? ClaudeHeight : showResetDetails
            ? Math.Max(DetailsMinimumHeight, ResetRowsTop + DetailsFooterSpace +
                (snapshot?.ResetExpirations.Count ?? 0) * ResetRowHeight)
            : hoverExpanded ? ExpandedUsageHeight : WidgetHeight;
        ClientSize = new Size((int)Math.Round(WidgetWidth * UiScale),
            barOnly ? (int)Math.Round(BarHeight * UiScale)
                : Math.Min((int)Math.Round((wantedHeight + ActiveAnnouncementHeight) * UiScale), screen.WorkingArea.Height));
        UpdateWidgetRegion();
        // Keep a deliberately dragged larger face in place during refreshes.
        // Only shrinking back to compact restores its separately retained origin.
        var desired = barOnly || showResetDetails || showClaude || hoverExpanded ? Location : compactAnchor ?? Location;
        Location = !barOnly && wasFullyVisible ? FullyVisibleLocation(desired, Size, screen.WorkingArea) : KeepVisible(desired);
    }

    private void SetBarOnly(bool collapsed)
    {
        if (barOnly == collapsed) return;
        if (collapsed)
        {
            // Keep the strip's fill at the visible progress bar's screen position.
            // The details face has no progress bar, so collapse toward its bottom edge.
            var progressY = showResetDetails || showClaude ? LogicalHeight - 15 : ProgressTrack.Top;
            var stripLocation = new Point(Left, Top + (int)Math.Round((ContentTop + progressY - 2) * UiScale));
            CancelHover();
            barRestoreLocation = Location;
            barOnly = true;
            UpdateFaceSize();
            Location = KeepVisible(stripLocation);
        }
        else
        {
            var restore = barRestoreLocation ?? Location;
            barOnly = false;
            UpdateFaceSize();
            Location = KeepVisible(restore);
            barRestoreLocation = null;
        }
        pointerInHoverBody = false;
        suppressHoverUntilReentry = true;
        SetAnnouncementTip(false);
        RecordVisibility(collapsed ? "bar-collapse" : "bar-restore");
        Invalidate();
    }

    private void BringFullyOnScreen()
    {
        SetBarOnly(false);
        CancelHover();
        Location = FullyVisibleLocation(Location, Size, Screen.FromControl(this).WorkingArea);
        if (compactAnchor is not null) compactAnchor = Location;
        RecordVisibility("bring-fully-on-screen");
    }

    private GraphicsPath WidgetOutline()
    {
        var outline = new GraphicsPath();
        var height = LogicalHeight + (barOnly ? 0 : ActiveAnnouncementHeight);
        if (CrownAnnouncements)
            outline.AddPolygon(new PointF[]
            {
                new(0, AnnouncementHeight), new(35, AnnouncementHeight), new(45, 0),
                new(WidgetWidth - 45, 0), new(WidgetWidth - 35, AnnouncementHeight),
                new(WidgetWidth, AnnouncementHeight), new(WidgetWidth, height), new(0, height)
            });
        else outline.AddRectangle(new RectangleF(0, 0, WidgetWidth, height));
        return outline;
    }

    private void UpdateWidgetRegion()
    {
        if (regionSize == ClientSize && regionCrown == CrownAnnouncements && regionScale == UiScale) return;
        using var outline = WidgetOutline();
        using var transform = new Matrix();
        transform.Scale(UiScale, UiScale);
        outline.Transform(transform);
        var previous = Region;
        Region = CrownAnnouncements ? new Region(outline) : null;
        previous?.Dispose();
        regionSize = ClientSize;
        regionCrown = CrownAnnouncements;
        regionScale = UiScale;
    }

    private void ChangeAnnouncementPlacement(AnnouncementPlacement placement)
    {
        if (settings.ResetAnnouncementPlacement == placement) return;
        CancelHover();
        var area = Screen.FromControl(this).WorkingArea;
        var wasFullyVisible = area.Contains(Bounds);
        var oldTop = settings.ResetAnnouncementPlacement == AnnouncementPlacement.Crown ? AnnouncementHeight : 0;
        settings.ResetAnnouncementPlacement = placement;
        var newTop = placement == AnnouncementPlacement.Crown ? AnnouncementHeight : 0;
        var delta = (int)Math.Round((oldTop - newTop) * UiScale);
        if (!barOnly && !showClaude) Location = new Point(Left, Top + delta);
        else if (barOnly && !showClaude && barRestoreLocation is { } restore) barRestoreLocation = new Point(restore.X, restore.Y + delta);
        if (compactAnchor is { } anchor) compactAnchor = new Point(anchor.X, anchor.Y + delta);
        UpdateFaceSize();
        if (!barOnly && wasFullyVisible) Location = FullyVisibleLocation(Location, Size, area);
        pointerInHoverBody = false;
        Invalidate();
        if (!isolatedPreferences) settings.Save();
    }

    private bool AnnouncementStale(DateTimeOffset now) => announcementFailed ||
        announcementCheckedAt is null || now - announcementCheckedAt > TimeSpan.FromMinutes(10);

    private string AnnouncementText(DateTimeOffset now) => announcements is null
        ? announcementFailed ? "Reset info offline" : "Checking resets…"
        : AnnouncementStale(now) ? "Stale · reset info" : announcements.Label(now);

    private bool AnnouncementIsActive(DateTimeOffset now) =>
        !AnnouncementStale(now) && announcements?.IsActive(now) == true;

    private void DrawAnnouncement(Graphics g)
    {
        using var outline = WidgetOutline();
        // The right/bottom edges are inset to keep their stroke inside the client area.
        using var border = new Matrix();
        border.Translate(0.5f, 0.5f);
        border.Scale((WidgetWidth - 1f) / WidgetWidth,
            (LogicalHeight + AnnouncementHeight - 1f) / (LogicalHeight + AnnouncementHeight));
        outline.Transform(border);
        g.DrawPath(borderPen, outline);
        var now = DateTimeOffset.Now;
        var bounds = AnnouncementBounds;
        var textOffset = CrownAnnouncements ? 3 : 0;
        var brush = AnnouncementIsActive(now) ? announcementBrush : dimBrush;
        using var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
        g.DrawString(AnnouncementText(now), resetFont, brush,
            new RectangleF(bounds.Left, bounds.Top + textOffset, bounds.Width - 54, bounds.Height), format);
        var sourceBounds = AnnouncementSourceBounds;
        sourceBounds.Offset(0, textOffset);
        format.Alignment = StringAlignment.Far;
        g.DrawString("Source ↗", announcementSourceFont, brush, sourceBounds, format);
        if (!CrownAnnouncements)
            g.DrawLine(borderPen, 12, LogicalHeight, WidgetWidth - 12, LogicalHeight);
    }

    private void SetAnnouncementTip(bool show)
    {
        var text = showClaude ? "Shared Claude account limits · checked every five minutes. Right-click to refresh."
            : barOnly ? "Click to restore · drag to move" + (!liveConnected ? " · usage offline" : "")
            : show ? "Data from Codex Resets · " + ResetAnnouncementClient.SourceUrl + "\n" +
            (announcements?.Details(DateTimeOffset.Now) ?? "Checking the independent reset tracker.") +
            (AnnouncementStale(DateTimeOffset.Now) ? "\nStatus unavailable or stale; check the source." : "") +
            (announcementCheckedAt is { } checkedAt ? $"\nLast checked {checkedAt.ToLocalTime():h:mm tt}." : "") +
            "\nClick the source link to open the tracker. Drag the crown or title strip to move." : "";
        if (text == lastAnnouncementTip) return;
        lastAnnouncementTip = text;
        announcementTip.SetToolTip(this, text);
    }

    private void OpenAnnouncementSource()
    {
        try { Process.Start(new ProcessStartInfo(ResetAnnouncementClient.SourceUrl) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Trace.WriteLine($"CodexBar: could not open reset tracker: {ex.GetType().Name}");
            trayIcon.ShowBalloonTip(3500, "CodexBar", "Could not open codex-resets.com in your browser.", ToolTipIcon.Warning);
        }
    }

    private async Task RefreshAnnouncementsAsync()
    {
        if (announcementRefreshing || exiting || Disposing || IsDisposed) return;
        announcementRefreshing = true;
        announcementTimer.Stop();
        var nextCheck = TimeSpan.FromMinutes(5);
        try
        {
            var current = await announcementClient.ReadAsync(announcementCancellation.Token);
            if (exiting || Disposing || IsDisposed) return;
            announcements = current;
            announcementCheckedAt = DateTimeOffset.Now;
            announcementFailed = false;
        }
        catch (OperationCanceledException) when (announcementCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (exiting || Disposing || IsDisposed) return;
            announcementFailed = true;
            if (ex is AnnouncementReadException { RetryAfter: { } delay } && delay > nextCheck) nextCheck = delay;
            Trace.WriteLine($"CodexBar: reset announcement read failed: {ex.GetType().Name}");
        }
        finally
        {
            announcementRefreshing = false;
            if (!exiting && !Disposing && !IsDisposed)
            {
                announcementTimer.Interval = (int)Math.Clamp(nextCheck.TotalMilliseconds, 300_000, int.MaxValue);
                if (!isolatedPreferences) announcementTimer.Start();
                SetAnnouncementTip(false);
                Invalidate();
            }
        }
    }

    private void UpdatePace()
    {
        UpdateUsageTrayIcon();
        if (barOnly) SetAnnouncementTip(false);
        if (snapshot is null) return;
        if (paceReset != snapshot.ResetsAt) abovePace = false;
        paceReset = snapshot.ResetsAt;
        pace = UsagePace.Calculate(snapshot.UsedPercent, snapshot.ResetsAt, snapshot.CapturedAt, abovePace);
        abovePace = pace?.AbovePace ?? false;
        BackColor = NormalBackground;
        textBrush.Color = Color.FromArgb(240, 245, 241);
        dimBrush.Color = Color.FromArgb(155, 174, 162);
        controlPen.Color = dimBrush.Color;
        borderPen.Color = Color.FromArgb(42, 58, 49);
        trackBrush.Color = Color.FromArgb(31, 39, 34);
    }

    private async Task RefreshUsageAsync()
    {
        if (!await refreshGate.WaitAsync(0)) return;
        refreshTimer.Stop();
        try
        {
            var current = await liveClient.ReadWeeklyAsync();
            snapshot = current;
            liveConnected = true;
            readError = null;
            UpdatePace();
            UpdateFaceSize();
            await NotifyIfUsageLimitReachedAsync(current);
            Invalidate();
        }
        catch (Exception ex)
        {
            liveConnected = false;
            readError = snapshot is null ? "Usage unavailable" : null;
            UpdatePace();
            Debug.WriteLine(ex);
            Invalidate();
        }
        finally
        {
            refreshTimer.Start();
            refreshGate.Release();
        }
    }

    private void UpdateUsageTrayIcon()
    {
        if (exiting || Disposing || IsDisposed) return;
        var current = snapshot;
        var available = liveConnected && current is not null && double.IsFinite(current.UsedPercent);
        var number = available ? $"{Math.Clamp(100 - current!.UsedPercent, 0, 100):0}" : "—";
        if (number != trayNumber)
        {
            var next = CreateUsageTrayIcon(number);
            var previous = trayIcon.Icon;
            trayIcon.Icon = next;
            trayNumber = number;
            previous?.Dispose();
        }
        var tooltip = available
            ? $"Codex weekly left: {number}% · reset {current!.ResetsAt.ToLocalTime():ddd h:mm tt}"
            : current is null ? "Codex weekly left: unavailable" : "Codex weekly left: offline";
        trayIcon.Text = tooltip[..Math.Min(63, tooltip.Length)];
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Show widget", null, (_, _) => ShowWidget());
        menu.Items.Add("Collapse to progress bar", null, (_, _) => SetBarOnly(true));
        menu.Items.Add("Bring fully on screen", null, (_, _) => { BringFullyOnScreen(); ShowWidget(); });
        menu.Items.Add("Switch face", null, (_, _) => ToggleFace());
        menu.Items.Add("Refresh Claude", null, async (_, _) => await RefreshClaudeAsync(manual: true));
        menu.Items.Add("Refresh now", null, async (_, _) =>
        {
            await Task.WhenAll(RefreshUsageAsync(), RefreshAnnouncementsAsync(), RefreshClaudeAsync(manual: true));
        });
        menu.Items.Add(new ToolStripMenuItem($"MajorCommand Edition · {editionVersion}") { Enabled = false });
        var downloadUpdate = new ToolStripMenuItem("Update available…") { Name = "availableUpdate", Visible = false };
        downloadUpdate.Click += (_, _) => OpenUpdatePage();
        menu.Items.Add(downloadUpdate);
        var checkUpdates = new ToolStripMenuItem("Check for updates") { Name = "checkUpdates" };
        checkUpdates.Click += async (_, _) => await RefreshUpdatesAsync(manual: true);
        menu.Items.Add(checkUpdates);

        var announcementLayout = new ToolStripMenuItem("Reset announcements") { Name = "resetAnnouncements" };
        foreach (var placement in new[] { AnnouncementPlacement.Bottom, AnnouncementPlacement.Crown })
        {
            var item = new ToolStripMenuItem(placement == AnnouncementPlacement.Crown ? "Crown" : "Bottom")
            {
                Name = placement.ToString(), Checked = settings.ResetAnnouncementPlacement == placement
            };
            item.Click += (_, _) =>
            {
                ChangeAnnouncementPlacement(placement);
                foreach (var peer in announcementLayout.DropDownItems.OfType<ToolStripMenuItem>())
                    peer.Checked = peer == item;
            };
            announcementLayout.DropDownItems.Add(item);
        }
        announcementLayout.DropDownItems.Add(new ToolStripSeparator());
        announcementLayout.DropDownItems.Add("Data from Codex Resets ↗", null, (_, _) => OpenAnnouncementSource());
        menu.Items.Add(announcementLayout);

        var refreshInterval = new ToolStripMenuItem("Refresh interval");
        foreach (var option in new[]
                 {
                     (Label: "5 seconds", Seconds: 5),
                     (Label: "15 seconds", Seconds: 15),
                     (Label: "30 seconds", Seconds: 30),
                     (Label: "1 minute", Seconds: 60),
                     (Label: "5 minutes", Seconds: 300)
                 })
        {
            var item = new ToolStripMenuItem(option.Label) { CheckOnClick = true };
            item.Checked = settings.RefreshSeconds == option.Seconds;
            item.Click += (_, _) =>
            {
                settings.RefreshSeconds = option.Seconds;
                refreshTimer.Interval = option.Seconds * 1_000;
                foreach (ToolStripMenuItem peer in refreshInterval.DropDownItems)
                    peer.Checked = peer == item;
                settings.Save();
            };
            refreshInterval.DropDownItems.Add(item);
        }
        menu.Items.Add(refreshInterval);

        var opacity = new ToolStripMenuItem("Transparency");
        foreach (var percent in new[] { 100, 90, 80, 70, 60, 50 })
        {
            var item = new ToolStripMenuItem($"{percent}% opaque") { CheckOnClick = true };
            item.Checked = Math.Abs(settings.Opacity - percent / 100d) < 0.01;
            item.Click += (_, _) =>
            {
                settings.Opacity = percent / 100d;
                Opacity = settings.Opacity;
                foreach (ToolStripMenuItem peer in opacity.DropDownItems) peer.Checked = peer == item;
                settings.Save();
            };
            opacity.DropDownItems.Add(item);
        }
        menu.Items.Add(opacity);

        var hover = new ToolStripMenuItem($"Expand on hover — {(settings.ExpandOnHover ? "On" : "Off")}")
        {
            CheckOnClick = true,
            Checked = settings.ExpandOnHover
        };
        hover.CheckedChanged += (_, _) =>
        {
            settings.ExpandOnHover = hover.Checked;
            if (hover.Checked) QueueHover(true);
            else CancelHover();
            hover.Text = $"Expand on hover — {(hover.Checked ? "On" : "Off")}";
            settings.Save();
        };
        menu.Items.Add(hover);

        var topmost = new ToolStripMenuItem($"Always on top — {(settings.AlwaysOnTop ? "On" : "Off")}")
        {
            CheckOnClick = true,
            Checked = settings.AlwaysOnTop
        };
        topmost.CheckedChanged += (_, _) =>
        {
            settings.AlwaysOnTop = topmost.Checked;
            ApplyAlwaysOnTopState();
            topmost.Text = $"Always on top — {(topmost.Checked ? "On" : "Off")}";
            settings.Save();
        };
        menu.Items.Add(topmost);

        var taskbar = new ToolStripMenuItem($"Show in taskbar — {(settings.ShowInTaskbar ? "On" : "Off")}")
        {
            Name = "showInTaskbar",
            CheckOnClick = true,
            Checked = settings.ShowInTaskbar
        };
        taskbar.CheckedChanged += (_, _) =>
        {
            settings.ShowInTaskbar = taskbar.Checked;
            ApplyTaskbarVisibility(Visible);
            taskbar.Text = $"Show in taskbar — {(taskbar.Checked ? "On" : "Off")}";
            settings.Save();
        };
        menu.Items.Add(taskbar);

        var startsWithWindows = AppSettings.StartsWithWindows;
        var startup = new ToolStripMenuItem($"Start with Windows — {(startsWithWindows ? "On" : "Off")}")
        {
            CheckOnClick = true,
            Checked = startsWithWindows
        };
        startup.Click += (_, _) =>
        {
            try
            {
                AppSettings.StartsWithWindows = startup.Checked;
                settings.WindowsStartupInitialized = true;
                settings.Save();
            }
            catch (Exception ex)
            {
                startup.Checked = !startup.Checked;
                ReportStartupError(ex);
            }
            startup.Text = $"Start with Windows — {(startup.Checked ? "On" : "Off")}";
        };
        menu.Items.Add(startup);
        menu.Items.Add("Usage notifications…", null, (_, _) => ShowNotificationSettings());
        menu.Items.Add("Send test alert", null, async (_, _) =>
        {
            var sent = await SendTestUsageAlertAsync();
            trayIcon.ShowBalloonTip(
                sent ? 2500 : 4500,
                "CodexBar",
                sent ? "Test usage alert triggered." : "Could not trigger test usage alert.",
                sent ? ToolTipIcon.Info : ToolTipIcon.Warning);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { RecordVisibility("exit-request", "tray-menu"); exiting = true; Close(); });
        return menu;
    }

    private async Task RefreshUpdatesAsync(bool manual = false)
    {
        if (updateRefreshing || exiting || Disposing || IsDisposed) return;
        if (manual && DateTimeOffset.UtcNow < nextManualUpdateCheck)
        {
            if (!isolatedPreferences) trayIcon.ShowBalloonTip(2500, "CodexBar — MajorCommand Edition",
                "Please wait five minutes between update checks.", ToolTipIcon.Info);
            return;
        }
        updateRefreshing = true;
        updateTimer.Stop();
        var check = (ToolStripMenuItem)trayIcon.ContextMenuStrip!.Items["checkUpdates"]!;
        var download = (ToolStripMenuItem)trayIcon.ContextMenuStrip.Items["availableUpdate"]!;
        check.Text = "Checking for updates…";
        check.Enabled = false;
        nextManualUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(5);
        try
        {
            var update = await updateClient.ReadAsync(editionVersion, updateCancellation.Token);
            if (exiting || Disposing || IsDisposed) return;
            availableUpdate = update;
            download.Visible = update is not null;
            download.Text = $"Update available · {update?.Version} ↗";
            check.Text = update is null ? "Check for updates — up to date" : "Check for updates";
            check.ToolTipText = $"Last checked {DateTimeOffset.Now:g}. Downloads open in your browser; installation is manual.";
            if (update is not null && notifiedUpdate != update.Version.ToString())
            {
                notifiedUpdate = update.Version.ToString();
                if (!isolatedPreferences) trayIcon.ShowBalloonTip(5000, "CodexBar update available",
                    $"MajorCommand Edition {update.Version} is available. Right-click the widget or tray icon and choose Update available.", ToolTipIcon.Info);
            }
            else if (manual && update is null && !isolatedPreferences)
                trayIcon.ShowBalloonTip(2500, "CodexBar — MajorCommand Edition", "You have the latest release for your update channel.", ToolTipIcon.Info);
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (exiting || Disposing || IsDisposed) return;
            // Keep a previously verified download notice, but do not claim the failed check succeeded.
            check.Text = "Check for updates — unavailable";
            check.ToolTipText = "Could not check GitHub. Try again later; previously found updates remain available.";
            Trace.WriteLine($"CodexBar: update check failed: {ex.GetType().Name}");
            if (manual && !isolatedPreferences) trayIcon.ShowBalloonTip(3500, "CodexBar — MajorCommand Edition",
                "Could not check for updates. Check your connection and try again later.", ToolTipIcon.Warning);
        }
        finally
        {
            updateRefreshing = false;
            if (!exiting && !Disposing && !IsDisposed)
            {
                check.Enabled = true;
                if (!isolatedPreferences) updateTimer.Start();
            }
        }
    }

    private void OpenUpdatePage()
    {
        if (availableUpdate is null) return;
        try { Process.Start(new ProcessStartInfo(availableUpdate.PageUrl) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Trace.WriteLine($"CodexBar: could not open release page: {ex.GetType().Name}");
            trayIcon.ShowBalloonTip(3500, "CodexBar — MajorCommand Edition",
                "Could not open the download page. Visit github.com/majorcommand/codex-bar/releases.", ToolTipIcon.Warning);
        }
    }

    private void ReportStartupError(Exception error)
    {
        Debug.WriteLine(error);
        trayIcon.ShowBalloonTip(4500, "CodexBar",
            "Could not update Windows startup. Try Start with Windows from the right-click menu.",
            ToolTipIcon.Warning);
    }

    private void ShowNotificationSettings()
    {
        using var dialog = new Form
        {
            Text = "Usage notification settings",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(540, 360),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };

        var header = new Label
        {
            Location = new Point(12, 12),
            AutoSize = false,
            Size = new Size(510, 50),
            Text = "When weekly capacity resets to 100% available, CodexBar can send one notification email to your configured address.\n" +
                   "If SMTP fields are blank, CodexBar opens your default mail app with a prefilled draft instead.",
            TextAlign = ContentAlignment.TopLeft
        };
        header.MaximumSize = new Size(510, 0);

        var enabled = new CheckBox
        {
            Text = "Notify when weekly capacity resets to 100% available",
            Location = new Point(12, 50),
            AutoSize = true,
            Checked = settings.NotifyOnUsageLimitReached
        };

        var toLabel = new Label { Location = new Point(12, 84), AutoSize = true, Text = "Notification address:" };
        var toInput = new TextBox { Location = new Point(190, 82), Width = 334, Text = settings.UsageLimitNotificationEmail ?? string.Empty };

        var hostLabel = new Label { Location = new Point(12, 118), AutoSize = true, Text = "SMTP host:" };
        var hostInput = new TextBox { Location = new Point(190, 116), Width = 220, Text = settings.SmtpHost ?? string.Empty };

        var portLabel = new Label { Location = new Point(12, 152), AutoSize = true, Text = "SMTP port:" };
        var portInput = new TextBox { Location = new Point(190, 150), Width = 90, Text = settings.SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture) };

        var ssl = new CheckBox
        {
            Text = "Use SSL/TLS",
            Location = new Point(290, 150),
            AutoSize = true,
            Checked = settings.SmtpUseSsl
        };

        var userLabel = new Label { Location = new Point(12, 186), AutoSize = true, Text = "SMTP user:" };
        var userInput = new TextBox { Location = new Point(190, 184), Width = 334, Text = settings.SmtpUser ?? string.Empty };

        var passwordLabel = new Label { Location = new Point(12, 220), AutoSize = true, Text = "SMTP password:" };
        var passwordInput = new TextBox
        {
            Location = new Point(190, 218),
            Width = 334,
            Text = settings.SmtpPassword ?? string.Empty,
            UseSystemPasswordChar = true
        };

        var fromLabel = new Label { Location = new Point(12, 254), AutoSize = true, Text = "From address (optional):" };
        var fromInput = new TextBox { Location = new Point(190, 252), Width = 334, Text = settings.SmtpFromAddress ?? string.Empty };

        var save = new Button
        {
            Text = "Save",
            Location = new Point(445, 298),
            Width = 75,
            Height = 28
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(362, 298),
            Width = 75,
            Height = 28
        };
        var instructions = new Label
        {
            Location = new Point(12, 298),
            AutoSize = false,
            Width = 340,
            Text = "Tip: Gmail requires an app password (not your login password)."
        };

        if (settings.NotifyOnUsageLimitReached)
            instructions.Text += " Tip 2: Leave SMTP host blank to use mail draft fallback.";
        dialog.Controls.AddRange(new Control[]
        {
            header, enabled, toLabel, toInput, hostLabel, hostInput, portLabel, portInput, ssl,
            userLabel, userInput, passwordLabel, passwordInput, fromLabel, fromInput,
            save, cancel, instructions
        });

        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;

        save.Click += (_, _) =>
        {
            if (enabled.Checked && string.IsNullOrWhiteSpace(toInput.Text))
            {
                MessageBox.Show(dialog, "Notification email is required when notifications are enabled.", "Validation", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            MailAddress? toAddress = null;
            if (!string.IsNullOrWhiteSpace(toInput.Text) &&
                !MailAddress.TryCreate(toInput.Text.Trim(), out toAddress))
            {
                MessageBox.Show(dialog, "Enter a valid notification email address.", "Validation", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            MailAddress? fromAddress = null;
            if (!string.IsNullOrWhiteSpace(fromInput.Text) &&
                !MailAddress.TryCreate(fromInput.Text.Trim(), out fromAddress))
            {
                MessageBox.Show(dialog, "Enter a valid From address.", "Validation", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(hostInput.Text))
            {
                if (!int.TryParse(portInput.Text, out var parsedPort) || parsedPort is < 1 or > 65535)
                {
                    MessageBox.Show(dialog, "SMTP port must be a number between 1 and 65535.", "Validation", MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                settings.SmtpPort = parsedPort;
            }

            var hasUser = !string.IsNullOrWhiteSpace(userInput.Text);
            var hasPassword = !string.IsNullOrWhiteSpace(passwordInput.Text);
            if (hasUser != hasPassword)
            {
                MessageBox.Show(dialog, "SMTP user and password must either both be filled in or both be blank.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (hasUser && !ssl.Checked)
            {
                MessageBox.Show(dialog, "SSL/TLS must be enabled when SMTP credentials are provided.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            settings.NotifyOnUsageLimitReached = enabled.Checked;
            settings.UsageLimitNotificationEmail = toAddress?.Address;
            settings.SmtpHost = NormalizeOrNull(hostInput.Text);
            settings.SmtpUseSsl = ssl.Checked;
            settings.SmtpUser = NormalizeOrNull(userInput.Text);
            settings.SmtpPassword = NormalizeOrNull(passwordInput.Text);
            settings.SmtpFromAddress = fromAddress?.Address;
            settings.Save();
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };

        dialog.ShowDialog(this);
    }

    private static string? NormalizeOrNull(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }

    private static string BuildNotificationBody(UsageSnapshot snapshot, bool isTest = false)
    {
        var resetText = snapshot.ResetsAt.ToLocalTime().ToString("ddd, MMM d h:mm tt");
        var prefix = isTest ? "TEST: " : string.Empty;
        return $"{prefix}Codex has reached 0% usage (100% available) at {DateTimeOffset.Now:ddd, MMM d h:mm tt}. " +
            $"Current window resets at {resetText}.";
    }

    private void DrawWindowControls(Graphics g)
    {
        g.FillEllipse(dimBrush, LogicalWidth - 77, 15, 4, 4);
        g.DrawLine(controlPen, LogicalWidth - 48, 17, LogicalWidth - 40, 17);
        g.DrawLine(controlPen, LogicalWidth - 20, 13, LogicalWidth - 12, 21);
        g.DrawLine(controlPen, LogicalWidth - 12, 13, LogicalWidth - 20, 21);
    }

    private int WindowControlAt(PointF point) => !barOnly && point.Y >= 0 && point.Y <= TitleHeight &&
        point.X >= LogicalWidth - 90 && point.X < LogicalWidth
        ? (int)((point.X - (LogicalWidth - 90)) / 30) : -1;

    private void DrawBarOnly(Graphics g)
    {
        g.DrawRectangle(borderPen, 0.5f, 0.5f, WidgetWidth - 1, BarHeight - 1);
        if (showClaude)
        {
            var now = DateTimeOffset.UtcNow;
            var week = claudeReading?.SevenDay;
            var known = week is not null && !week.Expired(now);
            DrawProgress(g, known ? week!.Remaining : 0,
                known && !claudeReading!.Stale(now) ? RemainingColor(week!.Remaining) : dimBrush.Color);
            return;
        }
        var remaining = snapshot is null ? 0 : Math.Clamp(100 - snapshot.UsedPercent, 0, 100);
        DrawProgress(g, remaining, liveConnected ? RemainingColor(remaining) : dimBrush.Color);
    }

    private void DrawUsageOverview(Graphics g, double remaining)
    {
        var forecast = RemainingAtResetText();
        var warning = ForecastNeedsWarning();
        var color = RemainingColor(remaining);
        usageBrush.Color = color;
        DrawProgress(g, remaining, color);

        var average = pace?.AveragePerDay is { } daily
            ? daily < 100 ? $"{daily:0.#}%" : $"{daily:0}%" : "—";
        using var centered = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        var left = (LogicalWidth - 269) / 2f;
        if (warning) g.FillRectangle(forecastWarningBrush, left + 171, 32, 98, 63);
        g.DrawString($"{remaining:0}%", percentFont, usageBrush, new RectangleF(left, 31, 94, 44), centered);
        DrawPercentage(g, average, compactPercentFont, usageBrush, new RectangleF(left + 93, 34, 77, 34), centered);
        DrawPercentage(g, forecast, compactPercentFont, warning ? forecastWarningTextBrush : usageBrush, new RectangleF(left + 171, 34, 98, 34), centered);
        g.DrawString("Weekly Left", compactLabelFont, dimBrush, new RectangleF(left, 66, 94, 15), centered);
        g.DrawString("Daily usage\nat current rate", compactLabelFont, dimBrush, new RectangleF(left + 93, 66, 77, 29), centered);
        g.DrawString("Est. Remaining\nat Reset", compactLabelFont, warning ? forecastWarningTextBrush : dimBrush, new RectangleF(left + 171, 66, 98, 29), centered);
    }

    private static Font PercentageFont(Graphics g, string text, Font normalFont, float width, StringFormat format)
    {
        var available = width - 4;
        var measured = g.MeasureString(text, normalFont, int.MaxValue, format).Width;
        var size = normalFont.Size * Math.Min(1, available / measured);
        var fitted = new Font(normalFont.FontFamily, size, normalFont.Style, normalFont.Unit);
        // GDI font metrics do not scale exactly at small sizes.
        while (g.MeasureString(text, fitted, int.MaxValue, format).Width > available)
        {
            size *= 0.95f;
            fitted.Dispose();
            fitted = new Font(normalFont.FontFamily, size, normalFont.Style, normalFont.Unit);
        }
        return fitted;
    }

    private static void DrawPercentage(Graphics g, string text, Font normalFont, Brush brush, RectangleF bounds, StringFormat format)
    {
        using var fitted = PercentageFont(g, text, normalFont, bounds.Width, format);
        bounds.Y += (normalFont.GetHeight(g) - fitted.GetHeight(g)) / 2;
        g.DrawString(text, fitted, brush, bounds, format);
    }

    private string RemainingAtResetText()
    {
        if (!liveConnected) return "—";
        if (pace?.RemainingAtReset is { } projected)
        {
            var text = $"{Math.Min(projected, 100):0}%";
            // Avoid a negative-zero label after rounding a small shortfall.
            return text == CultureInfo.CurrentCulture.NumberFormat.NegativeSign + "0%" ? "0%" : text;
        }
        return pace?.Exhausted == true ? "0%" : "—";
    }

    private bool ForecastNeedsWarning() => liveConnected &&
        (RemainingAtResetText() == "0%" || pace?.RemainingAtReset < 0);

    private string OverviewTitle()
    {
        if (snapshot is null) return "CODEX  ·  WAITING";
        if (!liveConnected) return $"CODEX  ·  OFFLINE · {snapshot.CapturedAt.ToLocalTime():h:mm tt}";
        if (pace is null) return "CODEX  ·  RESET UNKNOWN";
        var countdown = pace.DaysUntilReset >= 1 ? FormatDays(pace.DaysUntilReset)
            : pace.DaysUntilReset < 1d / 24 ? "<1h" : $"{pace.DaysUntilReset * 24:0.#}h";
        return $"CODEX  ·  {countdown} until reset";
    }

    private void DrawFooter(Graphics g)
    {
        var more = showResetDetails && snapshot is not null &&
            snapshot.ResetExpirations.Count > VisibleExpirationRows;
        var text = more ? "Scroll · click for Claude · 2/3"
            : showResetDetails ? "Click for Claude · 2/3" : "Click for reset details · 1/3";
        if (showResetDetails && snapshot is not null && !liveConnected)
            text = $"Offline · read {snapshot.CapturedAt.ToLocalTime():h:mm tt} · {(more ? "scroll · " : "")}2/3";
        g.DrawString(text, resetFont, dimBrush, 20, LogicalHeight - 24);
    }

    private void DrawExpandedUsage(Graphics g, double remaining)
    {
        var color = RemainingColor(remaining);
        usageBrush.Color = color;
        g.DrawString($"{remaining:0}%", expandedPercentFont, usageBrush, 16, 36);
        g.DrawString("of week left", bodyFont, dimBrush, 174, 73);
        DrawProgress(g, remaining, color);

        g.DrawString("Daily usage rate", bodyFont, dimBrush, 20, 127);
        g.DrawString("Until reset", bodyFont, dimBrush, 166, 127);
        var average = pace?.AveragePerDay is { } daily ? $"{daily:0.0}%" : "—";
        using var averageFormat = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        DrawPercentage(g, average, expandedValueFont, usageBrush, new RectangleF(20, 148, 130, 28), averageFormat);
        g.DrawString(pace is null ? "—" : FormatDays(pace.DaysUntilReset), expandedValueFont, textBrush, 166, 148);

        string forecast;
        if (!liveConnected) forecast = $"Offline · read {snapshot!.CapturedAt.ToLocalTime():h:mm tt}";
        else if (pace is null) forecast = "Waiting for a current reset time";
        else if (pace.Exhausted) forecast = "Weekly capacity exhausted";
        else if (pace.AveragePerDay is null) forecast = "Waiting for elapsed cycle time";
        else if (pace.AbovePace) forecast = $"Runs out in ~{FormatDays(pace.DaysOfCapacity!.Value)} at this pace";
        else if (pace.RemainingAtReset < 0.5) forecast = "Near weekly limit · little headroom";
        else forecast = $"On track · ~{pace.RemainingAtReset:0}% left at reset";
        var warning = ForecastNeedsWarning();
        if (warning) g.FillRectangle(forecastWarningBrush, 16, 180, WidgetWidth - 32, 25);
        g.DrawString(forecast, expandedForecastFont, warning ? forecastWarningTextBrush : textBrush, 20, 183);
        DrawFooter(g);
    }

    private static string FormatDays(double days)
    {
        if (days < 1d / 24) return "<1 hour";
        if (days < 1) return $"{days * 24:0.#} hours";
        return $"{days:0.#} days";
    }

    private void DrawResetSchedule(Graphics g, UsageSnapshot usage)
    {
        var reset = usage.ResetsAt.ToLocalTime();
        g.DrawString("Full Weekly Reset", bodyFont, dimBrush, 20, 44);
        DrawResetRow(g, $"{reset:ddd'.' MMM d} · {reset:h:mm tt}", ResetTimeLeft(usage.ResetsAt, true), 66);
        g.DrawString("Banked Reset Expiries", bodyFont, dimBrush, 20, 98);
        if (usage.ResetExpirations.Count == 0)
        {
            g.DrawString("No banked resets available", bodyFont, textBrush, 20, ResetRowsTop);
            return;
        }

        var visibleRows = VisibleExpirationRows;
        firstVisibleExpiration = Math.Clamp(firstVisibleExpiration, 0,
            Math.Max(0, usage.ResetExpirations.Count - visibleRows));
        for (var i = firstVisibleExpiration;
             i < Math.Min(usage.ResetExpirations.Count, firstVisibleExpiration + visibleRows); i++)
        {
            var y = ResetRowsTop + (i - firstVisibleExpiration) * ResetRowHeight;
            var expiry = usage.ResetExpirations[i].ToLocalTime();
            DrawResetRow(g, $"{i + 1}. {expiry:ddd'.' MMM d} · {expiry:h:mm tt}",
                ResetTimeLeft(usage.ResetExpirations[i], false), y);
        }
    }

    private string ResetTimeLeft(DateTimeOffset target, bool weeklyReset)
    {
        if (!liveConnected || snapshot is null) return weeklyReset ? "Offline" : "—";
        if (weeklyReset) return pace is null ? "Unknown" : FormatDays(pace.DaysUntilReset);
        var days = (target - snapshot.CapturedAt).TotalDays;
        if (days <= 0) return "Expired";
        if (days < 1) return FormatDays(days);
        var rounded = Math.Round(days, MidpointRounding.AwayFromZero);
        return rounded == 1 ? "1 day" : $"{rounded:0} days";
    }

    private void DrawResetRow(Graphics g, string date, string timeLeft, float y)
    {
        const int columnGap = 8;
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };
        var countdownWidth = (float)Math.Ceiling(g.MeasureString(timeLeft, dateFont, int.MaxValue, format).Width);
        var dateWidth = LogicalWidth - 40 - countdownWidth - columnGap;
        var measuredDateWidth = g.MeasureString(date, dateFont, int.MaxValue, format).Width;
        // Keep ordinary rows at their existing font size; fit longer dates without
        // dropping date/time text or crowding the right-aligned countdown.
        using var fittedDateFont = new Font(dateFont.FontFamily,
            dateFont.Size * Math.Min(1, dateWidth / measuredDateWidth), dateFont.Style, GraphicsUnit.Pixel);
        g.DrawString(date, fittedDateFont, textBrush,
            new RectangleF(20, y + (dateFont.Height - fittedDateFont.Height) / 2f, dateWidth, 22), format);
        format.Alignment = StringAlignment.Far;
        var brush = timeLeft is "Offline" or "Unknown" or "Expired" or "—" ? dimBrush
            : countdownBrush;
        g.DrawString(timeLeft, dateFont, brush,
            new RectangleF(LogicalWidth - 20 - countdownWidth, y, countdownWidth, 22), format);
    }

    private void DrawProgress(Graphics g, double percent, Color color)
    {
        var track = ProgressTrack;
        g.FillRectangle(trackBrush, track);
        usageBrush.Color = color;
        if (percent > 0) g.FillRectangle(usageBrush, track.X, track.Y,
            (int)Math.Round(track.Width * Math.Clamp(percent, 0, 100) / 100), track.Height);
    }

    private static Color RemainingColor(double percent)
    {
        var start = Color.FromArgb(245, 67, 67);
        var mid = Color.FromArgb(246, 190, 55);
        var end = Color.FromArgb(46, 220, 112);
        return percent <= 60 ? Lerp(start, mid, percent / 60) : Lerp(mid, end, (percent - 60) / 40);
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    private async Task NotifyIfUsageLimitReachedAsync(UsageSnapshot current)
    {
        var previousUsedPercent = settings.LastObservedWeeklyUsedPercent;
        var lastNotificationReset = settings.LastUsageLimitNotificationResetAt;
        var alreadyNotifiedForWindow = lastNotificationReset.HasValue &&
            (current.ResetsAt - lastNotificationReset.Value).Duration() <= ResetTimeMatchTolerance;
        var becameFullyAvailable = previousUsedPercent.HasValue &&
            previousUsedPercent.Value > FullAvailabilityUsedPercent &&
            current.UsedPercent <= FullAvailabilityUsedPercent;
        var observationChanged = settings.LastObservedWeeklyUsedPercent != current.UsedPercent;

        // Persist changed observations first. If the app restarts, or notification
        // delivery fails, this transition cannot be handled a second time.
        settings.LastObservedWeeklyUsedPercent = current.UsedPercent;

        if (!settings.NotifyOnUsageLimitReached || !becameFullyAvailable || alreadyNotifiedForWindow)
        {
            if (observationChanged)
                settings.Save();
            return;
        }

        // The reset can happen at any time, so do not infer it from the expected
        // weekly schedule. The usage transition above is the event; the reset time
        // is retained only as a narrow guard against duplicate readings.
        settings.LastUsageLimitNotificationResetAt = current.ResetsAt;
        settings.Save();

        var sent = await SendUsageLimitEmailAsync(current);
        trayIcon.ShowBalloonTip(
            sent ? 2500 : 4500,
            "CodexBar",
            sent
                ? "Weekly capacity reset to 100% available. The email notification was triggered."
                : "Weekly capacity reset to 100% available, but the email notification could not be sent.",
            sent ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private Task<bool> SendTestUsageAlertAsync()
    {
        var test = new UsageSnapshot(
            0,
            DateTimeOffset.UtcNow.AddHours(24),
            DateTimeOffset.UtcNow,
            Array.Empty<DateTimeOffset>());
        return SendUsageLimitEmailAsync(test, isTest: true, forceSend: true);
    }

    private async Task<bool> SendUsageLimitEmailAsync(UsageSnapshot current, bool isTest = false, bool forceSend = false)
    {
        var toAddress = settings.UsageLimitNotificationEmail;
        var smtpHost = settings.SmtpHost;

        if (!forceSend && !settings.NotifyOnUsageLimitReached)
            return false;
        if (string.IsNullOrWhiteSpace(toAddress))
            return false;

        if (string.IsNullOrWhiteSpace(smtpHost))
            return TryLaunchMailClient(current, toAddress!, isTest);

        return await SendViaSmtpAsync(current, toAddress!, smtpHost, isTest);
    }

    private static bool TryLaunchMailClient(UsageSnapshot current, string toAddress, bool isTest)
    {
        try
        {
            if (!MailAddress.TryCreate(toAddress, out var recipient))
                return false;

            var subject = Uri.EscapeDataString(isTest
                ? "TEST: Codex weekly capacity reset alert"
                : "Codex weekly capacity is 100% available");
            var body = Uri.EscapeDataString(BuildNotificationBody(current, isTest));
            var uri = $"mailto:{Uri.EscapeDataString(recipient.Address)}?subject={subject}&body={body}";

            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    private async Task<bool> SendViaSmtpAsync(UsageSnapshot current, string toAddress, string smtpHost, bool isTest)
    {
        try
        {
            var fromAddress = string.IsNullOrWhiteSpace(settings.SmtpFromAddress) ? toAddress : settings.SmtpFromAddress!;
            var subject = isTest
                ? "TEST: Codex weekly capacity reset alert"
                : "Codex weekly capacity is 100% available";
            var body = BuildNotificationBody(current, isTest);

            using var message = new MailMessage(fromAddress, toAddress)
            {
                Subject = subject,
                Body = body
            };

            var hasUser = !string.IsNullOrWhiteSpace(settings.SmtpUser);
            var hasPassword = !string.IsNullOrWhiteSpace(settings.SmtpPassword);
            if (hasUser != hasPassword || (hasUser && !settings.SmtpUseSsl))
                return false;

            var credentialsProvided = hasUser && hasPassword;
            using var client = new SmtpClient(smtpHost, Math.Clamp(settings.SmtpPort, 1, 65535))
            {
                EnableSsl = settings.SmtpUseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false
            };

            if (credentialsProvided)
                client.Credentials = new NetworkCredential(settings.SmtpUser, settings.SmtpPassword);

            using var timeout = new CancellationTokenSource(NotificationDeliveryTimeout);
            await client.SendMailAsync(message, timeout.Token);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    private void ShowWidget()
    {
        SetBarOnly(false);
        RecordVisibility("show-request", "tray-or-menu");
        ApplyTaskbarVisibility(true);
        Show();
        WindowState = FormWindowState.Normal;
        ApplyAlwaysOnTopState();
        Activate();
    }

    private void ApplyTaskbarVisibility(bool widgetVisible)
    {
        ShowInTaskbar = widgetVisible && settings.ShowInTaskbar;
    }

    private void ApplyAlwaysOnTopState()
    {
        TopMost = settings.AlwaysOnTop;
        if (!IsHandleCreated) return;

        if (!SetWindowPos(
            Handle,
            settings.AlwaysOnTop ? HwndTopMost : HwndNoTopMost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate))
            Trace.WriteLine($"CodexBar: could not apply topmost state (Windows error {Marshal.GetLastWin32Error()}).");
    }

    private void MaintainAlwaysOnTop(bool widgetVisible, bool menuVisible, Func<bool> needsRepair, Action apply)
    {
        if (!settings.AlwaysOnTop || !widgetVisible || !Enabled || menuVisible ||
            WindowState != FormWindowState.Normal || mouseDownScreen is not null ||
            exiting || Disposing || IsDisposed || !IsHandleCreated)
            return;
        if (needsRepair())
        {
            RecordVisibility("topmost-repair-attempt");
            apply();
        }
    }

    private bool NeedsTopmostRepair()
    {
        // A window on an inactive virtual desktop must not be brought forward.
        if (IsCloaked(Handle)) return false;
        const int extendedStyle = -20;
        const int topmostStyle = 0x00000008;
        if ((GetWindowStyle(Handle, extendedStyle) & topmostStyle) == 0) return true;
        if (!GetWindowRect(Handle, out var widgetBounds)) return false;

        // The reported style can remain topmost even when ordinary windows cover
        // the widget. Check actual order instead of trusting that flag alone.
        const uint previousWindow = 3;
        var visited = new HashSet<IntPtr>();
        for (var window = GetWindow(Handle, previousWindow);
             window != IntPtr.Zero && visited.Count < 512 && visited.Add(window);
             window = GetWindow(window, previousWindow))
        {
            var visible = IsWindowVisible(window);
            var topmost = (GetWindowStyle(window, extendedStyle) & topmostStyle) != 0;
            if (!visible || topmost)
                continue;
            if (GetWindowRect(window, out var otherBounds) &&
                IsOrdinaryOccluder(widgetBounds.Rectangle, otherBounds.Rectangle,
                    visible, topmost, IsCloaked(window)))
                return true;
        }
        return false;
    }

    private static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool IsOrdinaryOccluder(Rectangle widgetBounds, Rectangle otherBounds,
        bool visible, bool topmost, bool cloaked) =>
        visible && !topmost && !cloaked && widgetBounds.IntersectsWith(otherBounds);

    private void HideToTray(string reason)
    {
        RecordVisibility("hide-request", reason);
        CancelHover();
        FlushPositionSave();
        Hide();
        RecordVisibility("hide-completed", reason);
        ApplyTaskbarVisibility(false);
        trayIcon.ShowBalloonTip(1500, "CodexBar", "Still updating in the notification area.", ToolTipIcon.Info);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        RecordVisibility("form-closing", $"reason={e.CloseReason};cancel={e.Cancel}");
        if (!exiting)
        {
            e.Cancel = true;
            HideToTray($"form-closing:{e.CloseReason}");
            return;
        }
        refreshTimer.Stop();
        topmostTimer.Stop();
        announcementTimer.Stop();
        announcementCancellation.Cancel();
        CancelHover();
        FlushPositionSave();
        trayIcon.Visible = false;
    }

    private void QueuePositionSave()
    {
        if (isolatedPreferences || WindowState != FormWindowState.Normal) return;
        positionSaveTimer.Stop();
        positionSaveTimer.Start();
    }

    private void FlushPositionSave()
    {
        if (!positionSaveTimer.Enabled) return;
        positionSaveTimer.Stop();
        SavePositionNow();
    }

    private void SavePositionNow()
    {
        if (isolatedPreferences || WindowState != FormWindowState.Normal) return;
        var savedLocation = compactAnchor ?? barRestoreLocation ?? Location;
        settings.X = savedLocation.X;
        settings.Y = savedLocation.Y;
        settings.Save();
    }

    private Point KeepVisible(Point desired)
    {
        var screen = Screen.FromRectangle(new Rectangle(desired, Size));
        return PartiallyVisibleLocation(desired, Size, screen.Bounds, screen.WorkingArea, barOnly);
    }

    private static Point PartiallyVisibleLocation(Point desired, Size size, Rectangle screen, Rectangle workArea, bool bar)
    {
        var visibleWidth = (int)Math.Ceiling(size.Width * 0.2);
        var visibleHeight = (int)Math.Ceiling(size.Height * 0.2);
        return new Point(Math.Clamp(desired.X, screen.Left - size.Width + visibleWidth, screen.Right - visibleWidth),
            bar ? Math.Clamp(desired.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height))
                : Math.Clamp(desired.Y, screen.Top - size.Height + visibleHeight, screen.Bottom - visibleHeight));
    }

    private static Point FullyVisibleLocation(Point desired, Size size, Rectangle screen) => new(
        Math.Clamp(desired.X, screen.Left, Math.Max(screen.Left, screen.Right - size.Width)),
        Math.Clamp(desired.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - size.Height)));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            updateCancellation.Cancel();
            updateClient.Dispose();
            updateTimer.Dispose();
            updateCancellation.Dispose();
            announcementCancellation.Cancel();
            announcementClient.Dispose();
            announcementTimer.Dispose();
            announcementCancellation.Dispose();
            announcementTip.Dispose();
            liveClient.Dispose();
            claudeTimer.Dispose();
            claudeCancellation.Cancel();
            claudeClient.Dispose();
            claudeCancellation.Dispose();
            refreshTimer.Dispose();
            positionSaveTimer.Dispose();
            hoverTimer.Dispose();
            topmostTimer.Dispose();
            var lastTrayIcon = trayIcon.Icon;
            trayIcon.Dispose();
            lastTrayIcon?.Dispose();
            appIcon.Dispose();
            borderPen.Dispose();
            controlPen.Dispose();
            titleFont.Dispose();
            percentFont.Dispose();
            resetFont.Dispose();
            announcementSourceFont.Dispose();
            announcementBrush.Dispose();
            compactFont.Dispose();
            compactPercentFont.Dispose();
            compactLabelFont.Dispose();
            expandedPercentFont.Dispose();
            expandedValueFont.Dispose();
            expandedForecastFont.Dispose();
            bodyFont.Dispose();
            dateFont.Dispose();
            dimBrush.Dispose();
            statusOkBrush.Dispose();
            statusOfflineBrush.Dispose();
            textBrush.Dispose();
            trackBrush.Dispose();
            usageBrush.Dispose();
            countdownBrush.Dispose();
            forecastWarningBrush.Dispose();
            forecastWarningTextBrush.Dispose();
            refreshGate.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(8, 10, 9));
        using var ring = new Pen(Color.FromArgb(46, 220, 112), 3);
        g.FillEllipse(background, 2, 2, 28, 28);
        g.DrawArc(ring, 6, 6, 20, 20, -90, 285);
        using var font = new Font("Segoe UI", 10, FontStyle.Bold);
        using var brush = new SolidBrush(Color.White);
        g.DrawString("C", font, brush, 9, 7);
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static Icon CreateUsageTrayIcon(string number)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(NormalBackground);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var normalFont = new Font("Segoe UI", 29f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        using var fitted = PercentageFont(g, number, normalFont, 32, format);
        g.DrawString(number, fitted, Brushes.White, new RectangleF(0, 0, 32, 32), format);
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
}
