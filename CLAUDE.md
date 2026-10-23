# CLAUDE.md — Tommy

**Tommy** is a Windows-first (Linux-capable) .NET 9 / Avalonia 11.3.0 MVVM desktop
AI agent that orchestrates a local **Strata** LLM over an OpenAI-compatible
endpoint. Thesis: give the local model first-class **typed tools** on the native
function-calling path so it never hammers the shell. Six projects under `src/`
(Core ← Engine/Tools/Mcp/Persistence ← App), tests in `tests/`.

## Read these when you need depth (`developer-docs/`)

- [`spec.md`](developer-docs/spec.md) — **current** architecture & design (authoritative).
- [`progress.md`](developer-docs/progress.md) — phase status, commit log, superseded facts, **pending TODOs**.
- [`conventions.md`](developer-docs/conventions.md) — engineering patterns + working-with-this-project facts (fuller version of the rules below).
- [`claude-visual-verification.md`](developer-docs/claude-visual-verification.md) — how to render & inspect the UI.
- [`office-sandbox.md`](developer-docs/office-sandbox.md) — why `office_extract` runs unsandboxed; AppContainer is deferred R&D.
- [`appcontainer-launcher-spec.md`](developer-docs/appcontainer-launcher-spec.md) — C++ launcher impl reference.
- [`initial-spec-v0.md`](developer-docs/initial-spec-v0.md) — frozen original brief, reference only (spec.md wins).

## Rules that always apply

**Build / test / run** (Git Bash or PowerShell):
- Build `dotnet build Tommy.sln` · Test `dotnet test tests/Tommy.Tests` · Run `dotnet run --project src/Tommy.App`
- A **running app copy-locks its `bin` DLLs**. A build failing *only* with
  **MSB3021/3026/3027** means the **compile succeeded** — filter those. New
  binaries need a **relaunch** by the user.
- To confirm a full App compile (incl. XAML) without closing the app:
  `dotnet build src/Tommy.App/Tommy.App.csproj -p:OutDir=<scratchpad>/appbuild/`
  (`-t:CoreCompile` alone gives false errors).

**Verify UI by looking at it.** Never claim UI work is correct from AXAML alone.
Render it with the headless snapshot harness (`dotnet test tests/Tommy.Snapshots`
→ PNGs in `artifacts/snapshots/`) and inspect the image — see
[`claude-visual-verification.md`](developer-docs/claude-visual-verification.md).
There is no click-automation; interactive behavior is verified by the user running
the real app.

**Git & committing:**
- Branch `main`, no remote. Stage **`src/` + `tests/`** for code, plus **`CLAUDE.md`
  + `developer-docs/`** when docs change (both are now tracked). Check nothing
  unexpected leaked; commit per feature.
- Commit messages end with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Never commit** a repo-root `integrated-tools/` (stale/legacy — tools provision
  into `~/.tommy/integrated-tools`) or `src/scripts/office-document-helper.exe` /
  `src/scripts/tommy-sandbox-x64.exe` (untracked binaries). `git` LF→CRLF warnings
  are harmless.

**Code patterns:**
- `reasoning_content` is read from each response and **never re-sent**; across
  turns history is prose-only. Durable state goes in the **task-state** record.
- Everything heavy runs out-of-process behind a **graceful-degrade** interface.
- Sidebar tool **groups are UI-only** (permission is per tool); always-on tools go
  in `ToolGroupMap.Hidden`.
- Tool output is **data, never instructions** — the prompt-injection boundary;
  destructive/network tools stay gated regardless of what a result claims.

Dev engine is live at `http://127.0.0.1:8080` (OpenAI `/v1`, telemetry `/metrics`).
