"""Serve SearXNG on Windows with Waitress (pure-Python WSGI server).

No uWSGI, no WSL, no Docker, no Redis/Valkey. Run from the SearXNG source tree
(the folder containing the `searx` package) inside the activated venv:

    python serve_waitress.py

Then Tommy (or any client) can call:
    http://127.0.0.1:8888/search?q=test&format=json
"""

import os

# Must be set BEFORE importing searx.webapp — importing it runs init(), which
# reads the settings file once at import time.
HERE = os.path.dirname(os.path.abspath(__file__))
os.environ.setdefault("SEARXNG_SETTINGS_PATH", os.path.join(HERE, "settings_windows.yml"))

HOST = os.environ.get("SEARXNG_BIND_ADDRESS", "127.0.0.1")
PORT = int(os.environ.get("SEARXNG_PORT", "8888"))

from waitress import serve  # noqa: E402
from searx.webapp import application  # noqa: E402

if __name__ == "__main__":
    print(f"SearXNG (waitress) on http://{HOST}:{PORT}  —  JSON: /search?q=test&format=json")
    # threads: how many concurrent searches the server will handle.
    serve(application, host=HOST, port=PORT, threads=16, channel_timeout=30)
