#!/usr/bin/env bash
#
# build-and-copy.sh — Linux port of build-and-copy.ps1
#
# Prepares Tommy's bundled `integrated-tools` (full Python + PyMuPDF, the MCP
# servers cloned from GitHub at pinned tags and built with npm, Playwright
# Chromium) and copies the whole tree next to the built app, where
# Tommy.Core.Runtime.IntegratedTools resolves it.
#
# Safe to re-run: each stage skips work that is already done. Use the --skip-*
# flags to iterate on one stage.
#
# Usage:
#   ./build-and-copy.sh
#   ./build-and-copy.sh --configuration Release --publish
#   ./build-and-copy.sh --skip-python --skip-mcp    # only refresh browser + copy
#   ./build-and-copy.sh --node-version 22           # pick the Node line to download
#
# Node, Python, and the MCP sources are all fetched from the network
# (nodejs.org / python-build-standalone / github.com) when not already bundled —
# you can delete the mcp/ folders to save space and the script re-clones them
# (git required; the script exits with an error if git is missing).
#
# Differences from the PowerShell version (see NOTES at the bottom):
#   * python-build-standalone asset is x86_64-unknown-linux-gnu, not msvc
#   * Node is downloaded as the linux-x64 tar.xz from nodejs.org
#   * helpers/office-document-processor (Windows binary) is NOT produced by
#     this script and is copied as-is only if a linux build is placed in
#     integrated-tools/helpers manually

set -euo pipefail

# ---------------------------------------------------------------------------
# Arguments (mirror of the PS parameters)
# ---------------------------------------------------------------------------
CONFIGURATION='Debug'
PUBLISH=0
SKIP_NODE=0
SKIP_PYTHON=0
SKIP_MCP=0
SKIP_BROWSER=0
SKIP_COPY=0
PYTHON_VERSION='3.12'
NODE_VERSION='24'

usage() { sed -n '2,29p' "$0"; exit "${1:-0}"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --configuration) CONFIGURATION="$2"; shift 2 ;;
        --publish)       PUBLISH=1; shift ;;
        --skip-python)   SKIP_PYTHON=1; shift ;;
        --skip-mcp)      SKIP_MCP=1; shift ;;
        --skip-browser)  SKIP_BROWSER=1; shift ;;
        --skip-copy)     SKIP_COPY=1; shift ;;
        --python-version) PYTHON_VERSION="$2"; shift 2 ;;
        --node-version)   NODE_VERSION="$2"; shift 2 ;;
        --skip-node)      SKIP_NODE=1; shift ;;
        -h|--help)       usage 0 ;;
        *) echo "Unknown option: $1" >&2; usage 1 ;;
    esac
done

case "$CONFIGURATION" in Debug|Release) ;; *) echo "Configuration must be Debug or Release" >&2; exit 1 ;; esac

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INTEGRATED="$ROOT/integrated-tools"
RUNTIMES="$INTEGRATED/runtimes"
MCP_DIR="$INTEGRATED/mcp"
BROWSERS="$INTEGRATED/browsers"

# The MCP servers are cloned from these pinned tags when not already present.
MCP_SOURCES=(
    'mcp-server-mysql https://github.com/benborla/mcp-server-mysql v2.0.9'
    'web-search-mcp https://github.com/mrkrsl/web-search-mcp v0.3.2'
)

stage() { printf '\n\033[36m=== %s ===\033[0m\n' "$1"; }
warn()  { printf '\033[33mwarning: %s\033[0m\n' "$1" >&2; }
die()   { printf '\033[31merror: %s\033[0m\n' "$1" >&2; exit 1; }

# The bundled Node is the runtime for every npm/npx step — never the system one.
# install_node() (stage 0) fills these in; with --skip-node they must already exist.
NPM=""
NPX=""


# ---------------------------------------------------------------------------
# 0. Node (latest release of the requested line from nodejs.org)
# ---------------------------------------------------------------------------
install_node() {
    stage "Node (nodejs.org, v$NODE_VERSION line)"

    local rid='linux-x64'
    local node_dir=""
    while IFS= read -r d; do node_dir="$d"; break; done < <(
        find "$RUNTIMES" -maxdepth 1 -type d -name "node-*$rid" 2>/dev/null | sort
    )

    if [[ -n "$node_dir" ]]; then
        echo "Node already present at $node_dir"
    else
        echo "Resolving latest v$NODE_VERSION.x release..."
        local ver
        ver=$(curl -fsSL -H 'User-Agent: tommy-build' \
            "https://nodejs.org/dist/latest-v$NODE_VERSION.x/" \
            | grep -oE "node-v[0-9]+\.[0-9]+\.[0-9]+-$rid\.tar\.xz" \
            | head -n1 | sed -E 's/^node-(v[0-9.]+)-.*/\1/')
        [[ -n "$ver" ]] || die "No v$NODE_VERSION.x line found on nodejs.org."

        local archive="${TMPDIR:-/tmp}/node-$ver-$rid.tar.xz"
        echo "Downloading node-$ver-$rid.tar.xz..."
        curl -fL -H 'User-Agent: tommy-build' -o "$archive" \
            "https://nodejs.org/dist/$ver/node-$ver-$rid.tar.xz"
        # The tarball extracts to a top-level `node-vX.Y.Z-linux-x64/` folder.
        tar -xJf "$archive" -C "$RUNTIMES"
        rm -f "$archive"
        node_dir="$RUNTIMES/node-$ver-$rid"
    fi

    NPM="$node_dir/bin/npm"
    NPX="$node_dir/bin/npx"
    export PATH="$node_dir/bin:$PATH"
}

