# Changelog

All notable changes to CodexBar are documented here.

## [1.2.0-beta.5] - 2026-10-07

- Automatically delegates expiring or rejected Claude credentials to native Claude Code for renewal, then rereads the credential before fetching stats. Supports standalone and Desktop-bundled installations, bounded background processes and retry recovery without storing widget copies of tokens.

## [1.2.0-beta.4] - 2026-10-06

- Adds a third Claude face with five-hour and weekly allowance remaining, local reset times, countdowns, last update time and offline/stale/expired states.
- Reads Claude account usage every five minutes using the existing Claude Code login, with bounded requests, rate-limit backoff and a Refresh Claude menu action. Credentials remain managed by Claude; no status-line setup is required.
- Handles Claude returning usage without a reset time after a window ends, retaining the available percentages and showing the missing reset time explicitly.
- Cycles through Codex usage, Codex reset details and Claude. Keeps Codex announcements on the Codex faces and the tray number tied to Codex; collapsing Claude uses its weekly allowance.

## [1.2.0-beta.3] - 2026-10-03

- Keeps the collapsed strip at the visible progress bar's screen position with matching fill width and side spacing. Restores the original widget position and preserves relative movement when dragging the strip.
- Fixes jagged widget text after numeric tray-icon initialization by explicitly using ClearType for widget text.
- Adds a title-dot/menu collapse mode: a live 290 × 8 progress bar that supports dragging and click/keyboard restoration of the previous page, without hover expansion. Keeps the existing hide-to-tray buttons.
- Allows up to 80% off-screen dragging at every edge, including into the taskbar area. Preserves deliberate placement across refreshes/restarts and adds Bring fully on screen recovery; the tiny bar remains vertically reachable above the taskbar.
- Replaces the notification icon with high-contrast weekly-remaining digits. Updates while hidden/collapsed and shows a dash for unavailable/offline readings; branding and launch shortcuts stay unchanged.
- Renames the overview labels to Daily usage at current rate and Est. Remaining at Reset, each on two lines. Adds 14 pixels of height for label spacing and lowers the progress bar; both faces keep their 290-pixel width and reset details are unchanged.
- Shows daily averages and reset forecasts immediately once any cycle time has elapsed, removing the six-hour wait. The exact cycle start remains undefined rather than dividing by zero.
- Fits large percentages within their existing columns by reducing the font size; keeps all digits, signed forecasts and existing warning colours.

## [1.2.0-beta.2] - 2026-10-02

- Keeps both faces and reset-announcement backgrounds dark, highlighting only the Remaining at Reset column when it displays zero or below.
- Uses black text on the red warning area, including the forecast row in expanded hover content.
- Shows negative forecasts such as -5% instead of clamping them to zero. Rounded negative-zero values display as 0%.
- Starts the highlight at the displayed 0% instead of waiting for the old -0.5% threshold; clears it at displayed 1% or higher. Offline and unknown forecasts do not highlight. Forecast calculations and actual weekly capacity are unchanged.
- Updates documentation and adds an illustrative warning screenshot. Preserves the widget dimensions, normal percentage/progress colours, green reset countdowns, yellow announcements, saved settings and startup behavior.

## [1.2.0-beta.1] - 2026-10-02

First public beta of **CodexBar — MajorCommand Edition**, an independent GPL-3.0-only fork of jspann21's CodexBar. Preserves the original project credit, settings and startup identity.

- Adds a public GitHub release check on launch and daily, with manual checks, a tray notice and an Update available download-page menu item. Beta builds receive beta/stable updates; stable builds exclude betas. Requests are bounded, cancellable and respect GitHub retry delays. Failures are visible and installation remains manual.
- Provides a Windows x64 lightweight EXE and portable ZIP, matching tagged source, licence, release notes and SHA-256 checksums.

