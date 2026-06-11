# Cowork Context Meter

A standalone Windows app that shows **how much of the context window each of
your Claude Cowork and Claude Code sessions has used** — in real time.

Claude Code has a built-in context counter; **Claude Cowork doesn't**. This
app fills that gap: it reads the session files Claude already writes to disk
and shows every session's token usage as a sortable list with a color-coded
percentage bar.

> Built by **Claude (Fable 5)** on **June 11, 2026**, in a Claude Code
> session — including the multi-agent code reviews and the tests it had to
> pass against real session data.

## Features

- **All your sessions in one list** — Cowork and Claude Code side by side,
  with title, project, model, last activity, tokens used, and **% of the
  context window** (green < 60%, amber 60–80%, red > 80%)
- **Live sessions** are badged, and **Auto-refresh (5s)** lets you watch a
  running session's context climb while you work
- **Double-click any session** for the full token breakdown (input / cache
  read / cache creation / output) and its context growth history
- **Exact window detection for Cowork** — Cowork records the `[1m]`
  million-token marker, so 200k vs 1M is detected precisely
- Search, hide-empty and hide-archived filters, sortable columns
- **Read-only by design**: opens files in shared-read mode, never locks or
  modifies them, writes nothing, no network

## How to use

1. Grab the `portable/` folder (or the zip from
   [Releases](../../releases)) and put it anywhere on a Windows 10/11 PC.
2. Double-click **`CoworkContextMeter.exe`**. No installer, no admin rights,
   nothing else to download — it uses the .NET Framework already in Windows.
3. If Windows SmartScreen warns about an unsigned download, click
   **More info → Run anyway**.
4. List looks empty? Run **`Diagnose.exe`** — it prints which Claude data
   folders exist on that machine and what was found in them.

## How it works

Claude stores every session as a JSONL transcript:

| Source | Location (resolved per Windows user — no fixed paths) |
|---|---|
| Claude Code | `%USERPROFILE%\.claude\projects\` |
| Cowork | `%APPDATA%\Claude\local-agent-mode-sessions\` |

A session's current context is the token usage recorded on its **latest
assistant turn**: `input + cache_read + cache_creation + output` tokens —
the same unit Claude Code's own counter uses. After a context compaction the
number drops automatically.

One caveat: Claude Code transcripts don't record whether a session has a
200k or 1M window, so the **Auto** mode assumes 200k until usage exceeds it
(use the window dropdown to force 1M). Cowork sessions are detected exactly.
The percentage is the raw token share; Claude Code's own indicator may read
slightly differently because it reserves buffer space for auto-compaction.

Under the hood it also handles Cowork's 270–470-character file paths, which
exceed the Windows path limit that is still enabled by default — the app
works without any registry changes.

## Building from source

No SDK needed — it compiles with the C# compiler that ships inside Windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

- `src/` — main app source (`Scanner.cs` core logic, `App.cs` WinForms UI,
  `TestScanner.cs` test harness)
- `portable/` — the redistributable package: prebuilt exes plus its own
  source and build script (`Diagnose.cs` replaces the machine-specific test
  harness with a generic diagnostic)

The source is deliberately **C# 5 only**, because that's what the built-in
.NET Framework compiler (`csc.exe`) understands.
