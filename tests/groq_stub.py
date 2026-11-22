"""Strict local OpenAI-compatible provider double for procurement AI tests."""
import json
import socketserver
import sys
import threading
from http.server import BaseHTTPRequestHandler
from pathlib import Path


ALLOWED_FACTS = {
    "decision", "risk", "noSellableStock", "stockExcludedFromSale", "dropsBelowSafetyStock",
    "demandEstimatedManually", "insufficientSalesHistory", "supplierOfferMissing",
    "stockExpiresBeforeArrival", "stockExpiresDuringCoverage", "expectedDeliveryByArrival",
    "deliveryExpectedLater", "packageRounding",
}


class State:
    calls = 0
    mode = "ok"
    lock = threading.Lock()


class AiProviderStub(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path != "/calls":
            self.send_error(404)
            return
        with State.lock:
            payload = json.dumps({"calls": State.calls}).encode()
        self.reply(200, payload)

    def do_POST(self):
        if self.path.startswith("/mode/"):
            mode = self.path.removeprefix("/mode/")
            if mode not in {"ok", "unavailable", "quota"}:
                self.send_error(400)
                return
            with State.lock:
                State.mode = mode
            self.reply(200, json.dumps({"mode": mode}).encode())
            return
        if self.path != "/api/v1/chat/completions":
            self.send_error(404)
            return
        if self.headers.get("Authorization") != "Bearer test-openrouter-key":
            self.send_error(401)
            return
        if self.headers.get("HTTP-Referer") != "https://kassa.qr-space.uz":
            self.send_error(400)
            return
        if self.headers.get("X-Title") != "Kassa Minimarket":
            self.send_error(400)
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0 or length > 16 * 1024:
                raise ValueError("invalid body size")
            request = json.loads(self.rfile.read(length))
            assert request["model"] == "openrouter/free"
            assert "include_reasoning" not in request
            assert "reasoning_effort" not in request
            assert request["max_tokens"] == 320
            assert len(request["messages"]) == 2
            facts = json.loads(request["messages"][1]["content"])
            assert set(facts) == ALLOWED_FACTS
            assert all(value is None or isinstance(value, (str, bool)) for value in facts.values())
            serialized = request["messages"][1]["content"]
            assert "00000000-" not in serialized and not any(character.isdigit() for character in serialized)
        except (AssertionError, KeyError, TypeError, ValueError, json.JSONDecodeError):
            self.send_error(422)
            return
        with State.lock:
            State.calls += 1
            mode = State.mode
        if mode == "unavailable":
            self.reply(503, json.dumps({"error": "test_unavailable"}).encode())
            return
        if mode == "quota":
            self.reply(429, json.dumps({"error": "test_quota"}).encode())
            return
        explanation = (
            "Запас может закончиться до следующей поставки. Проверьте рекомендацию магазина."
            if facts["decision"] == "order_recommended"
            else "Запаса достаточно до следующей поставки. Дополнительный заказ сейчас не требуется."
        )
        payload = json.dumps({
            "choices": [{
                "finish_reason": "stop",
                "message": {"content": explanation},
            }]
        }, ensure_ascii=False).encode()
        self.reply(200, payload)

    def reply(self, status, payload):
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, _format, *_args):
        return


if __name__ == "__main__":
    with socketserver.ThreadingTCPServer(("127.0.0.1", 0), AiProviderStub) as server:
        Path(sys.argv[1]).write_text(str(server.server_address[1]), encoding="ascii")
        server.serve_forever()
