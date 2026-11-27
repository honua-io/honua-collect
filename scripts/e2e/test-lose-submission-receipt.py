#!/usr/bin/env python3
"""Exercise the simulator proxy against a disposable loopback HTTP server."""

import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import subprocess
import sys
import tempfile
from threading import Thread
import time
import unittest


class ControlledProxyTests(unittest.TestCase):
    def test_offline_and_lost_receipt(self):
        requests = []

        class Upstream(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def respond(self):
                payload = self.rfile.read(int(self.headers.get("Content-Length", "0")))
                requests.append((self.command, self.path, payload))
                self.send_response(200)
                self.send_header("Content-Length", "2")
                self.end_headers()
                self.wfile.write(b"OK")

            do_GET = do_POST = respond

            def log_message(self, *_):
                pass

        with ThreadingHTTPServer(("127.0.0.1", 0), Upstream) as upstream:
            thread = Thread(target=upstream.serve_forever, daemon=True)
            thread.start()
            self.addCleanup(thread.join)
            self.addCleanup(upstream.shutdown)
            with tempfile.TemporaryDirectory() as directory:
                state = Path(directory)
                proxy = Path(__file__).with_name("lose-submission-receipt.py")
                with subprocess.Popen([
                    sys.executable, str(proxy), str(upstream.server_port), "inspection",
                    str(state), "--controlled",
                ]) as process:
                    try:
                        deadline = time.monotonic() + 5
                        while not (state / "port").exists():
                            self.assertIsNone(process.poll(), "Proxy exited before it started")
                            self.assertLess(time.monotonic(), deadline, "Proxy did not publish its port")
                            time.sleep(0.02)
                        port = int((state / "port").read_text())
                        form = "/api/v1/forms/inspection"
                        submissions = form + "/submissions"

                        def request(method, path, payload=None):
                            client = http.client.HTTPConnection("127.0.0.1", port, timeout=3)
                            try:
                                client.request(method, path, payload)
                                response = client.getresponse()
                                return response.status, response.read()
                            finally:
                                client.close()

                        self.assertEqual(request("GET", form), (200, b"OK"))
                        offline = state / "offline"
                        offline.touch()
                        with self.assertRaises(http.client.RemoteDisconnected):
                            request("GET", form)
                        self.assertTrue((state / "blocked").is_file())
                        self.assertEqual(requests, [("GET", form, b"")])
                        offline.unlink()

                        lose_receipt = state / "lose-receipt"
                        lose_receipt.touch()
                        self.assertEqual(request("POST", submissions, b"inspection"), (503, b""))
                        self.assertTrue((state / "withheld").is_file())
                        self.assertEqual(requests[-1], ("POST", submissions, b"inspection"))
                        lose_receipt.unlink()

                        self.assertEqual(request("POST", submissions, b"inspection"), (200, b"OK"))
                        self.assertEqual(requests, [
                            ("GET", form, b""),
                            ("POST", submissions, b"inspection"),
                            ("POST", submissions, b"inspection"),
                        ])
                    finally:
                        process.terminate()
                        process.wait(timeout=5)


if __name__ == "__main__":
    unittest.main()
