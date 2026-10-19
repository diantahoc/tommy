<#
.SYNOPSIS
    Prepares Tommy's bundled `integrated-tools` and copies them next to the built app.

.DESCRIPTION
    Tommy ships self-contained: a full Python (PyMuPDF for the PDF tool), the
    preshipped MCP servers (MariaDB/MySQL built from source, web-search built +
    Playwright Chromium), and the helper programs (LibreOffice). None of these
    are committed to git; this script produces them, then copies the whole
    `integrated-tools` tree into the app's build/publish output so it sits beside
    the executable (where Tommy.Core.Runtime.IntegratedTools resolves it).

    Safe to re-run: each stage skips work that is already done. Use the -Skip*
    switches to iterate on one stage.

.PARAMETER Configuration
    Debug (default) or Release — which bin/ output to copy into.

.PARAMETER Publish
    Also run `dotnet publish` and copy integrated-tools into the publish folder.

.EXAMPLE
    ./build-and-copy.ps1
    ./build-and-copy.ps1 -Configuration Release -Publish
    ./build-and-copy.ps1 -SkipPython -SkipMcp   # only refresh the browser + copy
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Publish,
    [switch]$SkipPython,
    [switch]$SkipMcp,
    [switch]$SkipBrowser,
    [switch]$SkipCopy,
    [string]$PythonVersion = '3.12'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$integrated = Join-Path $root 'integrated-tools'
$runtimes = Join-Path $integrated 'runtimes'
$mcpDir = Join-Path $integrated 'mcp'
$browsers = Join-Path $integrated 'browsers'

# The bundled Node is the runtime for every npm/npx step — never the system one.
$nodeDir = Get-ChildItem -Path $runtimes -Directory -Filter 'node-*' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $nodeDir) { throw "Bundled Node not found under $runtimes (expected a node-* folder)." }
$npmCmd = Join-Path $nodeDir.FullName 'npm.cmd'
$npxCmd = Join-Path $nodeDir.FullName 'npx.cmd'
$env:Path = "$($nodeDir.FullName);$env:Path"

function Write-Stage([string]$msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# ---------------------------------------------------------------------------
# 1. Full Python + PyMuPDF (replaces the embeddable distros, which can't venv)
# ---------------------------------------------------------------------------
function Install-Python {
    Write-Stage 'Python (full CPython via python-build-standalone)'

    # Drop the embeddable distros — they have no pip/venv and can't host PyMuPDF.
    Get-ChildItem -Path $runtimes -Directory -Filter 'python-*embed*' -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "Removing embeddable Python $($_.Name)"; Remove-Item -Recurse -Force $_.FullName }

    $target = Join-Path $runtimes "python-$PythonVersion"
    $pythonExe = Join-Path $target 'python/python.exe'
    if (Test-Path $pythonExe) {
        Write-Host "Python already present at $pythonExe"
    }
    else {
        Write-Host 'Resolving latest python-build-standalone release...'
        $headers = @{ 'User-Agent' = 'tommy-build' }
        $release = Invoke-RestMethod -Headers $headers 'https://api.github.com/repos/astral-sh/python-build-standalone/releases/latest'
        $asset = $release.assets |
            Where-Object { $_.name -match "cpython-$([regex]::Escape($PythonVersion))\.\d+\+.*x86_64-pc-windows-msvc-install_only\.tar\.gz$" } |
            Select-Object -First 1
        if (-not $asset) { throw "No install_only Windows asset for CPython $PythonVersion in the latest release." }

        $archive = Join-Path $env:TEMP $asset.name
        Write-Host "Downloading $($asset.name)..."
        Invoke-WebRequest -Headers $headers -Uri $asset.browser_download_url -OutFile $archive
        New-Item -ItemType Directory -Force $target | Out-Null
        # The install_only archive extracts to a top-level `python/` folder.
        tar -xzf $archive -C $target
        Remove-Item $archive -Force
    }

    Write-Host 'Installing PyMuPDF...'
    & $pythonExe -m pip install --upgrade pip
    & $pythonExe -m pip install PyMuPDF
    if ($LASTEXITCODE -ne 0) { throw 'pip install PyMuPDF failed.' }
}