require_node() {
    [[ -n "$NPM" ]] || die 'Node not set up: run without --skip-node, or extract a node-*-linux-x64 bundle into integrated-tools/runtimes first.'
}

# ---------------------------------------------------------------------------
# 1. Full Python + PyMuPDF (replaces the embeddable distros, which can't venv)
# ---------------------------------------------------------------------------
install_python() {
    stage 'Python (full CPython via python-build-standalone)'

    # Drop the embeddable distros — they have no pip/venv and can't host PyMuPDF.
    while IFS= read -r d; do
        echo "Removing embeddable Python $(basename "$d")"
        rm -rf "$d"
    done < <(find "$RUNTIMES" -maxdepth 1 -type d -name 'python-*embed*' 2>/dev/null)

    local target="$RUNTIMES/python-$PYTHON_VERSION"
    local python_exe="$target/bin/python3"   # linux layout: bin/, not python/

    if [[ -x "$python_exe" ]]; then
        echo "Python already present at $python_exe"
    else
        echo 'Resolving latest python-build-standalone release...'
        local asset url name
        asset=$(curl -fsSL -H 'User-Agent: tommy-build' \
            'https://api.github.com/repos/astral-sh/python-build-standalone/releases/latest' \
            | grep -oE '"name": *"cpython-'"$PYTHON_VERSION"'\.[0-9]+\+[0-9]+.*x86_64-unknown-linux-gnu-install_only\.tar\.gz"' \
            | head -n1 | sed 's/.*"name": *"\(.*\)"/\1/')
        [[ -n "$asset" ]] || die "No install_only Linux asset for CPython $PYTHON_VERSION in the latest release."

        url=$(curl -fsSL -H 'User-Agent: tommy-build' \
            'https://api.github.com/repos/astral-sh/python-build-standalone/releases/latest' \
            | grep -oE '"browser_download_url": *"[^"]*'"$asset"'"' \
            | head -n1 | sed 's/.*"\(https[^"]*\)".*/\1/')

        local archive="${TMPDIR:-/tmp}/$asset"
        echo "Downloading $asset..."
        curl -fL -H 'User-Agent: tommy-build' -o "$archive" "$url"
        mkdir -p "$target"
        # The install_only archive extracts to a top-level `python/` folder.
        tar -xzf "$archive" -C "$target" --strip-components=1
        rm -f "$archive"
    fi

    echo 'Installing PyMuPDF...'
    "$python_exe" -m pip install --upgrade pip
    "$python_exe" -m pip install PyMuPDF
}

# ---------------------------------------------------------------------------
# 2. MCP servers (clone from GitHub at pinned tags; build with npm)
# ---------------------------------------------------------------------------
find_mcp_dir() {
    # Matches both the versioned folders (mcp-server-mysql-2.0.9) and a plain
    # clone (mcp-server-mysql) produced by this script.
    find "$MCP_DIR" -maxdepth 1 -type d \( -name "$1" -o -name "$1-*" \) 2>/dev/null | sort | head -n1
}

ensure_mcp_source() {
    local name="$1" repo="$2" tag="$3"
    # Any existing copy (versioned folder or plain clone) is reused as-is.
    local existing
    existing=$(find_mcp_dir "$name")
    if [[ -n "$existing" ]]; then echo "$existing"; return; fi

    local dir="$MCP_DIR/$name-$tag"
    echo "Cloning $name $tag from $repo..." >&2
    git clone --depth 1 --branch "$tag" "$repo" "$dir" \
        || die "git clone of $name $tag failed."
    rm -rf "$dir/.git"
    echo "$dir"
}

