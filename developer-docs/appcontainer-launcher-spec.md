# Tommy AppContainer Sandbox Launcher — Specification

> Implementation reference for the C++ launcher. For *why* office extraction runs
> unsandboxed today and why this is deferred R&D, see
> [`office-sandbox.md`](office-sandbox.md).

**Status:** draft for implementation
**Target:** `tommy-sandbox.exe` (Windows, x64). Replaces LM Studio's `office-document-helper.exe`.
**Language:** C++17, Win32, MSVC, statically linked (`/MT`). No third-party deps.
**Why we need our own:** LM Studio's helper is proprietary and may not be redistributed (their ToU §1.3/§4.2). The patched LibreOffice (MPL-2.0) and fonts (GPLv2) *are* redistributable, but the sandbox launcher is not — so Tommy ships its own.

---

## 1. Purpose

Run a single untrusted child process (headless LibreOffice `soffice.exe`) inside a **Windows AppContainer** with:

- **No network** (no networking capabilities granted → connections are blocked by the OS).
- **Exactly one read-only root** it may read/execute (the LibreOffice install + the copied input document).
- **Exactly one read-write root** it may write (a per-run temp work dir: output + the LibreOffice user profile + temp).
- Access to the usual **system DLLs** (`System32`, etc.) — granted by default to all AppContainers, no action needed.
- Everything else on the machine **inaccessible** (the user's files, registry user hive writes, other temp, the network).

The launcher is **synchronous**: it starts the child, waits for it, and exits with the child's exit code. Tommy (the parent) captures stdout/stderr and enforces the wall-clock timeout by killing the launcher's process tree.

This is a reusable primitive — today it sandboxes `soffice`, later any untrusted converter.

---

## 2. CLI contract (must match Tommy's existing caller)

```
tommy-sandbox.exe appcontainer-exec <ro-root> <rw-root> <program> [arg1 arg2 ...]
```

- `appcontainer-exec` — the verb (keep this exact token; Tommy's `AppContainerSandbox.BuildArgs` already emits it).
- `<ro-root>` — absolute path to the directory granted **read + execute**. In practice the LibreOffice install root (holds `program\soffice.exe`, `share\`, `presets\`). The input document is copied into `<rw-root>` by Tommy, so only one ro root is required.
- `<rw-root>` — absolute path to the directory granted **read + write + execute + delete**. A fresh per-run temp dir Tommy created; holds the copied input, the `out\` dir, the `profile\` (LibreOffice `UserInstallation`), and should also hold the child's `TMP`.
- `<program>` — absolute path to the executable to run (e.g. `…\LibreOffice\program\soffice.exe`). Must live under `<ro-root>`.
- `[args…]` — passed through verbatim to the child, re-quoted correctly (see §7).

Tommy builds this argv in `src/Tommy.Tools/Internal/Sandbox.cs` → `AppContainerSandbox.BuildArgs`. **Keep the contract identical so no Tommy code changes except the helper's name/path** (see §11).

Exit status:
- On success: the launcher exits with **the child's exit code**.
- On launcher-internal failure (bad args, profile/ACL/CreateProcess failure): print a one-line diagnostic to **stderr** and exit **125** (reserved; distinct from LibreOffice's own small codes). Tommy already treats a missing output file + stderr as a failure, so the exact value is informational.

---

## 3. High-level sequence

```
parse args
→ derive/create AppContainer profile  → appContainerSid
→ grant appContainerSid on <ro-root>  (read/execute, inheritable)
→ grant appContainerSid on <rw-root>  (modify, inheritable)
→ build child environment (inherit + TMP/TEMP → <rw-root>\tmp)
→ build child command line (program + quoted args)
→ CreateProcess with SECURITY_CAPABILITIES (0 capabilities) + inherited std handles
→ WaitForSingleObject(child, INFINITE)
→ GetExitCodeProcess
→ (cleanup) revoke the temp ACE / delete per-run profile
→ return child exit code
```

All APIs are Unicode (`…W`). No elevation required: creating an AppContainer profile and editing DACLs on directories the user owns are unprivileged operations.

---

## 4. AppContainer profile

Use a **stable** container name so the SID is deterministic and the ro-root grant can persist harmlessly between runs:

```cpp
// name: <= 64 chars, no backslashes, package-moniker-ish
const wchar_t* kName = L"TommySandbox.Office";
PSID appContainerSid = nullptr;

HRESULT hr = CreateAppContainerProfile(
    kName, L"Tommy Office Sandbox", L"Headless document conversion sandbox",
    /*capabilities*/ nullptr, /*count*/ 0, &appContainerSid);

if (hr == HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)) {
    hr = DeriveAppContainerSidFromAppContainerName(kName, &appContainerSid);
}
if (FAILED(hr)) { /* stderr + exit 125 */ }
```

Notes:
- **Capabilities = none.** We want no network and no extra device access. With zero capabilities the process still runs; it just can't reach the network or capability-gated resources. (If a future tool needs outbound HTTP, add the `internetClient` capability via `DeriveCapabilitySidsFromName(L"internetClient", …)` and pass it in `SECURITY_CAPABILITIES`.)
- `CreateAppContainerProfile`/`DeriveAppContainerSidFromAppContainerName` are in **userenv.dll** (link `userenv.lib`).
- Free the SID with `FreeSid(appContainerSid)` at the end. The profile itself persists (fine for a stable name). Only call `DeleteAppContainerProfile(kName)` if you chose a per-run unique name (see §10).

### Per-run alternative (stronger isolation)
Use `kName = L"TommySandbox." + <random 8 hex>`; create it, grant ACLs, run, then `DeleteAppContainerProfile(name)` and revoke both ACEs in cleanup. Trade-off: must reliably revoke the ro-root ACE even on crash, or the install dir accumulates dead-SID ACEs. **Default to the stable name**; document the per-run option.

---

## 5. Granting directory access (DACLs)

AppContainer processes get **no** access to arbitrary paths — only to objects whose DACL contains an ACE for the container SID (or for `ALL APPLICATION PACKAGES`). System dirs already carry the latter; our two roots do not, so we add an explicit ACE to each.

For each root, **merge** a new grant ACE into the existing DACL (never replace it) and make it inheritable so files/subdirs created later are covered:

```cpp
bool GrantAppContainer(const wchar_t* path, PSID sid, DWORD accessMask) {
    PACL oldDacl = nullptr; PSECURITY_DESCRIPTOR sd = nullptr;
    DWORD err = GetNamedSecurityInfoW(path, SE_FILE_OBJECT,
        DACL_SECURITY_INFORMATION, nullptr, nullptr, &oldDacl, nullptr, &sd);
    if (err != ERROR_SUCCESS) return false;

    EXPLICIT_ACCESSW ea = {};
    ea.grfAccessPermissions = accessMask;
    ea.grfAccessMode        = GRANT_ACCESS;
    ea.grfInheritance       = CONTAINER_INHERIT_ACE | OBJECT_INHERIT_ACE;
    ea.Trustee.TrusteeForm  = TRUSTEE_IS_SID;
    ea.Trustee.TrusteeType  = TRUSTEE_IS_GROUP;   // AppContainer SID
    ea.Trustee.ptstrName    = reinterpret_cast<LPWSTR>(sid);

    PACL newDacl = nullptr;
    err = SetEntriesInAclW(1, &ea, oldDacl, &newDacl);
    if (err != ERROR_SUCCESS) { LocalFree(sd); return false; }

    err = SetNamedSecurityInfoW(const_cast<LPWSTR>(path), SE_FILE_OBJECT,
        DACL_SECURITY_INFORMATION, nullptr, nullptr, newDacl, nullptr);

    LocalFree(newDacl); LocalFree(sd);
    return err == ERROR_SUCCESS;
}
```

Access masks:
- **ro-root:** `GENERIC_READ | GENERIC_EXECUTE` — read data/attrs + load DLLs/exes.
- **rw-root:** `GENERIC_READ | GENERIC_WRITE | GENERIC_EXECUTE | DELETE` — create/modify/delete under the work dir.

Apply to `<rw-root>` **before** creating `<rw-root>\tmp` (or create tmp, then grant — either works since the ACE is inheritable and re-applied to the tree root). Grant on the directory root; inheritance covers the contents.

`GetNamedSecurityInfoW`/`SetNamedSecurityInfoW`/`SetEntriesInAclW` are in **advapi32.dll**.

> The ro-root ACE for a **stable** SID is idempotent and harmless to leave in place; re-granting on each run is a no-op merge. The rw-root is a per-run temp dir Tommy deletes afterward, so its ACE disappears with it.

---

## 6. Launching the child with SECURITY_CAPABILITIES

```cpp
SECURITY_CAPABILITIES sc = {};
sc.AppContainerSid = appContainerSid;
sc.Capabilities    = nullptr;   // 0 capabilities
sc.CapabilityCount = 0;
sc.Reserved        = 0;

// Attribute list: 1 attribute (security capabilities). Use 2 if you also
// pass an explicit handle list (recommended for std handles — see §8).
SIZE_T size = 0;
InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
auto* attrs = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(HeapAlloc(GetProcessHeap(), 0, size));
InitializeProcThreadAttributeList(attrs, 1, 0, &size);
UpdateProcThreadAttribute(attrs, 0,
    PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES,
    &sc, sizeof(sc), nullptr, nullptr);

STARTUPINFOEXW si = {};
si.StartupInfo.cb = sizeof(STARTUPINFOEXW);
si.lpAttributeList = attrs;
si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
si.StartupInfo.hStdInput  = GetStdHandle(STD_INPUT_HANDLE);
si.StartupInfo.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
si.StartupInfo.hStdError  = GetStdHandle(STD_ERROR_HANDLE);

PROCESS_INFORMATION pi = {};
BOOL ok = CreateProcessW(
    programPath,                 // lpApplicationName (absolute, under ro-root)
    cmdLine,                     // lpCommandLine (mutable buffer; see §7)
    nullptr, nullptr,
    TRUE,                        // bInheritHandles — needed for std handles
    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
    envBlock,                    // §9
    rwRoot,                      // lpCurrentDirectory — a writable cwd
    &si.StartupInfo, &pi);
```

- `EXTENDED_STARTUPINFO_PRESENT` is mandatory to pass `lpAttributeList`.
- On success, `WaitForSingleObject(pi.hProcess, INFINITE)`, then `GetExitCodeProcess(pi.hProcess, &code)`.
- Clean up: `DeleteProcThreadAttributeList(attrs)`, `HeapFree`, `CloseHandle(pi.hThread/hProcess)`.
- `InitializeProcThreadAttributeList`/`UpdateProcThreadAttribute`/`CreateProcessW` are in **kernel32.dll**.

---

## 7. Child command line construction & quoting

`CreateProcessW` takes a single mutable `lpCommandLine`. Build it as: the program path (quoted), then each arg quoted per the Windows `CommandLineToArgvW` rules, space-separated. Pass `programPath` *also* as `lpApplicationName` so there's no PATH search / `.exe` ambiguity.

Quoting algorithm per argument (standard MSVC rules):
- If the arg has no space, tab, or `"`, emit it as-is.
- Otherwise wrap in `"…"`, and inside: double any run of backslashes that precedes a `"` (or the closing quote), and escape each `"` as `\"`.

Write the assembled command line into a **non-const, null-terminated `wchar_t` buffer** (CreateProcessW may modify it). Include the program path as `argv[0]`.

---

## 8. Standard handle inheritance

LibreOffice is headless; Tommy wants its stdout/stderr. The launcher inherits its own std handles from Tommy (Tommy redirects them) and forwards them to the child:

- Set `STARTF_USESTDHANDLES` + the three `GetStdHandle` values (above), and `bInheritHandles = TRUE`.
- **Recommended:** also pass an explicit `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` containing exactly those three handles (bump the attribute count to 2). This avoids leaking unrelated inheritable handles into the AppContainer and makes inheritance reliable under AppContainer restrictions.
- Ensure each forwarded handle is inheritable (`SetHandleInformation(h, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT)`), or duplicate it inheritable first.

If stdin isn't needed, pass a handle to `NUL` (or the inherited stdin) — don't leave it `INVALID_HANDLE_VALUE` when `STARTF_USESTDHANDLES` is set.

---

## 9. Child environment (critical for LibreOffice)

LibreOffice must write its profile and temp inside the rw root — otherwise bootstrap fails exactly like the stock-in-sandbox case. Tommy already passes `-env:UserInstallation=file://<rw>/profile`, but the process also uses `%TMP%/%TEMP%`.

Build the child env block = **a copy of the launcher's environment** with these overridden to point inside `<rw-root>`:

- `TMP   = <rw-root>\tmp`
- `TEMP  = <rw-root>\tmp`

Create `<rw-root>\tmp` before launch (it's covered by the inheritable rw ACE). Keep the rest of the inherited environment (PATH etc.) so the child can find system DLLs. Pass the block with `CREATE_UNICODE_ENVIRONMENT`. (Use `GetEnvironmentStringsW`, copy, splice in the overrides, double-null-terminate.)

> Do **not** rely on the inherited `%TEMP%` (it points to the user's profile temp, outside the sandbox → access denied → "cannot be started / internal error").

---

## 10. Cleanup & lifetime

- Always: `DeleteProcThreadAttributeList`, free the attribute buffer, `CloseHandle` the process/thread, `FreeSid(appContainerSid)`, `LocalFree` any security descriptors.
- Stable container name (default): leave the profile and the ro-root ACE in place (idempotent). Tommy deletes `<rw-root>` after reading output, which removes the rw ACE with it.
- Per-run container name (optional): in a `__try/__finally`-style guard, revoke the ro-root ACE (`SetEntriesInAclW` with `REVOKE_ACCESS`) and `DeleteAppContainerProfile(name)` even on failure.
- The launcher must not hang: it only ever waits on the child. Tommy owns the timeout and will `TerminateProcess` the launcher tree; that's acceptable (the OS tears down the AppContainer job).

---

## 11. Tommy-side integration (what changes once the exe exists)

Minimal, because the CLI is unchanged:

1. **Ship the binary** from the `scripts/` kit instead of LM Studio's helper. In `src/scripts/tools.manifest.json`, replace the `office-document-helper` script entry with e.g.:
   ```json
   "tommy-sandbox": {
     "source": "tommy-sandbox.exe",
     "dest": "helpers/tommy-sandbox.exe",
     "os": "windows"
   }
   ```
   and place the built `tommy-sandbox.exe` in `src/scripts/`.
2. **Point the resolver at it** — `src/Tommy.Core/Runtime/IntegratedTools.cs` → `FindSandboxHelper()` returns `helpers/tommy-sandbox.exe`.
3. **No change** to `src/Tommy.Tools/Internal/Sandbox.cs` (`AppContainerSandbox.BuildArgs` already emits `appcontainer-exec <ro> <rw> <program> args…`), nor to `ConversionTools.cs`.
4. LibreOffice becomes an **archive** of the patched MPL build hosted on our repo (see `tools.manifest.json` `libreoffice` entry — drop `install:"msi"`, set `url`+`sha256`), shipped with its `license.txt` + `NOTICE` and a `LIBREOFFICE-SOURCE.txt` pointer for MPL compliance.

So the final redistributable combo is **our** `tommy-sandbox.exe` + the **MPL** patched LibreOffice + **GPL** fonts — no proprietary LM Studio code.

---

## 12. Verification checklist

Build: `cl /std:c++17 /O2 /MT /EHsc tommy-sandbox.cpp /link userenv.lib advapi32.lib kernel32.lib`

1. **Smoke:** `tommy-sandbox.exe appcontainer-exec <lo-root> <tmpwork> <lo-root>\program\soffice.exe --headless --norestore --nolockcheck -env:UserInstallation=file:///<tmpwork>/profile --convert-to pdf --outdir <tmpwork>\out <tmpwork>\input.txt` → `input.pdf` appears, exit 0, **no GUI dialog**, no 93-second hang.
2. **Isolation — files:** have the child try to read a path outside the two roots (e.g. `%USERPROFILE%\Documents\secret.txt`) → access denied.
3. **Isolation — network:** a test program that opens a socket inside the container → fails (no networking capability).
4. **Write confinement:** child can write under `<rw-root>` but not under `<ro-root>` nor elsewhere.
5. **Exit propagation:** child returning 3 → launcher exits 3; launcher arg error → exits 125 with a stderr line.
6. **stdout/stderr:** captured by the parent (run under Tommy's `Subprocess`, confirm `office_extract` returns text and the benign "Could not find platform independent libraries" line is the only noise).
7. **Unicode/space paths:** roots and input with spaces and non-ASCII characters work.
8. **Idempotent re-run:** second run reuses the stable profile without piling up ACEs or failing.

---

## 13. API reference (libs)

| API | DLL / lib |
|---|---|
| `CreateAppContainerProfile`, `DeriveAppContainerSidFromAppContainerName`, `DeleteAppContainerProfile` | userenv.dll / `userenv.lib` |
| `DeriveCapabilitySidsFromName` (only if adding capabilities) | kernelbase / `userenv.lib` |
| `GetNamedSecurityInfoW`, `SetNamedSecurityInfoW`, `SetEntriesInAclW`, `FreeSid` | advapi32.dll / `advapi32.lib` |
| `InitializeProcThreadAttributeList`, `UpdateProcThreadAttribute`, `DeleteProcThreadAttributeList`, `CreateProcessW`, `GetEnvironmentStringsW`, `SetHandleInformation` | kernel32.dll / `kernel32.lib` |

Minimum OS: Windows 10 1709+ (AppContainer with security-capabilities proc-thread attribute is well supported there).

---

## 14. Open questions / notes for the implementer

- **Architecture:** build x64 (matches the x86_64 LibreOffice).
- **Static CRT (`/MT`)** so the exe has no VC++ redist dependency.
- **Patched-LibreOffice pipe:** the `\\.\pipe\LOCAL\` behavior is inside the LibreOffice build (LM Studio's `windows-appcontainer-ipc.patch`), so the launcher does nothing special for IPC — just confirm the patched build is the one used.
- If `CreateProcessW` fails with `ERROR_ACCESS_DENIED` despite the ACE, double-check the ro-root ACE is **inheritable** and actually reached `program\soffice.exe`, and that the child's `lpCurrentDirectory` (`<rw-root>`) is writable.
- Keep the launcher **single-purpose and tiny** (~300–400 lines). No config files, no logging to disk — diagnostics go to stderr only.
