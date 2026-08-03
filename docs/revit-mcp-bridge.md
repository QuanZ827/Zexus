# Revit MCP bridge (development)

The Zexus bridge lets an external MCP-compatible coding agent execute the existing `ExecuteCode` tool inside Revit. It is intended for local development and testing, not unattended or remote automation.

## Architecture

```text
MCP host -> Python STDIO server -> loopback HTTP bridge -> Revit ExternalEvent -> ExecuteCode
```

The Python server is dependency-free. It translates MCP `tools/call` requests into authenticated requests to the bridge running inside the Revit process. Every Revit API operation stays behind `ExternalEvent` and therefore runs on Revit's UI thread.

## Requirements

- Windows with a supported Revit version and the Zexus add-in loaded.
- Python 3.8 or newer available to the MCP host.
- An MCP host that supports local STDIO servers.
- A disposable Revit model for every write-path test.

When Zexus starts, the bridge binds only to `127.0.0.1`. It chooses port `4821` when available and tries nearby ports when necessary. A new random bearer token is written for the current Revit session to:

```text
%APPDATA%\Zexus\bridge\token.txt
```

The MCP proxy reads the URL and token from that file automatically. The token file is removed when the bridge stops.

## MCP configuration

Use an absolute path to `mcp/revit_mcp_server.py`. Do not copy the session token into a configuration file.

Codex project configuration example:

```toml
[mcp_servers.zexus_revit]
command = "python"
args = ["C:/path/to/zexus/mcp/revit_mcp_server.py"]
cwd = "C:/path/to/zexus"
startup_timeout_sec = 10
tool_timeout_sec = 180
```

Generic JSON-style MCP configuration used by many other hosts:

```json
{
  "mcpServers": {
    "zexus_revit": {
      "command": "python",
      "args": ["C:/path/to/zexus/mcp/revit_mcp_server.py"]
    }
  }
}
```

The exact configuration location and property names depend on the MCP host.

## Tool

The server exposes one tool:

```text
revit_execute_code
```

Arguments:

- `description` (required): short explanation shown in logs and confirmation UI.
- `code` (required): a C# method body compiled by the existing Zexus `ExecuteCode` pipeline.
- `isWriteOperation` (optional, default `false`): must be `true` for model-changing code.
- `timeoutSeconds` (optional, 1-300, default `30`): execution timeout, excluding the confirmation window.

The bridge also scans for common write-like Revit API patterns and can force confirmation when a caller incorrectly declares code read-only. This scan is heuristic and is not a security sandbox.

## Revit approval modes

The `Zexus -> Bridge` ribbon command shows the current status and approval mode.

- **Ask approval** is the default. Every declared or detected write opens a visible Revit confirmation dialog.
- **Full access** auto-approves writes only for the current Revit process. Enabling it requires a visible warning, and restarting Revit resets the mode to Ask approval.

Safety scanning, bearer authentication, audit logging, and `ExternalEvent` dispatch remain enabled in both modes.

## Security boundaries

- The HTTP listener accepts only `127.0.0.1`; it must never be exposed to the network.
- All non-health endpoints require the per-session token in an `Authorization: Bearer` header.
- Tokens in URLs, alternate headers, or source-controlled configuration are not accepted.
- The token and generated C# source are not written to the audit log. The log records request metadata and code hashes.
- Generated code runs with the privileges of the Revit process. The string-based safety scan catches common dangerous calls but cannot provide isolation.
- Never use this bridge for unattended production writes. Inspect the generated code and use a disposable model during development.

Audit events are written to:

```text
%APPDATA%\Zexus\bridge\bridge-audit.log
```

## Verification

Run the Revit-free bridge tests and MCP protocol tests:

```powershell
dotnet run --project Bridge/ZexusBridgeCore.Tests/ZexusBridgeCore.Tests.csproj -c Debug
python -W error -m unittest mcp/test_revit_mcp_server.py
```

Then follow [Revit MCP smoke test](revit-mcp-smoke-test.md) with Revit open and a disposable model active.
