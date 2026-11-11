"""Strict local Groq double for procurement AI integration tests."""
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


class GroqStub(BaseHTTPRequestHandler):
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
        if self.path != "/openai/v1/chat/completions":
            self.send_error(404)
            return
        if self.headers.get("Authorization") != "Bearer test-groq-key":
            self.send_error(401)
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0 or length > 16 * 1024:
                raise ValueError("invalid body size")
            request = json.loads(self.rfile.read(length))
            assert request["model"] == "openai/gpt-oss-20b"
            assert request["include_reasoning"] is False
            assert request["reasoning_effort"] == "low"
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
    with socketserver.ThreadingTCPServer(("127.0.0.1", 0), GroqStub) as server:
        Path(sys.argv[1]).write_text(str(server.server_address[1]), encoding="ascii")
        server.serve_forever()
