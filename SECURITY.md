# Security

Counter runs entirely on your machine. It makes no network requests, has no account system, and
sends nothing anywhere. The attack surface is small by construction, but it is not zero: it reads
and writes a SQLite database, writes a registry value under `HKCU` when you ask it to start with
Windows, and calls a handful of Win32 functions to place its window.

## Reporting something

Please open a [security advisory](https://github.com/makswinz/Counter/security/advisories/new)
rather than a public issue. I will confirm receipt within a few days.

If you would rather not use GitHub, open a normal issue saying only that you have found something
and would like a private channel, and we will arrange one.

## Supported versions

The latest release is the supported version. This is a small project with one maintainer, so
patches go on top of `main` and ship as a new release rather than being backported.

## What is signed, and what is not

**Nothing is signed.** The installer and the portable executable carry no code-signing
certificate, so Windows SmartScreen will warn about them. That is expected, and it also means you
cannot use a signature to verify what you downloaded. If that matters to you, build from source:
the whole thing compiles with the .NET 8 SDK and nothing else.
