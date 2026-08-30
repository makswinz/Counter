# Changelog

All notable changes to Counter. The format follows [Keep a Changelog](https://keepachangelog.com),
and versions follow [semantic versioning](https://semver.org).

## [1.2.0] - 2026-08-30

### Fixed

- **The blur had square corners.** Every glass panel was drawing a rectangle of blurred desktop
  that reached past its own rounded outline, most visibly as a grey nub sitting in clear air at
  the bottom corners of the notch. The backdrop window was being clipped with `SetWindowRgn`,
  which does not clip an acrylic blur at all - an assumption that survived because it can only be
  tested on a machine where Windows transparency effects are switched on, and with them off the
  compositor returns a flat colour that has no corners to get wrong. Windows 11 now uses DWM's own
  corner rounding, with the backdrop drawn far enough inside the panel for the two curves to nest;
  Windows 10 keeps the region, where it works.
- **A dark halo around every translucent panel.** DWM shadows a window it rounds, so each panel
  had two shadows: the compositor's and its own. Panels now give up their own while a backdrop is
  under them.

### Changed

- **Every ink now clears 4.5:1 on every material, in both themes, over any wallpaper** - and the
  muted tone clears 3:1, the threshold for text that labels rather than states. It did not before:
  with a real blur behind it, dark frosted glass put primary text at 4.2:1 and muted text at
  1.6:1, because a blur destroys detail without changing luminance and a white wallpaper blurred
  is still white. The quiet end of the ink ladder was compressed and the two translucent materials
  were thickened, in that order, because a lost step of hierarchy costs less than a lost material.
  The full measurement, and the photographs it comes from, are in `docs/DESIGN.md`.

## [1.1.0] - 2026-08-29

### Fixed

- **Counter was stealing Ctrl+Shift+N from every application on the machine**, and three more
  besides. All four global shortcuts defaulted into the Ctrl+Shift space, which is where
  applications keep their own: Ctrl+Shift+N opens a private browser window, Ctrl+Shift+S is Save
  As, Ctrl+Shift+F is find-in-files. A global hotkey outranks all of them, and RegisterHotKey
  neither fails nor warns when it takes one. The defaults moved to Ctrl+Alt, and every shortcut
  can now be switched off individually in Settings, because any default will collide with
  something somebody uses.
- Backup rotation never deleted anything, so the folder grew without limit. SQLite pools
  connections by default and each backup file stayed open after being written.
- Deleted tasks appeared in the statistics.
- A stored setting of "2" was read as an enum's second member rather than rejected.

### Added

- **Hide.** From the tray, from Settings, or with Ctrl+Alt+C. The timer keeps running and the
  tray icon brings it back. Remembered across restarts.
- **Move.** Left, Centre or Right. The notch sits where a browser keeps its tabs, and stepping
  aside is often better than putting it away.
- **Remove time from a task**, as well as add it. The same dialog with the sign reversed, capped
  so a total can never go below nothing. A timer left running over lunch is the commonest way a
  total goes wrong.
- Six statistics describing the shape of a range rather than its size: time per task, time per
  day actually worked, the best day, the busiest weekday, the day the most was finished, and the
  longest unbroken run.
- A refraction band at the edge of frosted and liquid glass, where a thick edge bends what passes
  through it. It is the cue the eye reads as thickness, and no amount of transparency fakes it.
- A button in Settings that opens the Windows transparency setting, when that setting is what is
  standing between the glass and a real blur.

### Changed

- **The grain is gone.** Two percent of monochrome noise was meant to stop the surface reading as
  a flat digital fill. Real glass has no such thing.
- Frosted and liquid glass are considerably denser when there is no compositor blur behind them.
  Without a blur, translucent and legible are in direct tension, and a panel you can read your
  browser through is not frosted glass.

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
