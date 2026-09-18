#!/usr/bin/env python3
"""Fake agent-memory gateway for PersonalDashboard business tests.

Serves the sample wire contract (sample_dashboard.json, sample_status.json)
over HTTP on 127.0.0.1. Run it, then point tests at it:

    python fake_gateway.py [port]            # default 8123
    MEMORY_TEST_GATEWAY_URL=http://127.0.0.1:8123 dotnet test

Exits immediately if the port is already taken (caller retries with another).
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
DASHBOARD = json.loads((HERE / "sample_dashboard.json").read_text(encoding="utf-8"))
STATUS = json.loads((HERE / "sample_status.json").read_text(encoding="utf-8"))


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/v1/dashboard":
            body = DASHBOARD
        elif self.path == "/v1/status":
            body = STATUS
        else:
            self.send_response(404)
            self.end_headers()
            return
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, fmt, *args):  # keep test output quiet
        pass


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8123
    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    print(f"fake gateway on 127.0.0.1:{port}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()