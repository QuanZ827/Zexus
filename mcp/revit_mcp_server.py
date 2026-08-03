#!/usr/bin/env python3
"""Dependency-free STDIO MCP proxy for the local Zexus Revit HTTP bridge."""

from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any


SUPPORTED_PROTOCOL_VERSIONS = (
    "2025-11-25",
    "2025-06-18",
    "2025-03-26",
    "2024-11-05",
)
LATEST_PROTOCOL_VERSION = SUPPORTED_PROTOCOL_VERSIONS[0]
TERMINAL_STATUSES = {"succeeded", "failed", "rejected", "expired", "cancelled"}

TOOL_SCHEMA = {
    "name": "revit_execute_code",
    "title": "Execute Revit C# code",
    "description": (
        "Execute a C# method body inside Revit through the local Zexus development "
        "bridge. Write-like code follows the Revit session approval mode."
    ),
    "inputSchema": {
        "type": "object",
        "properties": {
            "description": {
                "type": "string",
                "description": "Short human-readable explanation of the operation.",
            },
            "code": {
                "type": "string",
                "description": "C# method body to compile and execute inside Revit.",
            },
            "isWriteOperation": {
                "type": "boolean",
                "default": False,
                "description": "Set true for every model-changing operation.",
            },
            "timeoutSeconds": {
                "type": "integer",
                "minimum": 1,
                "maximum": 300,
                "default": 30,
            },
        },
        "required": ["description", "code"],
        "additionalProperties": False,
    },
    "annotations": {
        "readOnlyHint": False,
        "destructiveHint": True,
        "idempotentHint": False,
        "openWorldHint": False,
    },
}


class ProtocolError(Exception):
    def __init__(self, code: int, message: str):
        super().__init__(message)
        self.code = code
        self.message = message


class ToolInputError(ValueError):
    pass


def token_file() -> Path:
    configured = os.environ.get("ZEXUS_BRIDGE_TOKEN_FILE")
    if configured:
        return Path(configured)
    app_data = os.environ.get("APPDATA", str(Path.home()))
    return Path(app_data) / "Zexus" / "bridge" / "token.txt"


def connection() -> tuple[str, str]:
    path = token_file()
    if not path.exists():
        raise RuntimeError(
            f"Zexus Bridge token file not found: {path}. "
            "Start Revit with the Zexus add-in loaded."
        )

    values: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if "=" in line:
            key, value = line.split("=", 1)
            values[key.strip()] = value.strip()

    url = values.get("ZEXUS_BRIDGE_URL")
    token = values.get("ZEXUS_BRIDGE_TOKEN")
    if not url or not token:
        raise RuntimeError("Zexus Bridge token file is incomplete")

    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1":
        raise RuntimeError("Refusing a bridge URL that is not http://127.0.0.1")

    return url.rstrip("/"), token


