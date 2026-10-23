# Tommy — Engineering Conventions

> Durable facts about how this project is built, tested, committed, and worked on.
> The load-bearing must-dos are mirrored into the root [`../CLAUDE.md`](../CLAUDE.md)
> so they survive context compaction; this file is the fuller reference.

---

## Build / test / run (Git Bash or PowerShell)

- **Build:** `dotnet build Tommy.sln` · **Test:** `dotnet test tests/Tommy.Tests`
- **Smoke** (needs Strata running): `dotnet run --project tools/Tommy.SmokeTest`
- **Run the app:** `dotnet run --project src/Tommy.App`
- Isolate app data in tests: set `TOMMY_HOME` to a temp dir; seed
  `$TOMMY_HOME/config.json` to skip first-run.
- App logs: `~/.tommy/logs/app-YYYYMMDD.log` — the `step N: finish=… toolCalls=…`
  lines are invaluable for diagnosing stalls.

## Building while the app is running (copy-lock)

A running app **copy-locks** its `bin` DLLs. A build then fails only with
**MSB3021 / MSB3026 / MSB3027** (copy errors) — which means **the compile
succeeded**. The rule: filter MSB302x; "only those = clean compile."

- Verify backend: `dotnet test tests/Tommy.Tests` (does **not** reference Tommy.App).
- **Confirm a full App compile (including XAML) without closing the app:**
  ```bash
  dotnet build src/Tommy.App/Tommy.App.csproj -p:OutDir=<scratchpad>/appbuild/
  ```
  Redirecting `OutDir` copies deps to a fresh dir instead of the locked `bin`, so
  it builds clean. `-t:CoreCompile` alone does **not** work (it skips reference
  resolution → false "System not found" errors).
- `tests/Tommy.Snapshots` **does** reference Tommy.App (headless renders + plain
  unit tests), so it is subject to the same copy-lock while the app runs.
- **New binaries require a relaunch** — the running app won't pick up a rebuild.

## Git & committing

- Branch `main`, **no remote**. Commits end with:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- **Stage `src/` + `tests/`** for code changes; also **`CLAUDE.md` +
  `developer-docs/`** when the docs change (both are tracked as of 2026-10-24).
  Sanity-check nothing unexpected leaked. Commit per feature with a descriptive
  message.
- **Never commit:**
  - A repo-root `integrated-tools/` folder — **stale/legacy**; tools are
    runtime-provisioned into `~/.tommy/integrated-tools`.
  - `src/scripts/office-document-helper.exe` — LM Studio's **proprietary** binary
    (cannot be redistributed), gitignored/untracked. So is the user's own
    `src/scripts/tommy-sandbox-x64.exe`. See [`office-sandbox.md`](office-sandbox.md).
- git warns `LF will be replaced by CRLF` — harmless.

## Verifying UI work — look at the pixels

**Do not claim UI work is correct from the AXAML alone.** Tommy has a headless
snapshot harness (`tests/Tommy.Snapshots`) that renders the real windows to PNGs
you can open and inspect. Run it, look at the images, iterate. Full how-to:
[`claude-visual-verification.md`](claude-visual-verification.md).

There is **no GUI click-automation** here — interactive behavior (click-driven
flows, live streaming visuals) is verified by the **user running the real app**.
Snapshots cover static layout/appearance; the user covers interaction.

## Patterns worth knowing

- **Interactive-UI tools** carry their effect through `ToolContext` callbacks
  (`AskUser`, `PresentFile`, `RecordTaskState`, `OpenBrowser`). `ChatViewModel`
  wires them, **marshals to the UI thread**, and raises events the `ShellViewModel`
  turns into view-models. Always-on / hidden tools go in `ToolGroupMap.Hidden`
  (hidden tools are never removed from `_enabledTools`, which seeds from **all**
  registry tools — that is what keeps them always-enabled).
- **Sidebar tool groups** are UI-only; permission stays per tool. `git_*` → "Git",
  `browser_*` → "Web Browser"; unmapped → "Other" (MCP tools currently land there).
- **`reasoning_content` is never re-sent** — read it from the response, route it to
  the activity block, drop it. Across turns `BuildHistory` is prose-only. Durable
  state goes in the **task-state** record, not in reasoning.
- **Everything heavy is out-of-process** and behind a graceful-degrade interface
  (`NativeProbe`/`Subprocess`): a missing runtime disables one tool with a clear
  message instead of crashing the app.
- **Path sandbox** — tools resolve paths under the workspace root via
  `PathSandbox`/`PathSandbox.TryResolveUnder` (keeps the escape guard). Git tools
  accept an optional `repo` subfolder, also sandboxed.
- Compiled bindings are on — a binding typo is a **compile error**, so a clean
  App build already catches most XAML-binding mistakes.

## Working with the user

- The user runs the **real desktop app** against a sample Dashboard project and
  reports bugs with screenshots + the log file. Diagnose from the log's
  `step N: finish=… toolCalls=…` lines.
- The engine is live at `http://127.0.0.1:8080` during development (see the
  `strata-dev-endpoint` memory and [`spec.md`](spec.md#engine-modes--telemetry)).
- Memory files (outside the repo, at
  `C:\Users\Twenty\.claude\projects\E--Projects-Tommy\memory\`) hold the same
  knowledge as these docs plus session-to-session notes; keep both roughly in sync
  when something durable changes.
