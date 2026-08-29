# Third-party notices

Counter includes third-party material. This file lists it, what it is used for, and the
licence it is used under. The full licence text of each item is reproduced or linked below.

---

## Microsoft Fluent UI System Icons

- **Project**: https://github.com/microsoft/fluentui-system-icons
- **Copyright**: Copyright (c) 2020 Microsoft Corporation
- **Licence**: MIT
- **Revision used**: `1.1.339` (commit `4d685f77b2cb8f3f412a74ec8d920c8c91149528`)
- **Licence text**: [`Assets/Icons/Fluent/LICENSE.txt`](Assets/Icons/Fluent/LICENSE.txt)

Every icon drawn anywhere in Counter comes from this one family. No other icon set, no
symbol font, no emoji and no hand-drawn approximation is used.

The 47 SVG files actually used are bundled under [`Assets/Icons/Fluent/`](Assets/Icons/Fluent/)
exactly as they were published, together with
[`manifest.json`](Assets/Icons/Fluent/manifest.json) recording the source, the revision, the
commit and a SHA-256 for each file. `tools/Sync-FluentIcons.ps1` converts them into frozen WPF
geometries in `src/Counter.App/Theme/Icons.xaml` and a lookup table in
`src/Counter.App/Controls/IconCatalog.g.cs`.

The conversion is a build-time step that has already been run and whose output is committed.
The application never downloads an icon, never resolves one over the network, and never depends
on a font being installed on the machine it runs on. `tools/Sync-FluentIcons.ps1 -Verify` checks
the bundled files against the manifest without touching the network at all.

### MIT License

```
MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## NuGet packages

Restored at build time and redistributed inside the published single-file executable.

| Package | Licence | Project |
| --- | --- | --- |
| `CommunityToolkit.Mvvm` 8.3.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| `Microsoft.Data.Sqlite` 8.0.10 | MIT | https://github.com/dotnet/efcore |
| `SQLitePCLRaw` (transitive) | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| `SQLite` (bundled native library) | Public domain | https://www.sqlite.org/copyright.html |
| .NET 8 runtime and WPF | MIT | https://github.com/dotnet/runtime |

---

## The OKLab colour space

`src/Counter.Core/Colour/Oklch.cs` implements the OKLab transform published by Bjorn Ottosson
in 2020, at https://bottosson.github.io/posts/oklab/. The matrices and the cube-root transfer are
his and are published as public domain; the gamut mapping, the ramp derivation and everything
else in that folder are this project's own.

No code is copied and nothing is redistributed. The reference is recorded because the numbers in
that file are not arbitrary and somebody reading it should know where to check them.

---

## Fonts

Counter uses only fonts already present on Windows (Segoe UI Variable, Segoe UI and the
generic fallbacks behind them). No font is bundled, downloaded or installed, and no icon is
drawn from a font glyph.
