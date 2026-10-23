# searxng-tommy

The **managed SearXNG** build that Tommy downloads and runs for `web_research`. SearXNG runs as a
plain native-Windows process — a Python venv + [Waitress](https://pypi.org/project/waitress/), no
uWSGI/WSL/Docker/Redis — with the JSON API enabled so Tommy's `SearxngSearchProvider` can call it
over localhost. The rate limiter runs in-memory via `fakeredis` (**Mode B**), no external server.

> This folder is the source of a **separate repo** (not part of Tommy's git history). Publish it as
> its own repository and attach a release archive; point Tommy at that archive's URL.

## How Tommy uses it

Tommy (`SearxngInstaller` / `ManagedSearxng`) does the heavy lifting on the user's machine:

1. Downloads the release **`.zip`** from the URL set in Settings → Web search → *Release URL*.
2. Extracts it under `~/.tommy/searxng/`.
3. Builds a venv with the **bundled** Python (no system Python needed) and
   `pip install -r requirements.txt waitress "fakeredis[lua]"`.
4. Runs `apply_windows_patch.py searx/valkeydb.py` (idempotent) — the one Windows fix.
5. Launches `serve_waitress.py` on `127.0.0.1:8888` the first time `web_research` runs, and calls
   `GET /search?q=…&format=json`.

So the release archive is **source only** — no `.venv`, no `.git`. Tommy builds the environment.

## Release archive layout (the contract Tommy relies on)

The `.zip` must extract to a tree that contains, somewhere under it, **one directory** holding:

```
<server>/
  serve_waitress.py          # launcher (from this kit)
  settings_windows.yml       # Mode B settings (from this kit)
  apply_windows_patch.py     # the Windows patch (from this kit)
  requirements.txt           # upstream SearXNG deps
  searx/                     # upstream SearXNG package (vanilla; Tommy patches it)
    valkeydb.py
    webapp.py
    …
```

Tommy finds `<server>` by locating `serve_waitress.py`. The `searx/` source may be vanilla upstream
— Tommy applies the patch — or pre-patched (the patch is a no-op then).

## Building a release

```powershell
# 1. Verify the build works locally (creates a .venv and smoke-tests the import).
./setup.ps1
./searxng/.venv/Scripts/python.exe ./searxng/serve_waitress.py   # optional: check the JSON API
# optional: ./searxng/.venv/Scripts/python.exe ./searxng/test_fakeredis_limiter.py

# 2. Produce the SOURCE-ONLY release zip (no .venv, no .git).
Remove-Item .\searxng\.venv -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item .\searxng\.git  -Recurse -Force -ErrorAction SilentlyContinue
Compress-Archive -Path .\searxng\* -DestinationPath .\searxng-tommy.zip -Force
```

Attach `searxng-tommy.zip` to a GitHub release and copy its asset URL into Tommy's
Settings → Web search → *Release URL*, then click **Install**.

## Files

- `apply_windows_patch.py` — guards the Unix-only `import pwd` in `searx/valkeydb.py` and adds a
  `memory://` branch so the limiter uses in-memory `fakeredis`. String-matched and idempotent.
- `settings_windows.yml` — `use_default_settings: true` + JSON format on, `bind_address 127.0.0.1`,
  `port 8888`, **Mode B** limiter (`limiter: true`, `valkey.url: "memory://"`). Replace the secret_key
  (setup.ps1 does this; Tommy fills it on install if still the placeholder).
- `serve_waitress.py` — serves the WSGI app under Waitress; host/port from `SEARXNG_BIND_ADDRESS` /
  `SEARXNG_PORT`.
- `test_fakeredis_limiter.py` — proves `fakeredis` runs SearXNG's real limiter Lua scripts.
- `setup.ps1` — local build + smoke test (for verifying before you publish).

## Licensing

SearXNG is **AGPL-3.0**. Running it as a separate local process and calling its HTTP API from Tommy
does not make Tommy a derivative. **Distributing** a modified SearXNG (this patched build) carries
AGPL obligations — keep this build in its own public repository, and get a real legal opinion before
shipping it inside a closed product.
