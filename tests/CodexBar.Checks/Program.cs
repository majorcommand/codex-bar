using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexBar;

internal static class Checks
{
    private static int passed;
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowStyle(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : null;
        if (output is not null) Directory.CreateDirectory(output);

        var diagnosticFolder = Path.Combine(output ?? Path.GetTempPath(), "visibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(diagnosticFolder);
        var rotationPath = Path.Combine(diagnosticFolder, "rotation.log");
        var rotationLog = new VisibilityLog(rotationPath, maxBytes: 2048);
        for (var i = 0; i < 30; i++) rotationLog.Write("rotation-check", $"entry={i};" + new string('x', 160));
        Check("Visibility log keeps only current and previous bounded files", File.Exists(rotationPath + ".previous") &&
            new FileInfo(rotationPath).Length <= 2048 && new FileInfo(rotationPath + ".previous").Length <= 2048 &&
            Directory.GetFiles(diagnosticFolder, "rotation.log*").Length == 2);
        Check("Rotation retains newest diagnostic entries", ReadVisibilityLog(rotationPath).Last().GetProperty("details").GetString()!.StartsWith("entry=29;"));
        var escapedPath = Path.Combine(diagnosticFolder, "escaped.log");
        var escapedLog = new VisibilityLog(escapedPath);
        escapedLog.Write("test-event", "line one\nline two");
        var escapedEntry = ReadVisibilityLog(escapedPath).Single();
        Check("Diagnostic records have timestamps and cannot inject extra lines", File.ReadAllLines(escapedPath).Length == 1 &&
            escapedEntry.GetProperty("details").GetString() == "line one\nline two" &&
            escapedEntry.GetProperty("time").GetDateTimeOffset() != default &&
            escapedEntry.GetProperty("pid").GetInt32() == Environment.ProcessId);
        var blockedFolder = Path.Combine(diagnosticFolder, "blocked");
        File.WriteAllText(blockedFolder, "isolated fixture");
        var failingLog = new VisibilityLog(Path.Combine(blockedFolder, "visibility.log"));
        Check("Diagnostic write failure is reported without throwing", !failingLog.Write("test", "failure") && failingLog.LastError is not null);
        File.Delete(blockedFolder);
        Check("Diagnostics can recover after a write failure", failingLog.Write("test", "recovered") && failingLog.LastError is null);
        var diagnosticPath = Path.Combine(diagnosticFolder, "widget.log");
        var widgetLog = new VisibilityLog(diagnosticPath);

        // Exercise startup policy without changing this PC's registry or preferences.
        var startupDefaults = new AppSettings();
        var startupWrites = 0;
        Check("First launch enables Windows startup once", startupDefaults.ApplyStartupDefault(() => startupWrites++) &&
            startupDefaults.WindowsStartupInitialized && startupWrites == 1);
        var restartedPreferences = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(startupDefaults))!;
        Check("Restart preserves a later startup off choice", !restartedPreferences.ApplyStartupDefault(() => startupWrites++) &&
            startupWrites == 1);
        var failedStartup = new AppSettings();
        var failureObserved = false;
        try { failedStartup.ApplyStartupDefault(() => throw new UnauthorizedAccessException("Isolated startup failure")); }
        catch (UnauthorizedAccessException) { failureObserved = true; }
        Check("Startup failure remains observable and retryable", failureObserved && !failedStartup.WindowsStartupInitialized &&
            failedStartup.ApplyStartupDefault(() => startupWrites++) && startupWrites == 2);

        var light = UsagePace.Calculate(10, Now.AddDays(4), Now)!;
        Check("10% after three days", Near(light.AveragePerDay, 10d / 3) &&
            Near(light.DaysOfCapacity, 27) && Near(light.RemainingAtReset, 76.6666666667) && !light.AbovePace);
        var heavy = UsagePace.Calculate(50, Now.AddDays(5), Now)!;
        Check("50% after two days", Near(heavy.AveragePerDay, 25) &&
            Near(heavy.DaysOfCapacity, 2) && heavy.AbovePace);
        Check("Partial days", Near(UsagePace.Calculate(10, Now.AddDays(5.5), Now)!.AveragePerDay, 10d / 1.5));
        Check("No usage has no exhaustion estimate", UsagePace.Calculate(0, Now.AddDays(4), Now) is
            { AveragePerDay: 0, DaysOfCapacity: null, RemainingAtReset: 100, AbovePace: false });
        Check("Early cycle avoids prediction", UsagePace.Calculate(10, Now.AddHours(167), Now) is
            { AveragePerDay: null, AbovePace: false });
        Check("Exhausted even early", UsagePace.Calculate(100, Now.AddHours(167), Now) is
            { Exhausted: true, AbovePace: true, DaysOfCapacity: 0 });
        Check("Six-hour boundary", UsagePace.Calculate(1, Now.AddHours(162), Now)!.AveragePerDay is not null);
        Check("Exact sustainable pace", UsagePace.Calculate(100d * 2 / 7, Now.AddDays(5), Now) is
            { AbovePace: false });
        Check("Warning stays stable around boundary", !UsagePace.Calculate(100.25 * 2 / 7, Now.AddDays(5), Now)!.AbovePace &&
            UsagePace.Calculate(99.75 * 2 / 7, Now.AddDays(5), Now, true)!.AbovePace &&
            !UsagePace.Calculate(99 * 2 / 7, Now.AddDays(5), Now, true)!.AbovePace);
        Check("Invalid and expired data", UsagePace.Calculate(double.NaN, Now.AddDays(4), Now) is null &&
            UsagePace.Calculate(double.PositiveInfinity, Now.AddDays(4), Now) is null &&
            UsagePace.Calculate(-1, Now.AddDays(4), Now) is null &&
            UsagePace.Calculate(101, Now.AddDays(4), Now) is null &&
            UsagePace.Calculate(10, Now, Now) is null &&
            UsagePace.Calculate(10, Now.AddDays(8), Now) is null);

        // Exercise the real form with isolated preferences. Do not show it, pump
        // its refresh timers, write settings, send email, or contact Codex.
        using var form = new WidgetForm(new AppSettings
        {
            X = 100, Y = 100, AlwaysOnTop = false, NotifyOnUsageLimitReached = false
        }, widgetLog);
        ((System.Windows.Forms.Timer)Get(form, "refreshTimer")!).Stop();
        ((System.Windows.Forms.Timer)Get(form, "positionSaveTimer")!).Stop();
        var topmostTimer = (System.Windows.Forms.Timer)Get(form, "topmostTimer")!;
        Check("Topmost recovery runs independently of usage refresh", topmostTimer.Enabled && topmostTimer.Interval == 2_000);
        topmostTimer.Stop();
        ((NotifyIcon)Get(form, "trayIcon")!).Visible = false;
        form.CreateControl();
        Check("Taskbar button stays off by default", !form.ShowInTaskbar);
        Check("Default preferences match requested hover, topmost and taskbar choices", new AppSettings() is
            { ExpandOnHover: false, AlwaysOnTop: true, ShowInTaskbar: false });
        Check("Overview preserves its 290 by 100 body plus announcement strip", form.ClientSize ==
            new Size((int)Math.Round(290 * form.DeviceDpi / 96f), (int)Math.Round(124 * form.DeviceDpi / 96f)));
        Save(form, output, "loading");

        SetSnapshot(form, 10, 4, 2);
        Check("Main face has normal background", form.BackColor == Color.FromArgb(8, 10, 9));
        Save(form, output, "on-track");
        SetSnapshot(form, 5.1, 6.3, 2);
        Check("Overview title gives remaining cycle time", (string)Invoke(form, "OverviewTitle")! == "CODEX  ·  6.3 days until reset");
        Save(form, output, "requested-layout");
        SetSnapshot(form, 10, 23.9 / 24, 2);
        Save(form, output, "hours-to-reset");
        SetSnapshot(form, 0, 4, 2);
        Save(form, output, "full-capacity");
        SetSnapshot(form, 99.75, 6.75, 2);
        Save(form, output, "high-daily-average");
        SetSnapshot(form, 100, 6.9, 2);
        Save(form, output, "early-exhausted");
        SetSnapshot(form, 10, 4, 2);
        Click(form, 40, 70);
        Check("Click opens details", (bool)Get(form, "showResetDetails")!);
        Check("Details shares the requested 290 pixel width", form.ClientSize.Width == (int)Math.Round(290 * form.DeviceDpi / 96f));
        Check("Two numbered expiry rows fit with the announcement strip", form.ClientSize.Height == (int)Math.Round(222 * form.DeviceDpi / 96f));
        var smallHeight = form.ClientSize.Height;
        Save(form, output, "reset-details");
        SetSnapshot(form, 5.1, 6.3, 2);
        Check("Weekly row retains fractional reset countdown", (string)Invoke(form, "ResetTimeLeft", Now.AddDays(6.3), true)! == "6.3 days");
        Check("Banked expiry countdown uses the observation time", (string)Invoke(form, "ResetTimeLeft", Now.AddDays(21.7), false)! == "22 days");
        Check("Hours and imminent expiries remain readable", (string)Invoke(form, "ResetTimeLeft", Now.AddHours(12), false)! == "12 hours" &&
            (string)Invoke(form, "ResetTimeLeft", Now.AddMinutes(30), false)! == "<1 hour");
        Check("Expired banked resets are explicit", (string)Invoke(form, "ResetTimeLeft", Now, false)! == "Expired" &&
            (string)Invoke(form, "ResetTimeLeft", Now.AddDays(-1), false)! == "Expired");
        Save(form, output, "requested-details");
        SetSnapshot(form, 10, 4, 1);
        Save(form, output, "single-expiry");
        Set(form, "liveConnected", false);
        Invoke(form, "UpdatePace");
        Check("Offline reset countdowns do not look live", (string)Invoke(form, "ResetTimeLeft", Now.AddDays(4), true)! == "Offline" &&
            (string)Invoke(form, "ResetTimeLeft", Now.AddDays(22), false)! == "—");
        Save(form, output, "offline-details");
        SetSnapshot(form, 10, 8, 2);
        Check("Unknown weekly timing stays explicit", (string)Invoke(form, "ResetTimeLeft", Now.AddDays(8), true)! == "Unknown");
        Save(form, output, "unknown-reset-details");
        Set(form, "snapshot", new UsageSnapshot(10, Now.AddDays(4), Now,
            new[] { Now.AddMinutes(-1), Now.AddMinutes(30), Now.AddHours(12), Now.AddDays(1), Now.AddDays(21.7) }));
        Invoke(form, "UpdatePace");
        Invoke(form, "UpdateFaceSize");
        Save(form, output, "expiry-boundaries");
        Set(form, "snapshot", new UsageSnapshot(10, Now.AddHours(23.9), Now,
            new[] { new DateTimeOffset(2026, 11, 25, 2, 59, 0, TimeSpan.Zero), Now.AddHours(23.9) }));
        Invoke(form, "UpdatePace");
        Invoke(form, "UpdateFaceSize");
        Save(form, output, "long-dates-and-hours");
        SetSnapshot(form, 10, 4, 2);
        Click(form, 40, 70);
        Check("Click returns to overview", !(bool)Get(form, "showResetDetails")!);
        Check("Returning restores compact dimensions", form.ClientSize ==
            new Size((int)Math.Round(290 * form.DeviceDpi / 96f), (int)Math.Round(124 * form.DeviceDpi / 96f)));
        var scale = form.DeviceDpi / 96f;
        var tinyMove = new MouseEventArgs(MouseButtons.Left, 1, (int)(41 * scale), (int)(70 * scale), 0);
        Invoke(form, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, (int)(40 * scale), (int)(70 * scale), 0));
        Invoke(form, "OnMouseMove", tinyMove);
        Invoke(form, "OnMouseUp", tinyMove);
        Check("Small pointer movement remains a click", (bool)Get(form, "showResetDetails")!);
        Invoke(form, "ToggleFace");