# ---------------------------------------------------------------------------
# 2. MCP servers (build MySQL from source; ensure web-search deps)
# ---------------------------------------------------------------------------
function Build-Mcps {
    Write-Stage 'MCP servers'

    $mysql = Get-ChildItem -Path $mcpDir -Directory -Filter 'mcp-server-mysql-*' | Select-Object -First 1
    if ($mysql) {
        if (Test-Path (Join-Path $mysql.FullName 'dist/index.js')) {
            Write-Host "mcp-server-mysql already built."
        }
        else {
            Write-Host "Building $($mysql.Name)..."
            Push-Location $mysql.FullName
            try {
                & $npmCmd install
                & $npmCmd run build
                if ($LASTEXITCODE -ne 0) { throw 'mcp-server-mysql build failed.' }
            }
            finally { Pop-Location }
        }
    }

    $websearch = Get-ChildItem -Path $mcpDir -Directory -Filter 'web-search-mcp-*' | Select-Object -First 1
    if ($websearch) {
        if (-not (Test-Path (Join-Path $websearch.FullName 'node_modules'))) {
            Write-Host "Installing $($websearch.Name) deps..."
            Push-Location $websearch.FullName
            try { & $npmCmd install } finally { Pop-Location }
        }
        if (-not (Test-Path (Join-Path $websearch.FullName 'dist/index.js'))) {
            Push-Location $websearch.FullName
            try { & $npmCmd run build } finally { Pop-Location }
        }
    }
}

# ---------------------------------------------------------------------------
# 3. Chromium for Playwright (pinned revision into integrated-tools/browsers)
# ---------------------------------------------------------------------------
function Install-Chromium {
    Write-Stage 'Playwright Chromium'

    # Remove the hand-downloaded nightly — Playwright needs its own pinned build.
    $nightly = Join-Path $browsers 'chromium-nightly-win'
    if (Test-Path $nightly) { Write-Host 'Removing hand-downloaded nightly Chromium'; Remove-Item -Recurse -Force $nightly }

    New-Item -ItemType Directory -Force $browsers | Out-Null
    $websearch = Get-ChildItem -Path $mcpDir -Directory -Filter 'web-search-mcp-*' | Select-Object -First 1
    if (-not $websearch) { Write-Warning 'web-search-mcp not found; skipping Chromium install.'; return }

    # Install into the bundle so it travels with the app, not the user profile.
    $env:PLAYWRIGHT_BROWSERS_PATH = $browsers
    Push-Location $websearch.FullName
    try {
        & $npxCmd --yes playwright install chromium
        if ($LASTEXITCODE -ne 0) { throw 'playwright install chromium failed.' }
    }
    finally { Pop-Location }
}

# ---------------------------------------------------------------------------
# 4. Copy integrated-tools next to the built app
# ---------------------------------------------------------------------------
function Copy-Beside([string]$outDir) {
    if (-not (Test-Path $outDir)) { Write-Warning "Output not found: $outDir (build the app first)."; return }
    $dest = Join-Path $outDir 'integrated-tools'
    Write-Host "Copying integrated-tools -> $dest"
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    Copy-Item -Recurse -Force $integrated $dest
}

function Copy-Output {
    Write-Stage 'Copy beside app output'
    $app = Join-Path $root "src/Tommy.App/bin/$Configuration/net9.0"
    Copy-Beside $app

    if ($Publish) {
        $pub = Join-Path $root "src/Tommy.App/bin/$Configuration/net9.0/publish"
        Write-Host 'Publishing app...'
        dotnet publish (Join-Path $root 'src/Tommy.App/Tommy.App.csproj') -c $Configuration -o $pub | Out-Host
        Copy-Beside $pub
    }
}

if (-not $SkipPython) { Install-Python }
if (-not $SkipMcp) { Build-Mcps }
if (-not $SkipBrowser) { Install-Chromium }
if (-not $SkipCopy) { Copy-Output }

Write-Host "`nDone." -ForegroundColor Green
