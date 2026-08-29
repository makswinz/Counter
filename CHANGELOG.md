# Changelog

All notable changes to Counter. The format follows [Keep a Changelog](https://keepachangelog.com),
and versions follow [semantic versioning](https://semver.org).

## [1.0.0] - 2026-08-29

First public release.

### The notch

- A bar at the top edge of the screen showing the countdown and the active task, unfolding into a
  quick panel, then a full planner with a calendar and duration picker, then statistics.
- Hover to open, click to pin, Escape to close. The window never changes width, so opening a panel
  cannot make anything jump sideways.
- Tray icon and global shortcuts for starting, pausing and stopping without touching the panel.

### Time

- Every stretch of a running session is stored as a pair of instants, so all totals are added up
  from what actually happened rather than from what was planned.
- Sessions that cross midnight are split at local midnight and land on both days.
- Time can be added by hand and is kept apart from measured time so it can never be counted twice.

### Statistics

- Focus total, tasks done, sessions, average session, current and longest streak, completion rate.
- Time per task, time per day actually worked, the best day, the busiest weekday, the day the most
  was finished, and the longest unbroken run.
- A twelve-week journey heatmap and a daily activity chart.

### Appearance

- Light and dark themes, following Windows by default.
- Six accent families and a colour picker for any colour at all. The whole palette - five lit
  gradient stops, contour, halo, tint, and a foreground measured for contrast - is derived in
  OKLCH from that one colour.
- Three glass materials: Solid, Frosted and Liquid. The two translucent ones are backed by a real
  compositor blur in a companion window, because a layered window cannot blur its own backdrop.

### Data

- Everything is local, in SQLite, at `%LOCALAPPDATA%\Counter`. No account, no cloud, no telemetry.
- A rotating local backup, kept to the newest seven, plus manual backup, restore and CSV export.
