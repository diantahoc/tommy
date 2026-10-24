# Tommy — Build Progress & Pending Work

> Phase status, commit markers, superseded facts, and the TODO backlog. For the
> current architecture see [`spec.md`](spec.md); for how to build/test/commit see
> [`conventions.md`](conventions.md). The 8-phase build order itself is defined in
> [`initial-spec-v0.md`](initial-spec-v0.md#build-order).

Last updated: 2026-10-24 (298 Tommy.Tests + 8 Tommy.Snapshots).

---

## Phase status

**Done, committed on `main`:** Phases 0–6. **Next: Phase 7.**

- **Phase 0 — foundation.** 6-project skeleton, `StrataClient` + `SingleModelQueue`
  + reasoning split + streaming, health + vision/modality capability detection
  (`input_modalities` from `/v1/models`), telemetry; config persistence +
  managed/remote first-run setup + managed lifecycle.
- **Phase 1 — agent loop + file tools.** Tool model, `PermissionGrid`
  (autonomy→allow/ask, destructive always asks), `ToolRegistry` (attribute
  discovery + aliases), 10 native file tools with sandbox + diff previews; engine
  tool-call DTOs; the `AgentLoop` (reads parallel / writes sequential, approval
  callback, length recovery, loop detection, step budget); UI wiring with diff
  approval + autonomy selector. Proven live (model used `line_count`+`read_file`,
  no shell).
- **Phase 2 — persistence + projects.** Chat/project/workspace records,
  polymorphic JSONL events, `BlobStore`, repositories (workspace id = folder-path
  hash), `ParameterResolver` (chat→project→global), `ChatSearch`; shell UI with
  Chats/Code sidebar, search, transcripts persisted + rebuilt on reopen.
- **App-fix pass.** Tool output collapsed by default, approval shows params, tool
  aliases (`read_range`→`read_file_range`), file logger + global exception
  handlers, tool exec moved off the UI thread (hang fix).
- **Phase 3 — skills + memory.** `Skill`/`ISkillProvider` + `SKILL.md` parser,
  `SkillRepository` + 5 starter skills, `skill_search`/`skill_load`;
  `MemoryLayout` + `memory_recall`/`search`/`write` (write gated) + `MemoryStore`
  (index/reindex); `ChatViewModel` assembles system prompt = base + skill
  descriptions + memory index + upward convention-file cascade (`ConventionLoader`).
- **Phase 4 — MCP + tool breadth** (adopts `ModelContextProtocol.Core` 2.2.0).
  - `22ead6d` MCP client: Core config models; `McpConnection` (stdio/HTTP),
    `McpToolProxy`, `McpToolMapping`, `McpManager`. Name-prefix by instance id;
    tier from route+readonly; plain/user servers always-Ask via `ITool.ApprovalOverride`.
  - `38056e7` native git tools (`GitRunner` wraps the CLI, `GIT_TERMINAL_PROMPT=0`,
    timeout, graceful-no-git): status/log/diff/branch (Read), add/commit (Write,
    staged `--stat` preview), restore (Destructive), push/pull (Network). Paths sandboxed.
  - `36e6a69` `archive_list`/`archive_extract` (zip/tar/tar.gz, native .NET,
    zip-slip protection, tar symlinks skipped).
  - `ff679e7` MySQL integration layer (`IMcpIntegration`/`McpFieldSpec`/
    `McpInstance`, `MySqlIntegration` maps fields→env for `@benborla29/mcp-server-mysql`,
    read-only gates ALLOW_INSERT/UPDATE/DELETE). Instanceable.
  - `22b74f5` `image_read` (managed header parse, no native decoder),
    `VisionRouting` (model vs OCR by detected vision), `ocr` (Tesseract),
    `pdf_extract` (Python + PyMuPDF via `-I`). `NativeProbe` + `Subprocess`
    underpin graceful degradation.
- **Phase 5 — web research + safety** (harness-owned, NOT an MCP).
  - `5f57d33` `Tommy.Core/Research`: `TaggedData.Wrap`, `WebCapabilities`
    (allowlist + `IsIsolated`/`Disallowed` = the isolation invariant), abstractions.
  - `558d76f` `HttpWebFetcher`, `LlmSummarizer` (isolated no-tools completion),
    `WebResearchOrchestrator`. Tools `web_fetch` + `web_research` (Network).
  - `58391e7` `IHeadlessBrowser` + `browser_read` (degrades with no backend).
- **Phase 6 — telemetry + cost.**
  - `6dd378a` `EnergyMeter` (trapezoidal GPU-watts integration → kWh, gap guard,
    `Pause()`, `Cost(rate)`).
  - `3598684` `TelemetryViewModel` (formats `/metrics`), 2s poll feeding the active
    chat's `EnergyMeter` while sending; per-chat cost + per-message "~x Wh".

**Interleaved UI feedback rounds** (chat pane `6bbde25`, sidebar `ce831f6`,
context menu `aa0e87d`, circular ring + reasoning_effort `d0869f7`, chat-UI
redesign `37b546c`). UI-visible detail is summarized in the `tommy-ui-feedback`
memory; the durable conventions from those rounds are in [`conventions.md`](conventions.md).

## Later rounds (all committed on `main`)

- **Agent robustness + UX (2026-10-20/21).** `b991374` search-freeze fix
  (`WorkspaceWalk` prunes heavy dirs + 20s/50k budget) + `search_all` + Stop
  button + `ToolStarted`-before-exec; `47125eb` per-chat energy persistence;
  `a42a380` `ask_question` tool + **deny-stops-run** + length-truncation regen
  (cap 2048→4096, ceiling 8192; user set `MaxSteps` 25→100); `58d0b3b` chat-switch
  VM caching + context-usage reconciliation + markdown pipe tables; `db8e420`
  `present_file` + file pane; `98e9b66`/`b438a1c` bounded docked prompts.
- **Git reach + context survival (2026-10-21).** `78a221b` git tools take an
  optional `repo` subfolder; `3041979` `git_repos` discovery + search=grep
  steering; `fc6181a` bias-to-answer clause in the default prompt; `eed973e`
  context-overflow handling (typed `StrataRequestException`, per-result ~20k cap,
  compact-and-retry); `9d478fd` true summarize-on-compact (`ContextSummarizer`).
- **Endpoint auth + resume (2026-10-21).** `2ead139` bearer-token auth (core +
  first-run); `ee36b0c` editable token in Settings → Engine (live via
  `EngineService.SetAuthToken`); `9bf4d8d` auto-continue cut-off answers
  (`MaxAutoContinues` default 8) + **persistent resume reconstructed from the
  event log** (`ResumeHistoryBuilder`).
- **Streaming + task-state + SearXNG (2026-10-22/23).** `4aa0f5f` web_fetch
  summarizer fix (low effort + bigger budget + raw fallback); `65b208b`/`64e9693`
  **durable task-state** (`TaskState` + `TaskStateEvent` + `update_task_state`
  tool + task pane); `ef32347` flaky-VPN reliability (`SocketsHttpHandler` +
  TCP keep-alive); `2b6d4f6` varied "working" wording; `d389650` `video_info`;
  `81047bc` **token streaming** (default path); `2da70ed` middle-click autoscroll
  + Ctrl+Enter send queue; SearXNG backend (`0a2edd4`/`d4f4fd1`/`a14f1e8`/`13f14e5`
  — pluggable `IWebSearchProvider`, Tommy-managed SearXNG).
- **Self-provisioning + browser (2026-10-23).** `d1c24e7` runtime self-provisioning
  (`tools.manifest.json` + `Provisioning/`, root → `~/.tommy/integrated-tools` via
  `TommyHome`); `e12be7a` MinGit bundled + SearXNG folded into the provisioner;
  `11bf3e9`/`f7e6394` SearXNG Windows NTFS clone fix (`core.protectNTFS=false` +
  `git archive`); `ec5404c` reusable sandbox primitive + LibreOffice provisioned;
  `aebb04a` type-aware `present_file` (`CodeView`/HTML/PDF); browser feature P1–P3
  (`8561c81` Playwright `web_fetch_js`, node web-fetch DELETED; `3c88fb8` per-chat
  interactive browser; `a1b7a08` live screencast pane); `570959d` SearXNG
  health-check button.
- **Live-testing fixes + office-sandbox investigation (2026-10-23, later).**
  `328b024` browser-pane click-crash fix + resizable pane; `38620d6` managed
  SearXNG works (`limiter: false` + pip `tzdata`) + health Copy button; `d288ad7`
  filter benign health notes; `6d69edb` web_fetch gzip fix; `b6076f4` Markdown
  transcript export (`TranscriptMarkdown`); `c9ed042` optional-helper provisioning
  non-fatal + LibreOffice optional; `5384479` **office_extract unsandboxed behind
  a Settings toggle** (`AllowUnsandboxedOffice`); `33a8cf0` untrack
  `office-document-helper.exe`.
- **Docs/tooling (2026-10-24).** `6c37252` hide `update_task_state` + "Web Browser"
  tool group; this documentation reorganization.
- **MSSQL integration + UI overhaul (2026-10-24).**
  - `ddbd3f2` — **Microsoft SQL Server MCP integration** (`MsSqlIntegration`,
    instanceable, over `mssql-mcp-server` / vicagbasi/mssql-mcp): per-field inputs
    composed into `MSSQL_CONNECTION_STRING`, read-only (Read tier), bundled-Node /
    npx launch, pinned+SHA-256 manifest entry. Pure-JS `tedious` (no ODBC).
    `@bilims/mcp-sqlserver` noted as the alternative. +8 tests.
  - `0631bc9` — **wide markdown tables wrap** to fit the chat bubble
    (`MarkdownTableView` → proportional/star columns + MinWidth, host stretches)
    instead of clipping.
  - `9e35a29` — **custom title bar** (`ExtendClientAreaToDecorationsHint` +
    NoChrome): full-width bar with our own minimize/maximize/close, the toolbar
    consolidated into it (app menu, sidebar toggle, chat/code switcher, chat title,
    context, browser, engine pill), draggable, + a thin `WindowBorder`. Bundles
    **Font Awesome 5 Free Solid** (`Assets/Fonts/fa-solid-900.ttf`, OFL) as the
    `IconFont` resource for all title-bar glyphs. `ChatViewModel.Title` added.
  - `021ec9f` — **chat input bar redesign**: rounded compose card, round
    attach/autonomy/effort buttons + circular send (FA glyphs); autonomy now shows
    meaningful text (Review each / Review destructive / Auto-approve) via a flyout
    (`EnumMatchConverter`, SetAutonomy/SetThinkingEffort commands); input focused
    on chat open.
  - UI icon switch is **title-bar/toolbar only** so far; Settings/First-Run and
    in-chat glyphs still use their old icons.
  - **Boolean MCP config fields render as on/off switches**, not "true"/"false"
    text boxes (`McpFieldSpec.Boolean`; `McpFieldInput.BoolValue` over the canonical
    string). Applies to MySQL (allow schema changes) and MSSQL (encrypt / trust
    cert / allow custom SELECT). +1 snapshot (`Mcp-config-dialog-mssql`). The config
    label column widened (190px) so the longer labels aren't truncated.
  - **Compose "+" menu → Add database** (MySQL/MariaDB, Microsoft SQL Server) opens
    the same Connect-MCP dialog as the Connected-MCPs picker, reusing
    `ConfigureIntegrationAsync` (`OnAddDatabase`, selected by integration key). The
    attach flyout opens upward (`Placement="Top"`).
  - **Collapsed sidebar is now fully hidden** (`SidebarWidthPx` 0, border
    `IsVisible=SidebarExpanded`) instead of leaving an empty 54px rail/sliver.

**Missing UI (surfaced to the user 2026-10-24, not yet built):** no way to
**create a project** (only Sort&group → "Group by project" and the per-chat
"Assign to project" submenu, which need a project to already exist); and no way to
**view / restore archived chats** (right-click → Archive sets `Archived=true` and
`BuildChatList` filters them out, with no archived view or unarchive path).

## Superseded facts (so older notes don't mislead)

- `web_fetch_js` is **Microsoft.Playwright (.NET)**, not the old node/MCP path;
  web-search-mcp is **gone** (search = SearXNG only).
- Integrated tools live in **`~/.tommy/integrated-tools` (runtime-provisioned)**,
  not beside-app or committed by the user; `build-and-copy` is **retired**.
- The SearXNG kit is **`src/scripts/searxng/` (committed)**, not a separate repo;
  SearXNG is pinned to a **commit and cloned at runtime**, not a release zip.
- `office_extract` runs **unsandboxed** behind `AllowUnsandboxedOffice`, **not**
  sandboxed (AppContainer can't run LibreOffice — see [`office-sandbox.md`](office-sandbox.md)).
  `office-document-helper.exe` is **no longer committed**.
- Endpoint auth **is implemented** (bearer token); the old "no endpoint auth v1"
  resolution is void.

## Pending / next

**Phase 7 — control + recovery depth** (next build phase): interrupt-and-inject
(the Stop button is a start), plan-then-act, undo / change-journal,
checkpoint/restore, dry-run (intercept at the tier layer), loop/budget detection
(loop detection already exists), full run transcript. Last phase; then the Future
bucket stays deferred. On-disk storage for the undo journal / checkpoints / plans
was explicitly deferred here from Phase 2.

**Known follow-ups (flagged, not yet requested):**
- Per-server MCP tool grouping — MCP tools currently land in the "Other" sidebar group.
- Move the MySQL password / MCP auth tokens out of plaintext `config.json` once a
  real secrets store (DPAPI/libsecret) is wired behind `ISecretStore`.
- Title-bar: navigation back/next and the workspace file-tree button (deferred);
  extend the Font Awesome icon switch to Settings / First-Run / in-chat glyphs.

**User-confirmed live (2026-10-24):** the **browser pane** — agent input
forwarding into the screencast works end to end; and the **custom title bar** —
maximize, dragging on any empty area, and screen-edge snap all behave like a
native Windows title bar (no clipped top edge observed).

**Not yet user-confirmed live** (built + unit-tested, needs an interactive pass):
`office_extract` via the unsandboxed toggle, and the **MSSQL integration** (needs
a real SQL Server to point at).

**Spec resolutions settled (15):** multi-model deferred (one engine + queue);
single Autonomy enum; scratchpads native in Phase 1; per-tool enablement (groups
are UI only); DB MCP = MariaDB/MySQL; context size read from the Strata API;
endpoint auth **now implemented** (was deferred); token counts = API usage +
local pre-send estimate; `skill_search`/`skill_load` in v1; plain-client MCP tiers
default-safe (Ask) + override; undo/checkpoint storage deferred to Phase 7;
OCR→P4 / vision detection→P0; dual-memory conflict surfaced at reindex;
single-window chat-list + tabs; dry-run intercept at tier.