- Adds Codex Resets announcement data on both faces with a saved Crown or Bottom menu choice. Both layouts add 24 pixels while retaining the 290-pixel width; the shaped crown and title strip support dragging without hover expansion. Announced and possible reset text is yellow, with an underlined Source link and tracker credit/details in the tooltip and menu.
- Clarifies the countdown as "Reset Announced in ~8h", lowers crown text by three logical pixels and gives compact percentage labels two more pixels of space above the progress bar.
- Reads the independent tracker's public status API every five minutes using conditional requests, bounded requests and server retry delays. Distinguishes scheduled announcements, speculative/expired forecasts, no announcement and stale/offline data; passed scheduled times await confirmation. Account usage, forecasts and reset-credit values remain independent.
- Adds rotating local visibility diagnostics for unexpected disappearances, including explicit hide/show requests, Windows messages and widget/owner state changes. Records locally without account data or automatic uploads and reports log write failures.
- Recovers the visible widget's always-on-top position if its native flag is lost or ordinary windows cover it despite that flag. Checks locally every two seconds without activation, while respecting hiding, Off, dragging, menus, dialogs and other topmost windows.
- Adds clickable usage and reset-details faces at a shared 290-pixel width. The 290 × 100 usage face shows weekly remaining, daily average and estimated remaining at reset, with centered labels, a reset countdown in the title and a capacity bar below.
- Calculates average daily usage over the inferred seven-day cycle and estimates capacity remaining at reset or time until exhaustion. Withholds forecasts during the first six hours and marks offline data without a live pace warning.
- Warns with a muted red background and white text on both faces when the weekly pace predicts exhaustion before reset, with a small buffer to prevent flicker near the limit.
- Shows dates on the left and green countdowns on the right of reset details. Fits longer date text within its column, shows whole days for banked expiries and hours below one day, and marks expired/offline states explicitly. Long lists grow vertically and scroll when taller than the screen.
- Restores the compact location after page switches and hover expansion at screen edges, preserving deliberate drag movement without accumulating resize drift.
- Adds optional hover expansion at the same width, with delayed entry and collapse, screen-edge positioning, and a saved right-click preference that starts off.
- Reserves the title strip for dragging without hover expansion or face switching. Starting a title drag collapses hover content under the pointer, and release suppresses expansion until body reentry.
- Adds a saved Show in taskbar toggle, keeping the widget visible and the tray icon available when Off; defaults to Off and respects the preference on restore.
- Defaults to hover Off, always on top On and taskbar Off. Enables Windows startup once at the first launch of this build, preserves later Off choices and reports startup failures.
- Preserves dragging without flipping faces; adds keyboard and tray-menu face switching.
- Adds isolated calculation and actual-form checks with rendered preview fixtures, plus screenshots of both finished faces in the README.

## [1.1.1] - 2026-07-31

- Makes the Start with Windows status explicit in the tray menu.
- Adds notification setup guidance for Gmail app passwords and carrier email-to-text addresses.
- Clarifies that the configured refresh interval controls the real rate-limit request frequency.

## [1.1.0] - 2026-07-31

### Added

- Exact local expiration dates and times for every available banked rate-limit reset, ordered nearest first.
- Explicit `Always on top — On/Off` status in the tray menu.
- Optional weekly-capacity reset notifications through SMTP or a prefilled draft in the default mail app.
- A test-alert command and a live/offline connection indicator.

### Improved

- Reuses the supported Codex app-server response for reset-credit details without reading authentication files or requiring a separate login.
- Protects stored SMTP passwords with Windows Data Protection API.
- Validates notification settings, bounds delivery time, and writes settings atomically.
- Falls back across installed Codex executables and distinguishes retryable transport failures from semantic errors.
- Reduces unnecessary settings writes and widget repaints.

## [1.0.0] - 2026-07-22

- Initial public release of the lightweight Windows widget.
- Live weekly Codex capacity and reset-time display.
- Configurable refresh interval, transparency, always-on-top mode, Windows startup, and notification-area support.

[Unreleased]: https://github.com/majorcommand/codex-bar/compare/v1.2.0-beta.2...HEAD
[1.2.0-beta.2]: https://github.com/majorcommand/codex-bar/compare/v1.2.0-beta.1...v1.2.0-beta.2
[1.2.0-beta.1]: https://github.com/majorcommand/codex-bar/compare/v1.1.1...v1.2.0-beta.1
[1.1.1]: https://github.com/jspann21/codex-bar/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/jspann21/codex-bar/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/jspann21/codex-bar/releases/tag/v1.0.0
