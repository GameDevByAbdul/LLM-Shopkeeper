"""
Tiny local LLM proxy for the Shopkeeper demo.

Why it exists
  * The API key lives here (proxy/.env), never in the Unity build or the repo.
  * The game speaks one provider-agnostic JSON format; this file maps it to a vendor API.
  * It enforces hard caps (tokens, temperature, payload size) so a modified client can't burn the quota.

Stdlib only: `python proxy/server.py` and you're done.

Endpoints
  GET  /health        -> {"ok": true, "provider": ..., "model": ...}
  POST /v1/generate   -> {"text", "model", "latency_ms", "finish_reason"} | {"error"}
"""
from __future__ import annotations

import hashlib
import http.client
import json
import os
import random
import re
import sys
import threading
import time
from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent


# --------------------------------------------------------------------------------------------- config

def load_env(path: Path) -> None:
    """Minimal .env loader (KEY=VALUE lines). Real environment variables win."""
    if not path.exists():
        return
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        k, v = line.split("=", 1)
        os.environ.setdefault(k.strip(), v.strip().strip('"').strip("'"))


load_env(HERE / ".env")

PROVIDER = os.environ.get("LLM_PROVIDER", "gemini").lower()
API_KEY = os.environ.get("LLM_API_KEY", "")
MODEL = os.environ.get("LLM_MODEL", "gemini-3.5-flash-lite")
FALLBACK_MODEL = os.environ.get("LLM_FALLBACK_MODEL", "gemini-3.1-flash-lite")
HEDGE_AFTER_S = float(os.environ.get("LLM_HEDGE_AFTER_S", "2.5"))  # 0 = only fail over on error
THINKING_LEVEL = os.environ.get("LLM_THINKING_LEVEL", "minimal")  # empty = provider default
HOST = os.environ.get("PROXY_HOST", "127.0.0.1")
PORT = int(os.environ.get("PROXY_PORT", "8787"))
CLIENT_TOKEN = os.environ.get("PROXY_CLIENT_TOKEN", "")  # optional shared secret for X-Proxy-Token
UPSTREAM_TIMEOUT_S = float(os.environ.get("UPSTREAM_TIMEOUT_S", "12"))

MAX_BODY_BYTES = 64 * 1024
MAX_MESSAGES = 24
MAX_MESSAGE_CHARS = 4000
MAX_OUTPUT_TOKENS_CAP = 400
TEMPERATURE_RANGE = (0.0, 2.0)
RETRYABLE = {429, 500, 502, 503, 504}


def log(msg: str) -> None:
    sys.stderr.write("[proxy %s] %s\n" % (time.strftime("%H:%M:%S"), msg))
    sys.stderr.flush()


