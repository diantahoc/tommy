<#
.SYNOPSIS
    Prepares Tommy's bundled `integrated-tools` and copies them next to the built app.

.DESCRIPTION
    Tommy ships self-contained: a full Python (PyMuPDF for the PDF tool), the
    MCP servers (cloned from GitHub at pinned tags and built with npm),
    Playwright Chromium, and the helper programs (LibreOffice). None of these
    are committed to git; this script produces them, then copies the whole
    `integrated-tools` tree into the app's build/publish output so it sits beside
    the executable (where Tommy.Core.Runtime.IntegratedTools resolves it).

    Safe to re-run: each stage skips work that is already done. Use the -Skip*
    switches to iterate on one stage. Node, Python, and the MCP sources are all
    fetched from the network (nodejs.org / python-build-standalone / github.com)
    when not already bundled — you can delete the mcp/ folders to save space and
    the script will re-clone them (git required).

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
    [switch]$SkipNode,
    [string]$PythonVersion = '3.12',
    # Node feature line to download (e.g. '24' -> latest v24.x from nodejs.org).
    [string]$NodeVersion = '24'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$integrated = Join-Path $root 'integrated-tools'
$runtimes = Join-Path $integrated 'runtimes'
$mcpDir = Join-Path $integrated 'mcp'
$browsers = Join-Path $integrated 'browsers'

# The MCP servers are cloned from these pinned tags when not already present.
$mcpSources = @(
    @{ Name = 'mcp-server-mysql'; Repo = 'https://github.com/benborla/mcp-server-mysql'; Tag = 'v2.0.9' },
    @{ Name = 'web-search-mcp';   Repo = 'https://github.com/mrkrsl/web-search-mcp';     Tag = 'v0.3.2' }
)

# The bundled Node is the runtime for every npm/npx step — never the system one.
$npmCmd = $null
$npxCmd = $null

function Write-Stage([string]$msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# ---------------------------------------------------------------------------
# 0. Node (latest release of the requested line from nodejs.org)
# ---------------------------------------------------------------------------
function Install-Node {
    Write-Stage "Node (nodejs.org, v$NodeVersion line)"

    # A bundle for this platform counts; a foreign one (e.g. win-x64 on Linux) does not.
    $rid = if ($IsWindows) { 'win-x64' } else { 'linux-x64' }
    $nodeDir = Get-ChildItem -Path $runtimes -Directory -Filter "node-*$rid" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($nodeDir) {
        Write-Host "Node already present at $($nodeDir.FullName)"
    }
    else {
        Write-Host "Resolving latest v$NodeVersion.x release..."
        $ver = (Invoke-WebRequest -Headers @{ 'User-Agent' = 'tommy-build' } -UseBasicParsing `
                "https://nodejs.org/dist/latest-v$NodeVersion.x/").Content |
            Select-String "node-(v\d+\.\d+\.\d+)-$rid\.(tar\.gz|zip)" |
            ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1
        if (-not $ver) { throw "No v$NodeVersion.x line found on nodejs.org." }

        $target = Join-Path $runtimes "node-$ver-$rid"
        if ($IsWindows) {
            # Windows: zip; it extracts to a top-level `node-vX.Y.Z-win-x64/` folder.
            $archive = Join-Path $env:TEMP "node-$ver-$rid.zip"
            Write-Host "Downloading node-$ver-$rid.zip..."
            Invoke-WebRequest -Headers @{ 'User-Agent' = 'tommy-build' } -UseBasicParsing `
                -Uri "https://nodejs.org/dist/$ver/node-$ver-$rid.zip" -OutFile $archive
            Expand-Archive -Force $archive $runtimes
            Remove-Item $archive -Force
        }
        else {
            # Linux: tar.xz; extracts to a top-level `node-vX.Y.Z-linux-x64/` folder.
            $archive = Join-Path ([System.IO.Path]::GetTempPath()) "node-$ver-$rid.tar.xz"
            Write-Host "Downloading node-$ver-$rid.tar.xz..."
            Invoke-WebRequest -Headers @{ 'User-Agent' = 'tommy-build' } -UseBasicParsing `
                -Uri "https://nodejs.org/dist/$ver/node-$ver-$rid.tar.xz" -OutFile $archive
            tar -xJf $archive -C $runtimes
            Remove-Item $archive -Force
        }
        $nodeDir = Get-Item $target
    }

    $script:npmCmd = if ($IsWindows) { Join-Path $nodeDir.FullName 'npm.cmd' } else { Join-Path $nodeDir.FullName 'bin/npm' }
    $script:npxCmd = if ($IsWindows) { Join-Path $nodeDir.FullName 'npx.cmd' } else { Join-Path $nodeDir.FullName 'bin/npx' }
    $bin = if ($IsWindows) { $nodeDir.FullName } else { Join-Path $nodeDir.FullName 'bin' }
    $env:Path = "$bin;$env:Path"
}

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
# 2. MCP servers (clone from GitHub at pinned tags; build with npm)
# ---------------------------------------------------------------------------
function Get-McpDir([string]$name) {
    # Matches both the versioned folders (mcp-server-mysql-2.0.9) and a plain
    # clone (mcp-server-mysql) produced by this script.
    Get-ChildItem -Path $mcpDir -Directory |
        Where-Object { $_.Name -eq $name -or $_.Name -like "$name-*" } |
        Select-Object -First 1
}

function Ensure-McpSource([hashtable]$src) {
    # Any existing copy (versioned folder or plain clone) is reused as-is.
    $existing = Get-McpDir $src.Name
    if ($existing) { return $existing }

    $dir = Join-Path $mcpDir "$($src.Name)-$($src.Tag)"
    Write-Host "Cloning $($src.Name) $($src.Tag) from $($src.Repo)..."
    git clone --depth 1 --branch $src.Tag $src.Repo $dir
    if ($LASTEXITCODE -ne 0) { throw "git clone of $($src.Name) $($src.Tag) failed." }
    Remove-Item -Recurse -Force (Join-Path $dir '.git')
    return (Get-Item $dir)
}

function Build-Mcps {
    Write-Stage 'MCP servers'
    if (-not $npmCmd) { throw 'Node not set up: run without -SkipNode, or extract a node bundle into integrated-tools/runtimes first.' }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git is required to clone missing MCP sources; install git or keep the mcp/ folders in place.' }
    New-Item -ItemType Directory -Force $mcpDir | Out-Null

    $mysql = Ensure-McpSource $mcpSources[0]
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

    $websearch = Ensure-McpSource $mcpSources[1]
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

# ---------------------------------------------------------------------------
# 3. Chromium for Playwright (pinned revision into integrated-tools/browsers)
# ---------------------------------------------------------------------------
function Install-Chromium {
    Write-Stage 'Playwright Chromium'
    if (-not $npxCmd) { throw 'Node not set up: run without -SkipNode, or extract a node bundle into integrated-tools/runtimes first.' }

    # Remove the hand-downloaded nightly — Playwright needs its own pinned build.
    $nightly = Join-Path $browsers 'chromium-nightly-win'
    if (Test-Path $nightly) { Write-Host 'Removing hand-downloaded nightly Chromium'; Remove-Item -Recurse -Force $nightly }

    New-Item -ItemType Directory -Force $browsers | Out-Null
    $websearch = Get-McpDir 'web-search-mcp'
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

if (-not $SkipNode) { Install-Node }
if (-not $SkipPython) { Install-Python }
if (-not $SkipMcp) { Build-Mcps }
if (-not $SkipBrowser) { Install-Chromium }
if (-not $SkipCopy) { Copy-Output }

Write-Host "`nDone." -ForegroundColor Green