def request(method: str, path: str, payload: dict[str, Any] | None = None) -> dict[str, Any]:
    base, token = connection()
    body = None if payload is None else json.dumps(payload).encode("utf-8")
    http_request = urllib.request.Request(
        base + path,
        data=body,
        method=method,
        headers={
            "Authorization": "Bearer " + token,
            "Content-Type": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(http_request, timeout=10) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"Bridge HTTP {exc.code}: {detail}") from exc


def validate_arguments(arguments: Any) -> dict[str, Any]:
    if not isinstance(arguments, dict):
        raise ToolInputError("Tool arguments must be a JSON object")

    unknown = set(arguments) - {
        "description",
        "code",
        "isWriteOperation",
        "timeoutSeconds",
    }
    if unknown:
        raise ToolInputError("Unknown argument(s): " + ", ".join(sorted(unknown)))

    description = arguments.get("description")
    code = arguments.get("code")
    is_write = arguments.get("isWriteOperation", False)
    timeout = arguments.get("timeoutSeconds", 30)

    if not isinstance(description, str) or not description.strip():
        raise ToolInputError("description is required and must be a non-empty string")
    if not isinstance(code, str) or not code.strip():
        raise ToolInputError("code is required and must be a non-empty string")
    if not isinstance(is_write, bool):
        raise ToolInputError("isWriteOperation must be a boolean")
    if isinstance(timeout, bool) or not isinstance(timeout, int) or not 1 <= timeout <= 300:
        raise ToolInputError("timeoutSeconds must be an integer from 1 through 300")

    return {
        "description": description,
        "code": code,
        "isWriteOperation": is_write,
        "timeoutSeconds": timeout,
    }


def execute(arguments: Any) -> dict[str, Any]:
    payload = validate_arguments(arguments)
    created = request("POST", "/execute", payload)
    request_id = created["requestId"]
    deadline = time.monotonic() + payload["timeoutSeconds"] + 130

    while time.monotonic() < deadline:
        result = request("GET", "/requests/" + request_id)
        if result.get("status") in TERMINAL_STATUSES:
            return result
        time.sleep(0.25)

    raise RuntimeError(f"Timed out waiting for Revit request {request_id}")


def tool_error(message: str) -> dict[str, Any]:
    return {
        "content": [{"type": "text", "text": message}],
        "isError": True,
    }


def handle(message: Any) -> dict[str, Any] | None:
    if not isinstance(message, dict) or message.get("jsonrpc") != "2.0":
        raise ProtocolError(-32600, "Invalid JSON-RPC request")

    method = message.get("method")
    if not isinstance(method, str):
        raise ProtocolError(-32600, "JSON-RPC method must be a string")

    params = message.get("params") or {}
    if not isinstance(params, dict):
        raise ProtocolError(-32602, "params must be an object")
    request_id = message.get("id")

    if method == "initialize":
        requested = params.get("protocolVersion")
        if not isinstance(requested, str):
            raise ProtocolError(-32602, "initialize requires protocolVersion")
        negotiated = requested if requested in SUPPORTED_PROTOCOL_VERSIONS else LATEST_PROTOCOL_VERSION
        return {
            "jsonrpc": "2.0",
            "id": request_id,
            "result": {
                "protocolVersion": negotiated,
                "capabilities": {"tools": {"listChanged": False}},
                "serverInfo": {
                    "name": "zexus-revit",
                    "version": "0.1.0",
                    "description": "Local MCP proxy for the Zexus Revit development bridge",
                },
                "instructions": (
                    "Set isWriteOperation=true for every model-changing request. "
                    "Use a disposable Revit model for write tests."
                ),
            },
        }

    if method == "ping":
        return {"jsonrpc": "2.0", "id": request_id, "result": {}}

    if method.startswith("notifications/"):
        return None

    if method == "tools/list":
        return {
            "jsonrpc": "2.0",
            "id": request_id,
            "result": {"tools": [TOOL_SCHEMA]},
        }

    if method == "tools/call":
        if params.get("name") != TOOL_SCHEMA["name"]:
            raise ProtocolError(-32602, "Unknown tool: " + str(params.get("name")))
        try:
            result = execute(params.get("arguments"))
        except (ToolInputError, RuntimeError, KeyError, ValueError) as exc:
            call_result = tool_error(str(exc))
        else:
            call_result = {
                "content": [
                    {
                        "type": "text",
                        "text": json.dumps(result, ensure_ascii=False),
                    }
                ],
                "isError": result.get("status") != "succeeded",
            }
        return {"jsonrpc": "2.0", "id": request_id, "result": call_result}

    raise ProtocolError(-32601, "Method not found")


def error_response(request_id: Any, code: int, message: str) -> dict[str, Any]:
    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "error": {"code": code, "message": message},
    }


def process_message(message: Any) -> dict[str, Any] | None:
    request_id = message.get("id") if isinstance(message, dict) else None
    try:
        return handle(message)
    except ProtocolError as exc:
        return error_response(request_id, exc.code, exc.message)
    except Exception as exc:
        return error_response(request_id, -32603, "Internal error: " + str(exc))


def process_payload(payload: Any) -> dict[str, Any] | list[dict[str, Any]] | None:
    if isinstance(payload, list):
        if not payload:
            return error_response(None, -32600, "Empty JSON-RPC batch")
        responses = [response for item in payload if (response := process_message(item)) is not None]
        return responses or None
    return process_message(payload)


def main() -> None:
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8")
        sys.stdout.reconfigure(encoding="utf-8")

    for raw in sys.stdin:
        if not raw.strip():
            continue
        try:
            payload = json.loads(raw)
            response = process_payload(payload)
        except json.JSONDecodeError as exc:
            response = error_response(None, -32700, "Parse error: " + str(exc))

        if response is not None:
            print(
                json.dumps(response, ensure_ascii=False, separators=(",", ":")),
                flush=True,
            )


if __name__ == "__main__":
    main()