build_mcp() {
    stage 'MCP servers'
    require_node
    command -v git >/dev/null 2>&1 || die 'git is required to clone missing MCP sources; install git or keep the mcp/ folders in place.'
    mkdir -p "$MCP_DIR"

    local mysql
    mysql=$(ensure_mcp_source ${MCP_SOURCES[0]})
    if [[ -f "$mysql/dist/index.js" ]]; then
        echo "mcp-server-mysql already built."
    else
        echo "Building $(basename "$mysql")..."
        ( cd "$mysql" && "$NPM" install && "$NPM" run build )
    fi

    local websearch
    websearch=$(ensure_mcp_source ${MCP_SOURCES[1]})
    if [[ ! -d "$websearch/node_modules" ]]; then
        echo "Installing $(basename "$websearch") deps..."
        ( cd "$websearch" && "$NPM" install )
    fi
    if [[ ! -f "$websearch/dist/index.js" ]]; then
        ( cd "$websearch" && "$NPM" run build )
    fi
}

# ---------------------------------------------------------------------------
# 3. Chromium for Playwright (pinned revision into integrated-tools/browsers)
# ---------------------------------------------------------------------------
install_chromium() {
    stage 'Playwright Chromium'
    require_node

    # Remove the hand-downloaded nightlies — Playwright needs its own pinned build.
    for nightly in "$BROWSERS"/chromium-nightly-win "$BROWSERS"/chromium-nightly-linux; do
        if [[ -d "$nightly" ]]; then
            echo "Removing hand-downloaded nightly Chromium ($(basename "$nightly"))"
            rm -rf "$nightly"
        fi
    done

    mkdir -p "$BROWSERS"
    local websearch
    websearch=$(find_mcp_dir 'web-search-mcp')
    if [[ -z "$websearch" ]]; then
        warn 'web-search-mcp not found; skipping Chromium install.'
        return
    fi

    # Install into the bundle so it travels with the app, not the user profile.
    ( cd "$websearch" && PLAYWRIGHT_BROWSERS_PATH="$BROWSERS" "$NPX" --yes playwright install chromium )
}

# ---------------------------------------------------------------------------
# 4. Copy integrated-tools next to the built app
# ---------------------------------------------------------------------------
copy_beside() {
    local outDir="$1"
    if [[ ! -d "$outDir" ]]; then
        warn "Output not found: $outDir (build the app first)."
        return
    fi
    local dest="$outDir/integrated-tools"
    echo "Copying integrated-tools -> $dest"
    rm -rf "$dest"
    cp -a "$INTEGRATED" "$dest"
}

copy_output() {
    stage 'Copy beside app output'

    # Flag Windows-only payloads that won't run on Linux, but copy them anyway
    # so the tree stays intact (same as the PS script).
    while IFS= read -r d; do
        warn "Windows-only helper present: $(basename "$d") — needs a linux build to work here."
    done < <(find "$INTEGRATED/helpers" -maxdepth 1 -type d -name '*-win-*' 2>/dev/null)

    copy_beside "$ROOT/src/Tommy.App/bin/$CONFIGURATION/net9.0"

    if [[ "$PUBLISH" -eq 1 ]]; then
        local pub="$ROOT/src/Tommy.App/bin/$CONFIGURATION/net9.0/publish"
        echo 'Publishing app...'
        dotnet publish "$ROOT/src/Tommy.App/Tommy.App.csproj" -c "$CONFIGURATION" -o "$pub"
        copy_beside "$pub"
    fi
}

# ---------------------------------------------------------------------------
# Stages (same skip flags as the PS script)
# ---------------------------------------------------------------------------
[[ "$SKIP_NODE"   -eq 0 ]] && install_node
[[ "$SKIP_PYTHON" -eq 0 ]] && install_python
[[ "$SKIP_MCP"    -eq 0 ]] && build_mcp
[[ "$SKIP_BROWSER" -eq 0 ]] && install_chromium
[[ "$SKIP_COPY"   -eq 0 ]] && copy_output

printf '\n\033[32mDone.\033[0m\n'

# NOTES — components the PS script could not carry over:
#  * helpers/office-document-processor-win-x86_64-avx2-1.0.0 (LibreOffice-based
#    office processor): a Windows binary. The build script never built it; it is
#    only copied. On Linux it is inert — a linux-x86_64 build must be obtained
#    separately and placed in integrated-tools/helpers. The copy stage warns
#    about it.
#  * runtimes/node-v24.22.0-win-x64: Windows-only. The Node stage now
#    downloads the matching linux-x64 tarball from nodejs.org automatically
#    (--node-version picks the line); the script still refuses system Node.
#  * The Python and Chromium installers here fetch linux assets automatically,
#    so those stages are fully portable.
#  * mcp/: both servers are now cloned from GitHub at the pinned tags when
#    missing (shallow clone, .git removed to save space), so the folder can be
#    deleted to save disk; the script exits with an error if git is unavailable.
