import importlib.util
import json
import os
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


SPEC = importlib.util.spec_from_file_location(
    "revit_mcp_server", Path(__file__).with_name("revit_mcp_server.py")
)
SERVER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SERVER)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def send_json(self, value, status=200):
        body = json.dumps(value).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        assert self.headers.get("Authorization") == "Bearer test-token"
        self.send_json({"requestId": "abc", "status": "queued"}, 202)

    def do_GET(self):
        self.send_json(
            {"requestId": "abc", "status": "succeeded", "result": {"value": "ok"}}
        )


class Tests(unittest.TestCase):
    def setUp(self):
        self.previous_token_file = os.environ.get("ZEXUS_BRIDGE_TOKEN_FILE")
        self.http = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.http.serve_forever, daemon=True)
        self.thread.start()
        self.temp = tempfile.TemporaryDirectory()
        token = Path(self.temp.name) / "token.txt"
        token.write_text(
            "ZEXUS_BRIDGE_TOKEN=test-token\n"
            f"ZEXUS_BRIDGE_URL=http://127.0.0.1:{self.http.server_port}\n",
            encoding="utf-8",
        )
        os.environ["ZEXUS_BRIDGE_TOKEN_FILE"] = str(token)

    def tearDown(self):
        self.http.shutdown()
        self.http.server_close()
        self.thread.join(timeout=2)
        self.temp.cleanup()
        if self.previous_token_file is None:
            os.environ.pop("ZEXUS_BRIDGE_TOKEN_FILE", None)
        else:
            os.environ["ZEXUS_BRIDGE_TOKEN_FILE"] = self.previous_token_file

    def test_execute(self):
        result = SERVER.execute(
            {
                "description": "test",
                "code": "return doc.Title;",
                "timeoutSeconds": 2,
            }
        )
        self.assertEqual("succeeded", result["status"])

    def test_protocol_tools(self):
        response = SERVER.handle(
            {"jsonrpc": "2.0", "id": 1, "method": "tools/list"}
        )
        tool = response["result"]["tools"][0]
        self.assertEqual("revit_execute_code", tool["name"])
        self.assertTrue(tool["annotations"]["destructiveHint"])

    def test_protocol_version_negotiation(self):
        current = SERVER.handle(
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "initialize",
                "params": {"protocolVersion": "2025-11-25"},
            }
        )
        self.assertEqual("2025-11-25", current["result"]["protocolVersion"])

        fallback = SERVER.handle(
            {
                "jsonrpc": "2.0",
                "id": 2,
                "method": "initialize",
                "params": {"protocolVersion": "unsupported"},
            }
        )
        self.assertEqual(
            SERVER.LATEST_PROTOCOL_VERSION, fallback["result"]["protocolVersion"]
        )

    def test_invalid_tool_arguments_are_tool_errors(self):
        response = SERVER.handle(
            {
                "jsonrpc": "2.0",
                "id": 3,
                "method": "tools/call",
                "params": {
                    "name": "revit_execute_code",
                    "arguments": {"description": "missing code"},
                },
            }
        )
        self.assertTrue(response["result"]["isError"])

    def test_batch_and_notifications(self):
        response = SERVER.process_payload(
            [
                {"jsonrpc": "2.0", "method": "notifications/initialized"},
                {"jsonrpc": "2.0", "id": 4, "method": "ping"},
            ]
        )
        self.assertEqual(1, len(response))
        self.assertEqual(4, response[0]["id"])

    def test_refuses_non_loopback_token_url(self):
        token = Path(self.temp.name) / "bad-token.txt"
        token.write_text(
            "ZEXUS_BRIDGE_TOKEN=test-token\n"
            "ZEXUS_BRIDGE_URL=http://example.com:4821\n",
            encoding="utf-8",
        )
        os.environ["ZEXUS_BRIDGE_TOKEN_FILE"] = str(token)
        with self.assertRaisesRegex(RuntimeError, "127.0.0.1"):
            SERVER.connection()


if __name__ == "__main__":
    unittest.main()
