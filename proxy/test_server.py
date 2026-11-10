"""Offline tests for the proxy (mock provider, no network, no key). Run: python -m unittest proxy/test_server.py"""
import json
import os
import sys
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from pathlib import Path

os.environ["LLM_PROVIDER"] = "mock"
os.environ["MOCK_CHAOS"] = "0"
FAKE_KEY = "test-sentinel-key-do-not-leak"
os.environ["LLM_API_KEY"] = FAKE_KEY  # set before import so proxy/.env (setdefault) can never inject the real key
sys.path.insert(0, str(Path(__file__).resolve().parent))
import server  # noqa: E402


def body(**over):
    b = {"system": "You are a shopkeeper.",
         "messages": [{"role": "user", "text": "GAME STATE: round 1 of 8; your current asking price: 120 gold; "
                                               "customer's current offer: 60 gold.\nCUSTOMER SAYS: <<<60?>>>"}],
         "temperature": 0.7, "max_output_tokens": 200}
    b.update(over)
    return b


class ValidateRequestTests(unittest.TestCase):
    def test_clamps_temperature_and_tokens(self):
        r = server.validate_request(body(temperature=9, max_output_tokens=99999))
        self.assertEqual(r["temperature"], 2.0)
        self.assertEqual(r["max_output_tokens"], server.MAX_OUTPUT_TOKENS_CAP)

    def test_rejects_bad_shapes(self):
        for bad in [body(system=""), body(messages=[]), body(messages=[{"role": "system", "text": "x"}]),
                    body(messages=[{"role": "model", "text": "x"}]), body(response_schema="nope"),
                    body(messages=[{"role": "user", "text": "x" * (server.MAX_MESSAGE_CHARS + 1)}])]:
            with self.assertRaises(ValueError):
                server.validate_request(bad)


class HttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), server.Handler)
        cls.url = f"http://127.0.0.1:{cls.srv.server_address[1]}"
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()

    def post(self, payload):
        req = urllib.request.Request(self.url + "/v1/generate", json.dumps(payload).encode(), {"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def test_health(self):
        with urllib.request.urlopen(self.url + "/health", timeout=5) as r:
            self.assertEqual(json.loads(r.read())["provider"], "mock")

    def test_generate_returns_model_json(self):
        status, out = self.post(body())
        self.assertEqual(status, 200)
        turn = json.loads(out["text"])
        self.assertEqual(set(turn), {"reply", "mood", "offered_price", "intent"})
        self.assertIn("latency_ms", out)

    def test_bad_request_is_400_without_leaking_internals(self):
        status, out = self.post({"system": "x"})
        self.assertEqual(status, 400)
        self.assertNotIn("Traceback", out["error"])

    def test_response_never_contains_key(self):
        _, out = self.post(body())
        self.assertNotIn(os.environ.get("LLM_API_KEY", "\0never\0"), json.dumps(out))


if __name__ == "__main__":
    unittest.main()
