# Contributing to Counter

Issues and pull requests are welcome. This is a small project with a strong opinion about a few
things, so a paragraph on what those are will save you time.

## Getting set up

```powershell
git clone https://github.com/makswinz/Counter.git
cd Counter
.\run.ps1            # build Debug and run it
.\run.ps1 -Demo      # with a few example tasks, only into an empty database
dotnet test Counter.sln
```

You need the [.NET 8 SDK](https://dot.net). Nothing else. If `dotnet` is not on your PATH the
scripts fall back to a per-user install at `%LOCALAPPDATA%\Microsoft\dotnet`.

Your own data lives in `%LOCALAPPDATA%\Counter` and the tests never touch it: every test that
needs a database makes a temporary one and deletes it.

## What a pull request needs

- **`dotnet test Counter.sln` passes.** Warnings are errors, so a warning is a failure too.
- **New behaviour comes with a test.** The suite has no sleeps and needs no display, so there is
  no reason a change cannot be covered. If you think yours cannot be, say so in the pull request
  and we will work it out.
- **Match the surrounding code.** Comments explain *why*, not *what*. If a line needs a comment
  saying what it does, the line is usually the problem.

## Things that are decided

Not to shut down discussion, but so you know what you are arguing against:

- **No telemetry, no accounts, no network calls.** Counter does not talk to anything. A feature
  that needs a server is a different application.
- **No third-party UI framework.** Every control is drawn in this repository. It is why the
  interface is consistent and why it starts instantly.
- **Colour is generated, never listed.** Any accent produces its whole palette through
  `AccentEngine` in OKLCH. A pull request that hardcodes a gradient stop will be asked to derive
  it instead. See [docs/DESIGN.md](docs/DESIGN.md#accent).
- **Colour never carries meaning alone.** A state is an icon and a word as well as a hue.
- **One physical pixel means one physical pixel.** Hairlines resolve `1 / DpiScale` dynamically.

## If you change the icon

The mark is `Assets/logo.svg`. Export it to `Assets/logo.png` at 1024, then:

```powershell
.\tools\New-AppIcon.ps1
```

A test compares the committed `.ico` against what the code produces and fails if they drift.

## Reporting a bug

The most useful bug report contains the log. It is at:

```
%LOCALAPPDATA%\Counter\logs\counter-<date>.log
```

It contains no personal data beyond file paths, but read it before you paste it.
