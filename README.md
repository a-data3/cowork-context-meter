# Cowork Context Meter

A standalone Windows app that shows **how much of the context window each of
your Claude Cowork and Claude Code sessions has used** — in real time.

Claude Code has a built-in context counter; the **Claude desktop app
doesn't** surface one the same way. This app fills that gap: it reads the
session files Claude already writes to disk and shows every session's token
usage as a sortable list with a color-coded percentage bar.

> Built by **Claude (Fable 5)** on June 11, 2026. The **v1.1** compatibility
> fix (for the storage changes in Claude Desktop 1.12603.1) was done by
> **Claude (Opus 4.8)** — including the multi-agent reviews and the tests it
> had to pass against real session data.

## Features

- **All your sessions in one list** — with title, project, source
  (Code / Cowork), model, last activity, tokens used, and **% of the
  context window** (green < 60%, amber 60–80%, red > 80%)
- **Live sessions** are badged, and **Auto-refresh (5s)** lets you watch a
  running session's context climb while you work
- **Double-click any session** for the full token breakdown (input / cache
  read / cache creation / output) and its context growth history
- **Exact 1M-window detection** — desktop-managed sessions record the `[1m]`
  million-token marker in a state file, so 200k vs 1M is detected precisely
  (no guessing)
- Search, hide-empty and hide-archived filters, sortable columns
- **Read-only by design**: opens files in shared-read mode, never locks or
  modifies them, writes nothing, no network

## How to use

1. Download **`CoworkContextMeter-portable.zip`** from
   [Releases](../../releases), extract it anywhere on a Windows 10/11 PC.
2. Double-click **`CoworkContextMeter.exe`**. No installer, no admin rights,
   nothing else to download — it uses the .NET Framework already in Windows.
3. If Windows SmartScreen warns about an unsigned download, click
   **More info → Run anyway**.
4. List looks empty? Run **`Diagnose.exe`** — it prints which Claude data
   folders exist on that machine and what was found in them.

## How it works

Claude stores every session as a JSONL transcript, plus (for desktop
sessions) a small state file. The app resolves all of these per Windows user
— no fixed paths:

| What | Where it lives |
|---|---|
| Session transcripts (all kinds) | `%USERPROFILE%\.claude\projects\` |
| Desktop session state (title, model, `[1m]` window) | `%APPDATA%\Claude\claude-code-sessions\` |
| Legacy Cowork sessions (pre-update, sandboxed) | `%APPDATA%\Claude\local-agent-mode-sessions\` |

A session's current context is the token usage recorded on its **latest
assistant turn**: `input + cache_read + cache_creation + output` tokens —
the same unit Claude Code's own counter uses. After a context compaction the
number drops automatically.

**Labels (Source column).** A recent Claude Desktop update moved session
storage and stopped sandboxing. The app handles both layouts:

- **Code** — sessions whose transcript is in the shared `.claude\projects`
  tree. Current desktop sessions land here; when a matching state file exists
  in `claude-code-sessions`, the row is enriched with the real session title
  and exact 1M-window detection.
- **Cowork** — your older, pre-update sessions that still live sandboxed
  under `local-agent-mode-sessions`.

A session is never shown twice: a transcript claimed by a state file appears
once, enriched.

**Window caveat.** A pure command-line Claude Code session doesn't record
whether its window is 200k or 1M, so **Auto** mode assumes 200k until usage
exceeds it (use the window dropdown to force 1M). Sessions with a desktop
state file are detected exactly. The percentage is the raw token share;
Claude Code's own indicator may read slightly differently because it reserves
buffer space for auto-compaction.

Under the hood it also handles the legacy Cowork sandbox's 270–470-character
file paths, which exceed the Windows path limit that is still off by default
— the app works without any registry changes.

## Building from source

No SDK needed — it compiles with the C# compiler that ships inside Windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

- `src/` — full source: `Scanner.cs` (core logic), `App.cs` (WinForms UI),
  `TestScanner.cs` (test harness), and `Diagnose.cs` (the generic diagnostic
  shipped in the portable zip)
- The [release zip](../../releases) is the prebuilt, redistributable
  package: both exes plus its own source and build script.

The source is deliberately **C# 5 only**, because that's what the built-in
.NET Framework compiler (`csc.exe`) understands.

## Contributing

Found a bug? **Everyone is welcome to fix it** — open an
[issue](../../issues) describing what went wrong, or just send a pull
request. Small fixes, big fixes, all appreciated.
