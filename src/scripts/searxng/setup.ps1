# Bootstrap a native-Windows SearXNG in a plain venv for LOCAL TESTING. No WSL/Docker/Redis.
# Run from this folder. Requires: Python 3.11+ and git.
#
# This is for verifying the build works before you publish a release. The release ZIP that
# Tommy downloads is SOURCE ONLY (no .venv, no .git) — Tommy builds its own venv against the
# bundled Python. See README.md for how to produce the release archive.

$ErrorActionPreference = "Stop"
$KitDir = $PSScriptRoot

# 1. Get the source (skip if you already have it).
if (-not (Test-Path ".\searxng")) {
    git clone --depth 1 https://github.com/searxng/searxng.git
}
Set-Location .\searxng

# 2. Create and populate the venv (all deps ship Windows wheels).
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install --upgrade pip
.\.venv\Scripts\pip.exe install -r requirements.txt
.\.venv\Scripts\pip.exe install waitress
# fakeredis[lua] powers the in-memory rate limiter (Mode B).
.\.venv\Scripts\pip.exe install "fakeredis[lua]"

# 3. Apply the Windows patch (guards `pwd`; adds the memory:// branch).
.\.venv\Scripts\python.exe "$KitDir\apply_windows_patch.py" ".\searx\valkeydb.py"

# 4. Drop in settings + launcher, with a real secret_key.
Copy-Item "$KitDir\settings_windows.yml" .\settings_windows.yml -Force
Copy-Item "$KitDir\serve_waitress.py"    .\serve_waitress.py    -Force
Copy-Item "$KitDir\apply_windows_patch.py" .\apply_windows_patch.py -Force
Copy-Item "$KitDir\test_fakeredis_limiter.py" .\test_fakeredis_limiter.py -Force
$secret = .\.venv\Scripts\python.exe -c "import secrets; print(secrets.token_hex(32))"
(Get-Content .\settings_windows.yml) `
    -replace 'CHANGE_ME_run_the_token_hex_command', $secret |
    Set-Content .\settings_windows.yml

# 5. Smoke test: confirm the app imports on Windows (the real proof).
.\.venv\Scripts\python.exe -c "import os; os.environ['SEARXNG_SETTINGS_PATH']=r'.\settings_windows.yml'; import searx.webapp; print('OK: searx.webapp imported on Windows')"

Write-Host ""
Write-Host "Done. Start the server with:" -ForegroundColor Green
Write-Host "    .\searxng\.venv\Scripts\python.exe .\searxng\serve_waitress.py"
Write-Host "Then: http://127.0.0.1:8888/search?q=test&format=json"
