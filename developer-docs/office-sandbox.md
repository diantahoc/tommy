# Office extraction & the AppContainer sandbox

> **Why `office_extract` runs LibreOffice UNSANDBOXED on Windows, and why the
> AppContainer path is deferred R&D.** Hard-won, verified live on 2026-10-23 —
> don't re-derive it. The C++ launcher implementation reference is
> [`appcontainer-launcher-spec.md`](appcontainer-launcher-spec.md).

---

## Current behavior (shipped)

`office_extract` converts Office docs (`.docx/.xlsx/.pptx/.odt`) via bundled
headless LibreOffice (`soffice --headless --convert-to`, txt for Writer/Impress,
csv for Calc, per-run isolated profile).

- It runs LibreOffice **unsandboxed**, and **only** when
  `TommyConfig.AllowUnsandboxedOffice` is on — surfaced as **Settings → Integrated
  tools → "Run LibreOffice unsandboxed (office_extract)"**, read **live per run**
  via a `Func<bool>` into `ToolContext` (no chat re-open needed). With it off,
  `office_extract` refuses.
- `src/Tommy.Tools/ConversionTools.cs`:
  `sandboxed = sandbox.IsAvailable && !ctx.AllowUnsandboxedOffice` — the opt-in
  **skips** the broken sandbox rather than falling back to it.
- LibreOffice MSI provisioning (`tools.manifest.json`, `install:"msi"`, marked
  `optional`) is unchanged and works unsandboxed.

## What we proved (2026-10-23)

1. **Stock LibreOffice (our MSI, 26.8.1) cannot start in any AppContainer** →
   "The application cannot be started. An internal error occurred." Cause: it
   creates its IPC named pipe under `\\.\pipe\` but AppContainer only permits
   `\\.\pipe\LOCAL\`. (LM Studio's `windows-appcontainer-ipc.patch` in
   `lmstudio-ai/office-document-processor-sources` patches `sal/osl/w32/pipe.cxx`
   for exactly this.)
2. **LM Studio's patched LibreOffice gets past the pipe, then fails "User
   installation could not be completed." (exit 77)** inside an AppContainer — its
   first-run *user installation* can't complete there.
3. **The exit-77 is NOT a launcher bug.** LM Studio's **own**
   `office-document-helper.exe appcontainer-exec … soffice --convert-to …` fails
   **identically (exit 77)** on this machine. It also reproduces with a
   pre-initialized profile and a scrubbed minimal environment.
4. **Both stock and patched LibreOffice convert fine unsandboxed** (produce the
   PDF/text). So the builds are good; only the AppContainer path is broken.

**Likely cause:** `program/fundamental.ini` → `CONFIGURATION_LAYERS` pulls
`winreg:CURRENT_USER` / `winreg:LOCAL_MACHINE` during bootstrap — a restricted
registry view under AppContainer. LM Studio's shipping product almost certainly
drives LibreOffice via a **UNO server** (their `officeipcthread` patch forces
`ipc=false`; `sandbox-ipc-directory.patch` supports it) plus helper subcommands
(`app-group-container`, `windows-user-ident`), **not** the `--convert-to` CLI.

## Our C++ launcher (`tommy-sandbox-x64.exe`) is CORRECT

Smoke-tested and verified (so a future revisit doesn't re-blame it):

- AppContainer profile + SID created (`tommysandbox.office`).
- Inheritable ACLs land and inherit: container SID has `(RX)` on the LibreOffice
  tree (confirmed on `program\soffice.bin` and `share\registry\main.xcd`) and
  `(M)` on the rw root — soffice actually wrote `profile\user\registrymodifications.xcu`.
- Argument passing is correct (PowerShell echoed space-containing args intact; the
  earlier `cmd` "syntax of the command is incorrect" was a cmd-in-AppContainer
  quirk, not a launcher bug).
- The container writes to both `%TMP%` (auto-redirected to
  `…\Packages\tommysandbox.office\AC\Temp`) and the rw root.

The launcher binary lives at `src/scripts/tommy-sandbox-x64.exe` (**untracked**).
Its full spec is [`appcontainer-launcher-spec.md`](appcontainer-launcher-spec.md).

## Licensing (settles redistribution)

- Patched **LibreOffice = MPL-2.0**, **fonts = GPLv2** → redistributable with
  notices + a source pointer (LM Studio's ToU §1.5 scopes OSS to its own licenses).
- **`office-document-helper.exe` = LM Studio proprietary → CANNOT be
  redistributed** (ToU §1.3/§4.2). It is untracked + gitignored
  (commit `33a8cf0`). It remains in earlier git history, but the repo has **no
  remote**, so nothing was distributed — scrub history before ever pushing.

## Reproduction commands

```bash
# unsandboxed baseline (works):
soffice.exe --headless --norestore --nolockcheck -env:UserInstallation=file:///<W>/profile \
  --convert-to pdf --outdir <W>/out <W>/input.txt

# sandboxed (exit 77 with patched LO, with BOTH our launcher and LM Studio's helper):
tommy-sandbox-x64.exe appcontainer-exec <LO-root> <W> <LO-root>/program/soffice.exe \
  --headless --norestore --nolockcheck -env:UserInstallation=file:///<W>/profile \
  --convert-to pdf --outdir <W>/out <W>/input.txt
```

## If/when revisiting the sandbox (deferred R&D)

Reproduce LM Studio's **UNO-server** invocation: start
`soffice.exe --accept="pipe,name=tommy;urp;" --headless …` inside the container,
then drive conversion over UNO from a client — or capture their running product's
exact soffice command line + environment with Process Explorer and mirror it.
Needs the **patched** LibreOffice (MPL — host it ourselves) + **our** launcher,
never LM Studio's helper. The redistributable combo would then be our
`tommy-sandbox-x64.exe` + patched MPL LibreOffice + GPL fonts, no proprietary code.
