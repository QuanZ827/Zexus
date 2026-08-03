# Revit MCP smoke test

With Revit open, Zexus loaded, and a disposable test model active, ask the connected MCP client:

> Use `revit_execute_code` to return the active document title and active view name. This is read-only; set `isWriteOperation` to false. Do not create or modify elements.

Expected: `succeeded` with document/view information and no confirmation dialog.

Write-path test, only in a disposable model:

> Use `revit_execute_code` to create one model curve on the active sketch plane. Mark it as a write operation. Stop and let me approve the Revit confirmation dialog.

Expected: Revit shows confirmation before execution and the response reports changes when available.

These prompts are client-neutral. Codex is one supported MCP host, but any client that can launch a local STDIO MCP server can use the same `revit_execute_code` tool.
