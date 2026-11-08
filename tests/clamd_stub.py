"""Local protocol double for deterministic ClamAV replies in smoke tests."""
import socketserver
import struct
import sys
from pathlib import Path


class Scanner(socketserver.StreamRequestHandler):
    def handle(self):
        if self.rfile.read(10) != b"zINSTREAM\0":
            return
        chunks = []
        size = 0
        while prefix := self.rfile.read(4):
            length = struct.unpack(">I", prefix)[0]
            if length == 0:
                break
            size += length
            if size > 2 * 1024 * 1024:
                return
            chunks.append(self.rfile.read(length))
        data = b"".join(chunks)
        if b"KASA_TEST_THREAT" in data:
            reply = b"stream: Test.Signature FOUND\0"
        elif b"KASA_TEST_NO_VERDICT" in data:
            reply = b"stream: scanning unavailable ERROR\0"
        else:
            reply = b"stream: OK\0"
        self.wfile.write(reply)


if __name__ == "__main__":
    with socketserver.ThreadingTCPServer(("127.0.0.1", 0), Scanner) as server:
        Path(sys.argv[1]).write_text(str(server.server_address[1]), encoding="ascii")
        server.serve_forever()