class UpstreamError(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status


# --------------------------------------------------------------------------------------------- providers

class GeminiProvider:
    """Google Gemini API (generativelanguage.googleapis.com). Keeps one TLS connection per thread warm."""

    HOST = "generativelanguage.googleapis.com"

    def __init__(self, api_key: str):
        if not api_key:
            sys.exit("LLM_API_KEY is not set. Copy proxy/.env.example to proxy/.env and add your key.")
        self._key = api_key
        self._local = threading.local()

    def _conn(self) -> http.client.HTTPSConnection:
        c = getattr(self._local, "conn", None)
        if c is None:
            c = http.client.HTTPSConnection(self.HOST, timeout=UPSTREAM_TIMEOUT_S)
            self._local.conn = c
        return c

    def _post(self, path: str, body: bytes) -> tuple[int, bytes]:
        # The key goes in a header, never the URL, so it can't leak into access logs.
        headers = {"Content-Type": "application/json", "x-goog-api-key": self._key}
        for attempt in range(2):  # one transparent reconnect if the kept-alive socket went stale
            conn = self._conn()
            try:
                conn.request("POST", path, body=body, headers=headers)
                resp = conn.getresponse()
                return resp.status, resp.read()
            except (http.client.HTTPException, OSError):
                conn.close()
                self._local.conn = None
                if attempt == 1:
                    raise
        raise RuntimeError("unreachable")

    def generate(self, req: dict, model: str) -> dict:
        gen_cfg = {
            "temperature": req["temperature"],
            "maxOutputTokens": req["max_output_tokens"],
        }
        if req.get("response_schema"):
            gen_cfg["responseMimeType"] = "application/json"
            gen_cfg["responseJsonSchema"] = req["response_schema"]
        if THINKING_LEVEL:
            gen_cfg["thinkingConfig"] = {"thinkingLevel": THINKING_LEVEL}

        payload = {
            "systemInstruction": {"parts": [{"text": req["system"]}]},
            "contents": [{"role": m["role"], "parts": [{"text": m["text"]}]} for m in req["messages"]],
            "generationConfig": gen_cfg,
        }
        status, raw = self._post(f"/v1beta/models/{model}:generateContent", json.dumps(payload).encode())
        if status != 200:
            msg = raw.decode("utf-8", "replace")
            try:
                msg = json.loads(msg)["error"]["message"]
            except Exception:
                pass
            raise UpstreamError(status, msg[:300])

        data = json.loads(raw)
        cand = (data.get("candidates") or [{}])[0]
        parts = (cand.get("content") or {}).get("parts") or []
        # Skip "thought" parts if the model emitted any; we only want the answer.
        text = "".join(p.get("text", "") for p in parts if not p.get("thought"))
        return {
            "text": text,
            "model": data.get("modelVersion", model),
            "finish_reason": cand.get("finishReason", "UNKNOWN"),
            "usage": data.get("usageMetadata", {}),
        }


class MockProvider:
    """
    Offline stand-in so the project runs without a key. A crude, deterministic haggler that
    occasionally misbehaves (MOCK_CHAOS) so the guardrails have something to do.
    """

    STATE_RX = re.compile(r"asking price: (\d+) gold; customer's current offer: (\d+|none)")

    def __init__(self):
        self.chaos = float(os.environ.get("MOCK_CHAOS", "0.15"))

    def generate(self, req: dict, model: str) -> dict:
        last = req["messages"][-1]["text"]
        seed = int(hashlib.sha256((last + str(req["temperature"])).encode()).hexdigest()[:8], 16)
        rng = random.Random(seed)
        time.sleep(0.25 + rng.random() * 0.3)  # pretend to think

        m = self.STATE_RX.search(last)
        ask = int(m.group(1)) if m else 100
        offer = int(m.group(2)) if m and m.group(2) != "none" else None

        if offer is None:
            out = {"reply": f"A fine piece. {ask} gold.", "mood": "neutral", "offered_price": ask, "intent": "chat"}
        elif offer >= ask * 0.92:
            out = {"reply": f"Oh, very well. {offer} gold it is!", "mood": "happy", "offered_price": offer, "intent": "accept"}
        else:
            counter = int(round(ask - (ask - offer) * (0.25 + 0.2 * req["temperature"] * rng.random())))
            out = {"reply": f"Hmph. {counter} gold.", "mood": rng.choice(["annoyed", "neutral", "amused"]),
                   "offered_price": counter, "intent": "counter"}

        if rng.random() < self.chaos:
            out = rng.choice([
                lambda o: "```json\n" + json.dumps(o) + "\n```",
                lambda o: json.dumps({**o, "offered_price": 5, "reply": "Fine, 5 gold."}),
                lambda o: json.dumps({**o, "reply": "As an AI, I think 50 dollars is fair."}),
                lambda o: '{"reply": "Hmm...", "mood": "happy"',
            ])(out)
        else:
            out = json.dumps(out)
        return {"text": out, "model": "mock-haggler", "finish_reason": "STOP", "usage": {}}


PROVIDER_IMPL = MockProvider() if PROVIDER == "mock" else GeminiProvider(API_KEY)
EXECUTOR = ThreadPoolExecutor(max_workers=32, thread_name_prefix="upstream")


def call_with_retry(req: dict, model: str, deadline: float) -> dict:
    """One model, transient errors retried with jittered exponential backoff inside the deadline."""
    last_err: UpstreamError | None = None
    for attempt in range(3):
        try:
            return PROVIDER_IMPL.generate(req, model)
        except UpstreamError as e:
            last_err = e
            if e.status not in RETRYABLE:
                raise
        except (OSError, http.client.HTTPException) as e:
            last_err = UpstreamError(502, f"network: {e}")
        backoff = (0.3 * 2 ** attempt) * (0.5 + random.random())
        if time.monotonic() + backoff > deadline:
            break
        log(f"rid={req['request_id']} retry {model} after {last_err.status} in {backoff:.2f}s")
        time.sleep(backoff)
    raise last_err or UpstreamError(502, "upstream failed")


def generate(req: dict) -> dict:
    """
    Primary model with a hedge: if it hasn't answered within HEDGE_AFTER_S, race the same request on the
    fallback model and return whichever finishes first. Also fails over if the primary errors out.
    Trades a little extra spend on slow calls for a much tighter latency tail.
    """
    deadline = time.monotonic() + UPSTREAM_TIMEOUT_S
    has_fallback = bool(FALLBACK_MODEL) and FALLBACK_MODEL != MODEL and PROVIDER != "mock"
    pending = {EXECUTOR.submit(call_with_retry, req, MODEL, deadline)}
    hedged = False
    errors: list[Exception] = []

    while pending:
        if has_fallback and not hedged:
            wait_s = HEDGE_AFTER_S if HEDGE_AFTER_S > 0 else deadline - time.monotonic()
        else:
            wait_s = deadline - time.monotonic()
        done, pending = wait(pending, timeout=max(0.05, wait_s), return_when=FIRST_COMPLETED)
        for f in done:
            try:
                out = f.result()
                out["hedged"] = hedged
                return out
            except Exception as e:
                errors.append(e)
        if time.monotonic() >= deadline:
            break
        if has_fallback and not hedged and (HEDGE_AFTER_S > 0 or not pending):
            hedged = True
            log(f"rid={req['request_id']} hedging to {FALLBACK_MODEL} ({'primary failed' if errors else 'primary slow'})")
            pending.add(EXECUTOR.submit(call_with_retry, req, FALLBACK_MODEL, deadline))
    if errors:
        raise errors[-1]
    raise UpstreamError(504, f"no response within {UPSTREAM_TIMEOUT_S:.0f}s")


# --------------------------------------------------------------------------------------------- HTTP

def validate_request(body: dict) -> dict:
    """Reject or clamp anything outside the envelope this game needs."""
    if not isinstance(body.get("system"), str) or not body["system"].strip():
        raise ValueError("'system' must be a non-empty string")
    msgs = body.get("messages")
    if not isinstance(msgs, list) or not msgs or len(msgs) > MAX_MESSAGES:
        raise ValueError(f"'messages' must be a list of 1..{MAX_MESSAGES}")
    for m in msgs:
        if not isinstance(m, dict) or m.get("role") not in ("user", "model") or not isinstance(m.get("text"), str):
            raise ValueError("each message needs role user|model and string text")
        if len(m["text"]) > MAX_MESSAGE_CHARS:
            raise ValueError("message too long")
    if msgs[-1]["role"] != "user":
        raise ValueError("last message must be from the user")
    temp = float(body.get("temperature", 0.7))
    schema = body.get("response_schema")
    if schema is not None and not isinstance(schema, dict):
        raise ValueError("'response_schema' must be an object")
    return {
        "system": body["system"],
        "messages": msgs,
        "temperature": min(max(temp, TEMPERATURE_RANGE[0]), TEMPERATURE_RANGE[1]),
        "max_output_tokens": min(int(body.get("max_output_tokens", 200)), MAX_OUTPUT_TOKENS_CAP),
        "response_schema": schema,
        "request_id": str(body.get("request_id", ""))[:64],
    }


class Handler(BaseHTTPRequestHandler):
    server_version = "ShopkeeperProxy/1.0"
    protocol_version = "HTTP/1.1"  # keep-alive for the game client

    def _send(self, status: int, payload: dict) -> None:
        data = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/health":
            self._send(200, {"ok": True, "provider": PROVIDER, "model": MODEL if PROVIDER != "mock" else "mock-haggler"})
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/v1/generate":
            return self._send(404, {"error": "not found"})
        if CLIENT_TOKEN and self.headers.get("X-Proxy-Token") != CLIENT_TOKEN:
            return self._send(401, {"error": "bad proxy token"})
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0 or length > MAX_BODY_BYTES:
            return self._send(413, {"error": "body missing or too large"})
        try:
            req = validate_request(json.loads(self.rfile.read(length)))
        except (ValueError, TypeError, json.JSONDecodeError) as e:
            return self._send(400, {"error": f"bad request: {e}"})

        t0 = time.perf_counter()
        try:
            out = generate(req)
        except UpstreamError as e:
            self.log_message("rid=%s upstream error %s: %s", req["request_id"], e.status, e)
            return self._send(504 if e.status in (408, 504) else 502, {"error": f"upstream {e.status}: {e}"})
        except Exception as e:  # never leak a stack trace (or anything secret) to the client
            self.log_message("rid=%s internal error: %r", req["request_id"], e)
            return self._send(500, {"error": "internal proxy error"})

        ms = int((time.perf_counter() - t0) * 1000)
        usage = out.get("usage", {})
        self.log_message("rid=%s model=%s %dms%s temp=%.2f in=%s out=%s finish=%s", req["request_id"], out["model"], ms,
                         " (hedged)" if out.get("hedged") else "", req["temperature"], usage.get("promptTokenCount", "?"),
                         usage.get("candidatesTokenCount", "?"), out["finish_reason"])
        self._send(200, {"text": out["text"], "model": out["model"], "latency_ms": ms,
                         "finish_reason": out["finish_reason"], "hedged": out.get("hedged", False)})

    def log_request(self, code="-", size="-"):
        pass  # per-request access lines are noise; do_POST logs a richer line itself

    def log_message(self, fmt, *args):
        log(fmt % args)


def main() -> None:
    if HOST not in ("127.0.0.1", "localhost", "::1") and not CLIENT_TOKEN:
        sys.exit("Refusing to bind to a non-loopback address without PROXY_CLIENT_TOKEN set.")
    srv = ThreadingHTTPServer((HOST, PORT), Handler)
    srv.daemon_threads = True
    print(f"Shopkeeper proxy on http://{HOST}:{PORT}  provider={PROVIDER} model={MODEL if PROVIDER != 'mock' else 'mock'}",
          flush=True)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
