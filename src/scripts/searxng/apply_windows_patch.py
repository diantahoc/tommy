"""Make SearXNG importable on Windows (and optionally enable an in-memory store).

The entire searx/ package has a single Windows-hostile code path: a top-level
`import pwd` in searx/valkeydb.py (pwd is Unix-only) and a
`pwd.getpwuid(os.getuid())` call in its Valkey-error branch. This script guards
both. It also (optionally) adds a `memory://` branch to initialize() so the rate
limiter can run against a pure-Python in-memory fakeredis store with no server.

Usage:
    python apply_windows_patch.py                 # auto-locate installed searx
    python apply_windows_patch.py path\\to\\valkeydb.py
"""

import sys

MARKER = "[win-patch]"

IMPORT_OLD = """import os
import pwd
import logging
import warnings"""

IMPORT_NEW = """import os
import logging
import warnings

try:
    import pwd  # {marker} — Unix-only module, absent on Windows
except ImportError:
    pwd = None""".format(marker=MARKER)

CALL_OLD = """        _CLIENT = None
        _pw = pwd.getpwuid(os.getuid())
        logger.exception("[%s (%s)] can't connect valkey DB ...", _pw.pw_name, _pw.pw_uid)"""

CALL_NEW = """        _CLIENT = None
        if pwd is not None:  # {marker}
            _pw = pwd.getpwuid(os.getuid())
            logger.exception("[%s (%s)] can't connect valkey DB ...", _pw.pw_name, _pw.pw_uid)
        else:
            logger.exception("can't connect valkey DB ...")""".format(marker=MARKER)

MEM_MARKER = "[win-patch:memory]"

MEM_OLD = """    valkey_url = get_setting('valkey.url') or get_setting('redis.url')
    if not valkey_url:
        return False
    try:"""

MEM_NEW = """    valkey_url = get_setting('valkey.url') or get_setting('redis.url')
    if not valkey_url:
        return False
    if valkey_url in ("memory://", "fake://", "memory://fake"):  # {marker}
        import fakeredis  # pure-Python in-memory Valkey; pip install fakeredis[lua]
        _CLIENT = fakeredis.FakeStrictRedis()
        logger.info("using in-memory fakeredis store (single-process; no external server)")
        return True
    try:""".format(marker=MEM_MARKER)


def locate() -> str:
    import importlib.util

    spec = importlib.util.find_spec("searx.valkeydb")
    if spec is None or not spec.origin:
        sys.exit("Could not find searx.valkeydb on the path. Activate the venv, "
                 "or pass the path to valkeydb.py as an argument.")
    return spec.origin


def main() -> None:
    path = sys.argv[1] if len(sys.argv) > 1 else locate()
    with open(path, "r", encoding="utf-8") as fh:
        src = fh.read()

    changed = False

    # Patch 1 (required): make the module importable on Windows.
    if MARKER in src:
        print("pwd guard: already applied")
    elif IMPORT_OLD in src and CALL_OLD in src:
        src = src.replace(IMPORT_OLD, IMPORT_NEW, 1).replace(CALL_OLD, CALL_NEW, 1)
        changed = True
        print("pwd guard: applied")
    else:
        sys.exit("Unexpected valkeydb.py contents; upstream may have changed. "
                 "Patch by hand (guard `import pwd` and the getpwuid call).")

    # Patch 2 (optional): allow an in-memory fakeredis store via a memory:// url,
    # which turns the rate limiter on with no external server. Inert unless the
    # configured valkey.url is memory:// (and fakeredis is installed).
    if MEM_MARKER in src:
        print("memory:// store: already applied")
    elif MEM_OLD in src:
        src = src.replace(MEM_OLD, MEM_NEW, 1)
        changed = True
        print("memory:// store: applied")
    else:
        print("memory:// store: skipped (initialize() shape changed; optional)")

    if changed:
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(src)
    print("Done:", path)


if __name__ == "__main__":
    main()