        Invoke(form, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, (int)(40 * scale), (int)(70 * scale), 0));
        form.Capture = false;
        Invoke(form, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, (int)(40 * scale), (int)(70 * scale), 0));
        Check("Lost capture cancels the click", !(bool)Get(form, "showResetDetails")!);

        var original = form.Location;
        Invoke(form, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 40, 70, 0));
        Invoke(form, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 1, 80, 100, 0));
        Invoke(form, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 40, 70, 0));
        Check("Drag moves without flipping", form.Location != original && !(bool)Get(form, "showResetDetails")!);
        Invoke(form, "OnKeyDown", new KeyEventArgs(Keys.Space));
        Check("Keyboard switches face", (bool)Get(form, "showResetDetails")!);

        SetSnapshot(form, 50, 5, 2);
        Check("Risk colours details red", form.BackColor == Color.FromArgb(132, 38, 48));
        Save(form, output, "warning-details");
        Invoke(form, "ToggleFace");
        Save(form, output, "above-pace");
        Set(form, "liveConnected", false);
        Invoke(form, "UpdatePace");
        Check("Offline clears live warning", form.BackColor == Color.FromArgb(8, 10, 9));
        Check("Offline title identifies stale data", ((string)Invoke(form, "OverviewTitle")!).Contains("OFFLINE"));
        Save(form, output, "offline");

        SetSnapshot(form, 0, 7 - 1d / 24, 0);
        Check("New cycle clears warning", !(bool)Get(form, "abovePace")!);
        Save(form, output, "new-cycle");
        Invoke(form, "ToggleFace");
        Save(form, output, "no-banked-resets");
        SetSnapshot(form, 10, 4, 6);
        Check("Details grows rather than shrinking text", form.ClientSize.Height > smallHeight);
        Save(form, output, "many-resets");
        SetSnapshot(form, 10, 4, 100);
        Check("Details stays on screen", form.Height <= Screen.FromControl(form).WorkingArea.Height);
        Invoke(form, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 40, 150, -120));
        Check("Overflow resets remain reachable", (int)Get(form, "firstVisibleExpiration")! == 1);
        Save(form, output, "overflow-resets");
        for (var i = 0; i < 100; i++)
            Invoke(form, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 40, 150, -120));
        var lastFirstRow = (int)Get(form, "firstVisibleExpiration")!;
        Check("Last expiry remains reachable with one-line scrolling", lastFirstRow > 1 && lastFirstRow < 100);
        Save(form, output, "last-expiries");
        SetSnapshot(form, 10, 4, 1);
        Save(form, output, "shrunk-expiry-list");
        Check("Shortened expiry list resets the visible index", (int)Get(form, "firstVisibleExpiration")! == 0);

        Invoke(form, "ToggleFace");
        var preferences = (AppSettings)Get(form, "settings")!;
        var hoverTimer = (System.Windows.Forms.Timer)Get(form, "hoverTimer")!;
        EnterBody(form);
        Check("Hover is optional and off by default", !preferences.ExpandOnHover && !hoverTimer.Enabled &&
            !(bool)Get(form, "hoverExpanded")!);
        Check("Older settings keep hover disabled", !System.Text.Json.JsonSerializer
            .Deserialize<AppSettings>("{\"AlwaysOnTop\":false}")!.ExpandOnHover);
        preferences.ExpandOnHover = true;
        Check("Hover preference survives serialization", System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(preferences))!.ExpandOnHover);
        SetSnapshot(form, 10, 4, 2);
        MovePointer(form, 40, 17);
        HoverTick(form, false);
        Check("Header hover never expands", !hoverTimer.Enabled && !(bool)Get(form, "hoverExpanded")!);
        EnterBody(form);
        Check("Entry delays expansion", hoverTimer.Enabled && hoverTimer.Interval == 350 &&
            !(bool)Get(form, "hoverExpanded")!);
        MovePointer(form, 40, 17);
        Check("Moving to header cancels pending expansion", !hoverTimer.Enabled);
        EnterBody(form);
        HoverTick(form, false);
        Check("Passing pointer does not expand", !(bool)Get(form, "hoverExpanded")!);
        EnterBody(form);
        HoverTick(form, true, menuVisible: true);
        Check("Open menu postpones expansion", hoverTimer.Enabled && !(bool)Get(form, "hoverExpanded")!);
        HoverTick(form, true);
        Check("Hover expands vertically at the same width", (bool)Get(form, "hoverExpanded")! &&
            form.ClientSize == new Size((int)Math.Round(290 * scale), (int)Math.Round(260 * scale)));
        Save(form, output, "hover-expanded");
        Invoke(form, "OnMouseLeave", EventArgs.Empty);
        Check("Leaving delays collapse", hoverTimer.Enabled && hoverTimer.Interval == 450 &&
            (bool)Get(form, "hoverExpanded")!);
        EnterBody(form);
        Check("Reentry cancels collapse", !hoverTimer.Enabled && (bool)Get(form, "hoverExpanded")!);
        Invoke(form, "OnMouseLeave", EventArgs.Empty);
        HoverTick(form, true);
        Check("Pointer still inside keeps expanded content", (bool)Get(form, "hoverExpanded")!);
        Set(form, "mouseDownScreen", form.Location);
        HoverTick(form, false);
        Check("Dragging postpones resize", hoverTimer.Enabled && (bool)Get(form, "hoverExpanded")!);
        Set(form, "mouseDownScreen", null!);
        HoverTick(form, false);
        Check("Leaving collapses to original size", !(bool)Get(form, "hoverExpanded")! &&
            form.ClientSize == new Size((int)Math.Round(290 * scale), (int)Math.Round(124 * scale)));
        Click(form, 40, 17);
        Check("Header click is reserved for dragging", !(bool)Get(form, "showResetDetails")!);

        var area = Screen.FromControl(form).WorkingArea;
        form.Location = new Point(area.Right - form.Width, area.Bottom - form.Height);
        var compactPosition = form.Location;
        EnterBody(form);
        HoverTick(form, true);
        Check("Expansion stays within screen bounds", area.Contains(form.Bounds));
        Invoke(form, "QueueHover", false);
        HoverTick(form, false);
        Check("Collapse restores compact position", form.Location == compactPosition);
        Invoke(form, "ToggleFace");
        Check("Details fits inward at screen edge", area.Contains(form.Bounds));
        Invoke(form, "ToggleFace");
        Check("Returning from details restores the original position", form.Location == compactPosition);
        for (var i = 0; i < 3; i++)
        {
            Invoke(form, "ToggleFace");
            Invoke(form, "ToggleFace");
        }
        Check("Repeated page switches do not drift", form.Location == compactPosition);
        Invoke(form, "ToggleFace");
        SetSnapshot(form, 10, 4, 6);
        Check("Details refresh preserves compact anchor", (Point)Get(form, "compactAnchor")! == compactPosition);
        SetSnapshot(form, 10, 4, 2);
        var expandedPosition = form.Location;
        Invoke(form, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 80, 70, 0));
        Invoke(form, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 1, 40, 40, 0));
        var dragDelta = new Size(form.Left - expandedPosition.X, form.Top - expandedPosition.Y);
        Invoke(form, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 40, 40, 0));
        var draggedPosition = form.Location;
        SetSnapshot(form, 10, 4, 2);
        Check("Refresh keeps dragged details in place", form.Location == draggedPosition);
        Invoke(form, "ToggleFace");
        Check("Dragging details translates compact position without resize drift", form.Location == compactPosition + dragDelta);
        compactPosition = form.Location;

        SetSnapshot(form, 50, 5, 2);
        EnterBody(form);
        HoverTick(form, true);
        Check("Hover retains warning colour", form.BackColor == Color.FromArgb(132, 38, 48));
        Save(form, output, "hover-warning");
        Click(form, 40, 70);
        Check("Click on expanded face still opens reset details", (bool)Get(form, "showResetDetails")! &&
            !(bool)Get(form, "hoverExpanded")!);
        Check("Hover-to-details preserves compact anchor", (Point)Get(form, "compactAnchor")! == compactPosition);
        EnterBody(form);
        Check("Details face does not hover-resize", !hoverTimer.Enabled);
        Invoke(form, "ToggleFace");
        Check("Hover-to-details-to-compact restores location", form.Location == compactPosition);
        EnterBody(form);
        HoverTick(form, true);
        expandedPosition = form.Location;
        Invoke(form, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 80, 70, 0));
        Invoke(form, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 1, 40, 40, 0));
        dragDelta = new Size(form.Left - expandedPosition.X, form.Top - expandedPosition.Y);
        Invoke(form, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 40, 40, 0));
        draggedPosition = form.Location;
        SetSnapshot(form, 50, 5, 2);
        Check("Refresh keeps dragged hover content in place", form.Location == draggedPosition);
        preferences.ExpandOnHover = false;
        Invoke(form, "CancelHover");
        Check("Disabling hover collapses immediately", !(bool)Get(form, "hoverExpanded")! && !hoverTimer.Enabled);
        Check("Dragging hover content preserves compact offset", form.Location == compactPosition + dragDelta);

        preferences.ExpandOnHover = true;
        form.Location = new Point(area.Right - form.Width, area.Bottom - form.Height);
        compactPosition = form.Location;
        EnterBody(form);
        HoverTick(form, true);
        MovePointer(form, 80, 17);
        Check("Header offers a drag cursor and queues collapse", form.Cursor == Cursors.SizeAll && hoverTimer.Enabled);
        expandedPosition = form.Location;
        var headerDown = new MouseEventArgs(MouseButtons.Left, 1, (int)(80 * scale), (int)(17 * scale), 0);
        Invoke(form, "OnMouseDown", headerDown);
        Check("Press holds geometry until drag starts", (bool)Get(form, "hoverExpanded")! && !hoverTimer.Enabled);
        var headerMove = new MouseEventArgs(MouseButtons.Left, 1, (int)(60 * scale), (int)(17 * scale), 0);
        Invoke(form, "OnMouseMove", headerMove);
        Check("Header drag shrinks immediately without flipping", !(bool)Get(form, "hoverExpanded")! &&
            !(bool)Get(form, "showResetDetails")! && form.Height == (int)Math.Round(124 * scale));
        Check("Shrinking keeps header under pointer", form.Location == new Point(
            expandedPosition.X + (int)(60 * scale) - (int)(80 * scale), expandedPosition.Y));
        var bottomMove = new MouseEventArgs(MouseButtons.Left, 1, (int)(60 * scale),
            (int)(17 * scale) + compactPosition.Y - expandedPosition.Y, 0);
        Invoke(form, "OnMouseMove", bottomMove);
        Check("Header drag can reach original bottom edge", form.Bottom == area.Bottom);
        Invoke(form, "OnMouseLeave", EventArgs.Empty);
        Invoke(form, "OnMouseUp", headerDown);
        MovePointer(form, 40, 70);
        HoverTick(form, true);
        Check("Release does not immediately re-expand over body", !hoverTimer.Enabled && !(bool)Get(form, "hoverExpanded")!);
        MovePointer(form, 45, 70);
        Check("Movement within body remains suppressed after drag", !hoverTimer.Enabled);
        MovePointer(form, 40, 17);
        MovePointer(form, 40, 70);
        Check("Leaving and reentering body rearms hover", hoverTimer.Enabled && hoverTimer.Interval == 350);
        HoverTick(form, true);
        MovePointer(form, 40, 17);
        HoverTick(form, false);
        Check("Hovering header collapses the expanded face", !(bool)Get(form, "hoverExpanded")!);
        EnterBody(form);
        Invoke(form, "OnMouseDown", headerDown);
        Invoke(form, "OnMouseMove", headerMove);
        Invoke(form, "OnMouseUp", headerDown);
        Check("Compact header drag cancels pending hover", !hoverTimer.Enabled && !(bool)Get(form, "hoverExpanded")!);
        preferences.ExpandOnHover = false;
        Invoke(form, "CancelHover");

        var recoveryHandle = form.Handle;
        var repairs = 0;
        var probes = 0;
        Func<bool> covered = () => { probes++; return true; };
        Action recordRepair = () => repairs++;
        preferences.AlwaysOnTop = true;
        Invoke(form, "MaintainAlwaysOnTop", false, false, covered, recordRepair);
        Check("Recovery respects a deliberately hidden widget", probes == 0 && repairs == 0 && !form.Visible);
        preferences.AlwaysOnTop = false;
        Invoke(form, "MaintainAlwaysOnTop", true, false, covered, recordRepair);
        Check("Recovery respects always-on-top off", probes == 0 && repairs == 0);
        preferences.AlwaysOnTop = true;
        Invoke(form, "MaintainAlwaysOnTop", true, true, covered, recordRepair);
        form.Enabled = false;
        Invoke(form, "MaintainAlwaysOnTop", true, false, covered, recordRepair);
        form.Enabled = true;
        Set(form, "mouseDownScreen", new Point(100, 100));
        Invoke(form, "MaintainAlwaysOnTop", true, false, covered, recordRepair);
        Set(form, "mouseDownScreen", null!);
        Set(form, "exiting", true);
        Invoke(form, "MaintainAlwaysOnTop", true, false, covered, recordRepair);
        Set(form, "exiting", false);
        form.WindowState = FormWindowState.Minimized;
        Invoke(form, "MaintainAlwaysOnTop", true, false, covered, recordRepair);
        form.WindowState = FormWindowState.Normal;
        Check("Recovery pauses for menus, dialogs, drags, exit and minimization", probes == 0 && repairs == 0);
        Invoke(form, "MaintainAlwaysOnTop", true, false, (Func<bool>)(() => false), recordRepair);
        Check("Healthy window order is left alone", repairs == 0);
        Invoke(form, "MaintainAlwaysOnTop", true, false, covered, recordRepair);
        Check("Coverage triggers repair even when the topmost preference remains on", probes == 1 && repairs == 1);

        var widgetRect = new Rectangle(100, 100, 290, 100);
        var overlappingRect = new Rectangle(150, 120, 500, 400);
        var separateRect = new Rectangle(600, 100, 100, 100);
        Check("Ordinary overlapping window above needs recovery", Occludes(widgetRect, overlappingRect, true, false, false));
        Check("Other topmost windows are not fought", !Occludes(widgetRect, overlappingRect, true, true, false));
        Check("Hidden and inactive-desktop windows are ignored", !Occludes(widgetRect, overlappingRect, false, false, false) &&
            !Occludes(widgetRect, overlappingRect, true, false, true));
        Check("Separate and edge-touching windows do not trigger recovery", !Occludes(widgetRect, separateRect, true, false, false) &&
            !Occludes(widgetRect, new Rectangle(widgetRect.Right, 100, 100, 100), true, false, false));

        form.TopMost = false;
        Check("Native detector recognizes a lost topmost style", (bool)Invoke(form, "NeedsTopmostRepair")!);
        var foregroundBeforeRepair = GetForegroundWindow();
        var boundsBeforeRepair = form.Bounds;
        Invoke(form, "MaintainAlwaysOnTop", true, false,
            (Func<bool>)(() => (bool)Invoke(form, "NeedsTopmostRepair")!),
            (Action)(() => Invoke(form, "ApplyAlwaysOnTopState")));
        Check("Native repair reapplies actual topmost style", form.TopMost && (GetWindowStyle(recoveryHandle, -20) & 8) != 0);
        Check("Native repair preserves focus, position, size and hidden state", GetForegroundWindow() == foregroundBeforeRepair &&
            form.Bounds == boundsBeforeRepair && !form.Visible);
        preferences.AlwaysOnTop = false;
        Invoke(form, "ApplyAlwaysOnTopState");
        Check("Turning always-on-top off removes the native style", !form.TopMost && (GetWindowStyle(recoveryHandle, -20) & 8) == 0);

        Check("Missing taskbar setting uses off default", !System.Text.Json.JsonSerializer
            .Deserialize<AppSettings>("{\"AlwaysOnTop\":false}")!.ShowInTaskbar);
        Check("Explicit saved choices override the defaults", System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            "{\"AlwaysOnTop\":false,\"ExpandOnHover\":true,\"ShowInTaskbar\":true}") is
            { AlwaysOnTop: false, ExpandOnHover: true, ShowInTaskbar: true });
        var taskbarPosition = form.Location;
        var taskbarSize = form.Size;
        preferences.ShowInTaskbar = false;
        Invoke(form, "ApplyTaskbarVisibility", true);
        Check("Taskbar off applies while widget is intended visible", !form.ShowInTaskbar);
        Check("Taskbar setting preserves widget geometry and face", form.Location == taskbarPosition &&
            form.Size == taskbarSize && !(bool)Get(form, "showResetDetails")!);
        Check("Taskbar off survives serialization", !System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(preferences))!.ShowInTaskbar);
        Invoke(form, "ApplyTaskbarVisibility", false);
        Invoke(form, "ApplyTaskbarVisibility", true);
        Check("Restoring widget respects taskbar off", !form.ShowInTaskbar);
        preferences.ShowInTaskbar = true;
        Invoke(form, "ApplyTaskbarVisibility", false);
        Check("Enabling taskbar while hidden keeps its button hidden", !form.ShowInTaskbar);
        Invoke(form, "ApplyTaskbarVisibility", true);
        Check("Restoring widget respects taskbar on", form.ShowInTaskbar);

        using (var taskbarOffForm = new WidgetForm(new AppSettings
        {
            X = 100, Y = 100, ShowInTaskbar = false, AlwaysOnTop = false, NotifyOnUsageLimitReached = false
        }))
        {
            ((System.Windows.Forms.Timer)Get(taskbarOffForm, "refreshTimer")!).Stop();
            ((System.Windows.Forms.Timer)Get(taskbarOffForm, "positionSaveTimer")!).Stop();
            ((System.Windows.Forms.Timer)Get(taskbarOffForm, "topmostTimer")!).Stop();
            ((NotifyIcon)Get(taskbarOffForm, "trayIcon")!).Visible = false;
            var taskbarOption = taskbarOffForm.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>()
                .Single(item => item.Name == "showInTaskbar");
            Check("Saved off preference applies at startup and in menu", !taskbarOffForm.ShowInTaskbar &&
                taskbarOption.CheckOnClick && !taskbarOption.Checked && taskbarOption.Text == "Show in taskbar — Off");
        }

        ((System.Windows.Forms.Timer)Get(form, "positionSaveTimer")!).Stop();
        Click(form, 242, 17);
        Check("Minimise control does not flip face", !(bool)Get(form, "showResetDetails")! && !form.ShowInTaskbar);
        Click(form, 270, 17);
        Check("Close control keeps app in tray", !(bool)Get(form, "showResetDetails")! && !form.IsDisposed);

        var hideEvents = ReadVisibilityLog(diagnosticPath).Where(entry => entry.GetProperty("eventName").GetString() == "hide-request").ToArray();
        Check("Real button paths identify minimize and close separately", hideEvents.Any(entry => entry.GetProperty("details").GetString()!.StartsWith("minimize-button;")) &&
            hideEvents.Any(entry => entry.GetProperty("details").GetString()!.StartsWith("close-button;")));
        var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
        typeof(WidgetForm).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(object), typeof(FormClosingEventArgs) }, null)!.Invoke(form, new object[] { form, closing });
        Check("Close requests record their reason without changing tray behavior", closing.Cancel && !form.IsDisposed &&
            ReadVisibilityLog(diagnosticPath).Any(entry => entry.GetProperty("eventName").GetString() == "form-closing" &&
                entry.GetProperty("details").GetString()!.Contains("reason=UserClosing")));
        // Decode messages without passing synthetic messages to Windows or showing the form.
        Invoke(form, "RecordVisibilityMessage", 0x0018, IntPtr.Zero, new IntPtr(1));
        Invoke(form, "RecordVisibilityMessage", 0x0018, new IntPtr(1), new IntPtr(3));
        var messageEvents = ReadVisibilityLog(diagnosticPath).Where(entry => entry.GetProperty("eventName").GetString() == "WM_SHOWWINDOW").ToArray();
        Check("Native owner hide and restore reasons are preserved", messageEvents.Any(entry => entry.GetProperty("details").GetString()!.Contains("show=False;reason=owner-minimized")) &&
            messageEvents.Any(entry => entry.GetProperty("details").GetString()!.Contains("show=True;reason=owner-restored")));
        var positionType = typeof(WidgetForm).GetNestedType("NativeWindowPosition", BindingFlags.NonPublic)!;
        var position = Activator.CreateInstance(positionType)!;
        positionType.GetField("Flags")!.SetValue(position, (uint)0x0080);
        var positionPointer = Marshal.AllocHGlobal(Marshal.SizeOf(positionType));
        try
        {
            Marshal.StructureToPtr(position, positionPointer, false);
            Invoke(form, "RecordVisibilityMessage", 0x0047, IntPtr.Zero, positionPointer);
        }
        finally { Marshal.FreeHGlobal(positionPointer); }
        Check("Native hide positioning flags are captured", ReadVisibilityLog(diagnosticPath).Any(entry =>
            entry.GetProperty("eventName").GetString() == "WM_WINDOWPOSCHANGED" && entry.GetProperty("details").GetString()!.Contains("hide=True")));
        Invoke(form, "ObserveVisibility", "test-poll");
        var recordCount = File.ReadAllLines(diagnosticPath).Length;
        Invoke(form, "ObserveVisibility", "test-poll");
        Check("Stable polling does not fill the log", File.ReadAllLines(diagnosticPath).Length == recordCount);
        var sessionEntry = ReadVisibilityLog(diagnosticPath).First(entry => entry.GetProperty("eventName").GetString() == "session-start");
        Check("Diagnostic sessions identify build and widget state", sessionEntry.GetProperty("details").GetString()!.Contains("build=") &&
            sessionEntry.GetProperty("details").GetString()!.Contains("managedVisible=") &&
            sessionEntry.GetProperty("session").GetString()!.Length == 32);

        ResetAnnouncementChecks.Run(Check);
        ReleaseUpdateChecks.Run(Check);
        CheckReleaseUpdateMenu();
        CheckAnnouncementLayouts(output);
        if (args.Contains("--live-reset-check"))
        {
            var live = Task.Run(async () =>
            {
                using var client = new ResetAnnouncementClient();
                return await client.ReadAsync();
            }).GetAwaiter().GetResult();
            Check("Live public tracker endpoint parses through the actual client", live is not null);
            Console.WriteLine("Live tracker: " + live!.Label(DateTimeOffset.Now));
        }

        if (args.Contains("--live-release-check"))
        {
            var updates = Task.Run(async () =>
            {
                using var client = new ReleaseUpdateClient();
                var installed = await client.ReadAsync(EditionVersion.Current);
                var older = await client.ReadAsync(EditionVersion.Parse("1.2.0-beta.0")!);
                return (installed, older);
            }).GetAwaiter().GetResult();
            Check("Live GitHub releases do not offer an update to this beta", updates.installed is null);
            Check("Live GitHub release is discoverable by an earlier beta", updates.older?.Version == EditionVersion.Current);
        }
        Console.WriteLine($"PASS: {passed} checks. Rendered fixtures: {output ?? "not requested"}");
    }

    private static void CheckReleaseUpdateMenu()
    {
        var unavailable = false;
        var newer = true;
        using var handler = new ResetAnnouncementChecks.Handler((_, _) => Task.FromResult(unavailable
            ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)
            : ResetAnnouncementChecks.JsonResponse(newer
                ? ReleaseUpdateChecks.Releases(ReleaseUpdateChecks.Release("v1.2.0-beta.2")) : "[]")));
        using var widget = new WidgetForm(new AppSettings { AlwaysOnTop = false },
            releaseUpdateClient: new ReleaseUpdateClient(handler));
        foreach (var name in new[] { "refreshTimer", "positionSaveTimer", "hoverTimer", "topmostTimer" })
            ((System.Windows.Forms.Timer)Get(widget, name)!).Stop();
        ((NotifyIcon)Get(widget, "trayIcon")!).Visible = false;
        var timer = (System.Windows.Forms.Timer)Get(widget, "updateTimer")!;
        Check("Update checks are daily and isolated forms never check automatically", timer.Interval == 86_400_000 && !timer.Enabled && handler.Calls == 0);
        var check = (ToolStripMenuItem)widget.ContextMenuStrip!.Items["checkUpdates"]!;
        var download = (ToolStripMenuItem)widget.ContextMenuStrip.Items["availableUpdate"]!;
        var originalSize = widget.Size;
        ((Task)Invoke(widget, "RefreshUpdatesAsync", false)!).GetAwaiter().GetResult();
        Check("Actual form exposes a new beta download without resizing", download.Available && download.Text!.Contains("1.2.0-beta.2") &&
            Get(widget, "availableUpdate") is ReleaseUpdate && widget.Size == originalSize);
        ((Task)Invoke(widget, "RefreshUpdatesAsync", true)!).GetAwaiter().GetResult();
        Check("Repeated manual clicks are throttled", handler.Calls == 1);
        unavailable = true;
        ((Task)Invoke(widget, "RefreshUpdatesAsync", false)!).GetAwaiter().GetResult();
        Check("Failed form update checks retain the known download and expose failure", download.Available && check.Text!.Contains("unavailable") && check.Enabled);
        unavailable = false;
        newer = false;
        ((Task)Invoke(widget, "RefreshUpdatesAsync", false)!).GetAwaiter().GetResult();
        Check("Form update status recovers and removes obsolete notice", !download.Available && check.Text!.Contains("up to date") && Get(widget, "availableUpdate") is null);
    }

    private static void CheckAnnouncementLayouts(string? output)
    {
        var unavailable = false;
        using var handler = new ResetAnnouncementChecks.Handler((_, _) => Task.FromResult(unavailable
            ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new System.Net.Http.StringContent("tracker unavailable") }
            : ResetAnnouncementChecks.JsonResponse(ResetAnnouncementChecks.Status(
                ResetAnnouncementChecks.Scheduled(DateTimeOffset.UtcNow.AddHours(8))))));
        var preferences = new AppSettings { X = 100, Y = 100, AlwaysOnTop = false, ExpandOnHover = true,
            NotifyOnUsageLimitReached = false };
        using var widget = new WidgetForm(preferences, resetAnnouncementsClient: new ResetAnnouncementClient(handler));
        foreach (var name in new[] { "refreshTimer", "positionSaveTimer", "hoverTimer", "topmostTimer" })
            ((System.Windows.Forms.Timer)Get(widget, name)!).Stop();
        ((NotifyIcon)Get(widget, "trayIcon")!).Visible = false;
        widget.CreateControl();
        var scale = widget.DeviceDpi / 96f;
        Check("Isolated forms do not automatically contact the reset tracker", handler.Calls == 0 &&
            !((System.Windows.Forms.Timer)Get(widget, "announcementTimer")!).Enabled);
        SetSnapshot(widget, 24, 5, 2);
        var usage = Get(widget, "snapshot");
        ((Task)Invoke(widget, "RefreshAnnouncementsAsync")!).GetAwaiter().GetResult();
        Check("Actual form refresh reads announcements separately from account usage", handler.Calls == 1 &&
            ReferenceEquals(usage, Get(widget, "snapshot")) && !(bool)Get(widget, "announcementFailed")!);
        Check("Announced text uses the requested yellow", (bool)Invoke(widget, "AnnouncementIsActive", DateTimeOffset.Now)! &&
            ((SolidBrush)Get(widget, "announcementBrush")!).Color == Color.FromArgb(255, 220, 70));
        Save(widget, output, "announcement-bottom-usage");
        Click(widget, 40, 112);
        Check("Bottom announcement click does not switch pages", !(bool)Get(widget, "showResetDetails")!);
        MovePointer(widget, 40, 112);
        Check("Announcement strip does not trigger hover expansion", !((System.Windows.Forms.Timer)Get(widget, "hoverTimer")!).Enabled);
        Check("Visible source credit has its own link hit area", (bool)Invoke(widget, "IsAnnouncementSource",
            new Point((int)(240 * scale), (int)(112 * scale)))!);
        Invoke(widget, "ToggleFace");
        Save(widget, output, "announcement-bottom-details");
        Check("Page two has its announcement without losing expiry rows", widget.Height == (int)Math.Round(222 * scale));
        Invoke(widget, "ToggleFace");
        var layout = widget.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "resetAnnouncements");
        var crown = layout.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Name == "Crown");
        var bottom = layout.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Name == "Bottom");
        var previousBodyTop = widget.Top;
        crown.PerformClick();
        Check("Menu chooses and saves Crown with one checked layout", preferences.ResetAnnouncementPlacement == AnnouncementPlacement.Crown && crown.Checked && !bottom.Checked &&
            JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(preferences))!.ResetAnnouncementPlacement == AnnouncementPlacement.Crown);
        Check("Changing layout keeps the title strip in the same screen position", widget.Top + (int)Math.Round(24 * scale) == previousBodyTop);
        Check("Crown is a shaped window with empty corners", widget.Region is not null &&
            !widget.Region.IsVisible(0, 0) && widget.Region.IsVisible(145 * scale, 12 * scale) &&
            widget.Region.IsVisible(5 * scale, 40 * scale));
        Check("Native window applies the crown region", NativeCrown(widget, scale));
        widget.ShowInTaskbar = true;
        widget.ShowInTaskbar = false;
        Check("Taskbar handle recreation preserves the native crown", NativeCrown(widget, scale));
        Check("Crown keeps the same overall dimensions", widget.ClientSize == new Size((int)Math.Round(290 * scale), (int)Math.Round(124 * scale)));
        Save(widget, output, "announcement-crown-usage");
        Click(widget, 80, 12);
        Check("Crown click does not flip the usage face", !(bool)Get(widget, "showResetDetails")!);
        MovePointer(widget, 80, 12);
        Check("Crown is a drag area without hover expansion", widget.Cursor == Cursors.SizeAll &&
            !((System.Windows.Forms.Timer)Get(widget, "hoverTimer")!).Enabled);
        Invoke(widget, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, (int)(242 * scale), (int)(12 * scale), 0));
        Check("Crown cannot accidentally trigger title controls", !(bool)Get(widget, "controlPress")!);
        widget.Capture = false;
        Invoke(widget, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, (int)(242 * scale), (int)(41 * scale), 0));
        Check("Title controls move with the crown layout", (bool)Get(widget, "controlPress")!);
        widget.Capture = false;
        var beforeDrag = widget.Location;
        Invoke(widget, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, (int)(80 * scale), (int)(12 * scale), 0));
        Invoke(widget, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 1, (int)(60 * scale), (int)(12 * scale), 0));
        Invoke(widget, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, (int)(60 * scale), (int)(12 * scale), 0));
        Check("Crown drag moves the widget without hiding or switching", widget.Left == beforeDrag.X - (int)(20 * scale) &&
            !(bool)Get(widget, "showResetDetails")! && !widget.IsDisposed);
        Click(widget, 40, 94);
        Check("Crown layout body click still switches pages", (bool)Get(widget, "showResetDetails")!);
        Save(widget, output, "announcement-crown-details");
        Invoke(widget, "ToggleFace");
        EnterBody(widget);
        HoverTick(widget, true);
        Check("Crown survives hover expansion", (bool)Get(widget, "hoverExpanded")! && widget.Height == (int)Math.Round(260 * scale) && widget.Region is not null);
        MovePointer(widget, 80, 12);
        Invoke(widget, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, (int)(80 * scale), (int)(12 * scale), 0));
        Invoke(widget, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 1, (int)(60 * scale), (int)(12 * scale), 0));
        Invoke(widget, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, (int)(60 * scale), (int)(12 * scale), 0));
        Check("Dragging crown collapses hover without flipping", !(bool)Get(widget, "hoverExpanded")! &&
            !(bool)Get(widget, "showResetDetails")! && widget.Height == (int)Math.Round(124 * scale));
        var area = Screen.FromControl(widget).WorkingArea;
        widget.Location = new Point(area.Right - widget.Width, area.Bottom - widget.Height);
        var edge = widget.Location;
        for (var i = 0; i < 3; i++) { Invoke(widget, "ToggleFace"); Invoke(widget, "ToggleFace"); }
        Check("Crown page switches at screen edge retain their anchor", widget.Location == edge && area.Contains(widget.Bounds));
        bottom.PerformClick();
        crown.PerformClick();
        bottom.PerformClick();
        Check("Changing crown back to bottom restores a rectangular window", widget.Region is null && bottom.Checked && !crown.Checked && area.Contains(widget.Bounds));
        Set(widget, "announcements", new ResetAnnouncements(null, new ResetWatch(65, "next 24 hours", DateTimeOffset.UtcNow.AddHours(8))));
        Check("Possible resets use yellow too", (bool)Invoke(widget, "AnnouncementIsActive", DateTimeOffset.Now)! &&
            ((string)Invoke(widget, "AnnouncementText", DateTimeOffset.Now)!).StartsWith("Possible reset"));
        Save(widget, output, "announcement-possible");
        crown.PerformClick();
        Save(widget, output, "announcement-crown-possible");
        bottom.PerformClick();
        SetSnapshot(widget, 50, 5, 2);
        Save(widget, output, "announcement-warning");
        Check("Usage warning preserves yellow announcement text", widget.BackColor == Color.FromArgb(132, 38, 48) &&
            (bool)Invoke(widget, "AnnouncementIsActive", DateTimeOffset.Now)!);
        Set(widget, "announcementCheckedAt", DateTimeOffset.Now.AddMinutes(-11));
        Check("Old tracker data is explicitly stale", (string)Invoke(widget, "AnnouncementText", DateTimeOffset.Now)! == "Stale · reset info" &&
            !(bool)Invoke(widget, "AnnouncementIsActive", DateTimeOffset.Now)!);
        unavailable = true;
        ((Task)Invoke(widget, "RefreshAnnouncementsAsync")!).GetAwaiter().GetResult();
        Check("Tracker failure preserves account data and exposes stale status", (bool)Get(widget, "announcementFailed")! &&
            (bool)Get(widget, "liveConnected")! && (string)Invoke(widget, "AnnouncementText", DateTimeOffset.Now)! == "Stale · reset info");
        Save(widget, output, "announcement-stale");
        unavailable = false;
        ((Task)Invoke(widget, "RefreshAnnouncementsAsync")!).GetAwaiter().GetResult();
        Check("Announcement display recovers after a successful read", !(bool)Get(widget, "announcementFailed")! &&
            (bool)Invoke(widget, "AnnouncementIsActive", DateTimeOffset.Now)!);
        Check("Missing placement preference defaults to Bottom", JsonSerializer.Deserialize<AppSettings>("{}")!.ResetAnnouncementPlacement == AnnouncementPlacement.Bottom);
        ((System.Windows.Forms.Timer)Get(widget, "positionSaveTimer")!).Stop();
    }

    private static bool NativeCrown(WidgetForm widget, float scale)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        try { return GetWindowRgn(widget.Handle, region) == 3 && !PtInRegion(region, 0, 0) &&
            PtInRegion(region, (int)(145 * scale), (int)(12 * scale)); }
        finally { DeleteObject(region); }
    }

    private static void SetSnapshot(WidgetForm form, double used, double daysLeft, int resets)
    {
        Set(form, "snapshot", new UsageSnapshot(used, Now.AddDays(daysLeft), Now,
            Enumerable.Range(1, resets).Select(i => Now.AddDays(14 + i * 7)).ToArray()));
        Set(form, "liveConnected", true);
        Invoke(form, "UpdatePace");
        Invoke(form, "UpdateFaceSize");
    }

    private static void Click(WidgetForm form, int x, int y)
    {
        var scale = form.DeviceDpi / 96f;
        var args = new MouseEventArgs(MouseButtons.Left, 1, (int)(x * scale), (int)(y * scale), 0);
        Invoke(form, "OnMouseDown", args);
        Invoke(form, "OnMouseUp", args);
    }

    private static void HoverTick(WidgetForm form, bool pointerInside, bool menuVisible = false)
    {
        ((System.Windows.Forms.Timer)Get(form, "hoverTimer")!).Stop();
        Invoke(form, "ProcessHoverTick", pointerInside, menuVisible);
    }

    private static void EnterBody(WidgetForm form)
    {
        Invoke(form, "OnMouseLeave", EventArgs.Empty);
        MovePointer(form, 40, 70);
    }

    private static void MovePointer(WidgetForm form, int x, int y)
    {
        var scale = form.DeviceDpi / 96f;
        Invoke(form, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, (int)(x * scale), (int)(y * scale), 0));
    }

    private static void Save(WidgetForm form, string? path, string name)
    {
        if (path is null) return;
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(path, name + ".png"), ImageFormat.Png);
    }

    private static bool Near(double? value, double expected) => value.HasValue && Math.Abs(value.Value - expected) < 0.00001;
    private static JsonElement[] ReadVisibilityLog(string path) => File.ReadLines(path).Select(line =>
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }).ToArray();
    private static bool Occludes(Rectangle widget, Rectangle other, bool visible, bool topmost, bool cloaked) =>
        (bool)typeof(WidgetForm).GetMethod("IsOrdinaryOccluder", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { widget, other, visible, topmost, cloaked })!;
    private static void Check(string label, bool success)
    {
        if (!success) throw new InvalidOperationException("FAIL: " + label);
        passed++;
        Console.WriteLine("PASS: " + label);
    }
    private static object? Get(object instance, string name) => typeof(WidgetForm)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void Set(object instance, string name, object value) => typeof(WidgetForm)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static object? Invoke(object instance, string name, params object[] args) => typeof(WidgetForm)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
}
