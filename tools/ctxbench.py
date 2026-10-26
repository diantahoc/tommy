#!/usr/bin/env python3
"""Sweep Strata generation speed vs context depth.

Sends chat completions with growing prompts and reads the engine's own `timings`
(prefill prompt_per_second, decode predicted_per_second) at each depth. Each prompt
is unique (a nonce prefix) so the prefix cache doesn't skip prefill.
"""
import json, os, sys, time, urllib.request

# Point at your Strata endpoint; the API key comes from the environment so none is
# committed:  STRATA_URL, STRATA_API_KEY, STRATA_MODEL.
URL = os.environ.get("STRATA_URL", "http://127.0.0.1:8080/v1/chat/completions")
KEY = os.environ.get("STRATA_API_KEY", "")
MODEL = os.environ.get("STRATA_MODEL", "qwen3.8-flash-next-iq3_s")

# word counts -> roughly these map to ~1.3x tokens; actual prompt_n is reported.
SIZES = [int(x) for x in sys.argv[1].split(",")] if len(sys.argv) > 1 else [500, 4000, 16000, 48000, 96000, 150000]
GEN = int(sys.argv[2]) if len(sys.argv) > 2 else 64

# A varied base passage (not one repeated token) so prefill isn't pathologically easy.
BASE = ("The quick brown fox jumps over the lazy dog while nineteen clever engineers "
        "measure latency throughput and memory across a growing context window today. ").split()

def make_prompt(words, nonce):
    out = [f"session-{nonce}"]
    i = 0
    while len(out) < words:
        out.append(BASE[i % len(BASE)])
        i += 1
    body = " ".join(out)
    return body + "\n\nIn one short sentence, how many words did I just send (approximately)?"

def run(words, nonce):
    prompt = make_prompt(words, nonce)
    payload = json.dumps({
        "model": MODEL,
        "messages": [{"role": "user", "content": prompt}],
        "max_tokens": GEN,
        "temperature": 0.0,
        "stream": False,
    }).encode()
    req = urllib.request.Request(URL, data=payload, headers={
        "Authorization": f"Bearer {KEY}", "Content-Type": "application/json"})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=1200) as r:
        d = json.load(r)
    wall = time.time() - t0
    t = d.get("timings", {})
    u = d.get("usage", {})
    return {
        "prompt_tok": u.get("prompt_tokens"),
        "cache_n": t.get("cache_n"),
        "prefill_tps": t.get("prompt_per_second"),
        "prefill_ms": t.get("prompt_ms"),
        "gen_tok": t.get("predicted_n"),
        "decode_tps": t.get("predicted_per_second"),
        "draft_n": t.get("draft_n"),
        "draft_acc": t.get("draft_n_accepted"),
        "wall_s": round(wall, 1),
    }

print(f"{'prompt_tok':>11} {'prefill t/s':>12} {'prefill_s':>10} {'decode t/s':>11} "
      f"{'gen':>4} {'spec_acc':>9} {'wall_s':>7}")
for n in SIZES:
    try:
        r = run(n, nonce=int(time.time() * 1000) % 100000)
    except Exception as e:
        print(f"{n:>11} ERROR {e}")
        continue
    acc = f"{r['draft_acc']}/{r['draft_n']}" if r['draft_n'] else "-"
    pf_s = round((r['prefill_ms'] or 0) / 1000, 1)
    print(f"{r['prompt_tok']:>11} {r['prefill_tps']:>12.1f} {pf_s:>10} "
          f"{r['decode_tps']:>11.1f} {r['gen_tok']:>4} {acc:>9} {r['wall_s']:>7}")
    sys.stdout.flush()
