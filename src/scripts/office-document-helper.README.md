# office-document-helper.exe

Place **`office-document-helper.exe`** in this folder. It is the Windows AppContainer sandbox
launcher Tommy uses to run LibreOffice `soffice` on untrusted documents (`office_extract`).

- **CLI used by Tommy:** `office-document-helper.exe appcontainer-exec <read-only-root> <read-write-root> <program> [args…]`
- **Provisioning:** copied to `~/.tommy/integrated-tools/helpers/office-document-helper.exe`
  (manifest `scripts.office-document-helper`, Windows only). Resolved by
  `IntegratedTools.FindSandboxHelper()`.

## Origin & license

This binary comes from **LM Studio's** `office-document-processor` framework. Sources:
<https://github.com/lmstudio-ai/office-document-processor-sources>.

Before committing/redistributing it in this repo, confirm its license permits that. The LibreOffice
build it sandboxes is MPL-2.0 / LGPL-3.0 (redistributable with notices); the helper launcher is LM
Studio's own code — verify its terms.

Linux doesn't use this binary — it sandboxes with system `bwrap` (bubblewrap) instead.
