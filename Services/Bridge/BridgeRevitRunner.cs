using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.UI;
using Zexus.Bridge;
using Zexus.Models;
using Zexus.Tools;

namespace Zexus.Services.Bridge
{
    /// <summary>
    /// Runs a bridge request on the Revit UI thread: write confirmation (user-only),
    /// policy gate reuse, ExecuteCode tool execution, and result mapping.
    /// </summary>
    public static class BridgeRevitRunner
    {
        public static void ExecuteOnUiThread(UIApplication app, BridgeRequestRecord record, ToolRegistry registry)
        {
            try
            {
                if (record.IsTerminal) return; // expired/cancelled while queued
                record.SetStatus(BridgeRequestStatus.WaitingForRevit);

                // Heuristic write detection: never fully trust the caller's flag.
                var inferredPatterns = BridgeWriteSignal.DetectedPatterns(record.Code);
                var requiresConfirmation = record.IsWriteOperation || inferredPatterns.Count > 0;

                string forcedWriteWarning = null;
                if (inferredPatterns.Count > 0 && !record.IsWriteOperation)
                {
                    forcedWriteWarning =
                        "Write operation was inferred from code (" +
                        string.Join(", ", inferredPatterns) +
                        ") and confirmation was forced.";
                }

                // 1) Write confirmation - only the Revit user can approve.
                if (requiresConfirmation && !BridgeApprovalSettings.IsFullAccess)
                {
                    record.SetStatus(BridgeRequestStatus.PendingConfirmation,
                        "Waiting for user approval in Revit...");

                    var approval = WriteConfirmationDialog.Show(record, forcedWriteWarning);
                    if (approval != ConfirmationResult.Approved)
                    {
                        record.Complete(WithWarning(new BridgeCompletionData
                        {
                            Status = BridgeRequestStatus.Rejected,
                            Message = approval == ConfirmationResult.Denied
                                ? "Write operation rejected by user"
                                : "Write operation not confirmed (confirmation timed out)",
                            ErrorType = "user_denied"
                        }, forcedWriteWarning));
                        ZexusLogger.Info($"[Bridge] write rejected: {record.RequestId}");
                        return;
                    }
                }

                if (requiresConfirmation && BridgeApprovalSettings.IsFullAccess)
                {
                    ZexusLogger.Info($"[Bridge] write auto-approved by session FullAccess mode: {record.RequestId}");
                }

                if (record.IsTerminal) return;

                // 2) Reuse existing PolicyGate rules (currently guards bulk deletes in loops).
                var policy = new PolicyGate();
                var verdict = policy.Evaluate("ExecuteCode", new Dictionary<string, object>
                {
                    ["code"] = record.Code,
                    ["description"] = record.Description
                });

                if (verdict.Action == PolicyAction.Block)
                {
                    record.Complete(WithWarning(Fail("Blocked by policy: " + verdict.Message, "policy_block"), forcedWriteWarning));
                    return;
                }

                if ((verdict.Action == PolicyAction.RequireConfirmation ||
                    verdict.Action == PolicyAction.ForcePreview) &&
                    !BridgeApprovalSettings.IsFullAccess)
                {
                    var approval = WriteConfirmationDialog.Show(record, verdict.Message);
                    if (approval != ConfirmationResult.Approved)
                    {
                        record.Complete(WithWarning(new BridgeCompletionData
                        {
                            Status = BridgeRequestStatus.Rejected,
                            Message = "Additional confirmation not approved by user",
                            ErrorType = "user_denied"
                        }, forcedWriteWarning));
                        return;
                    }
                }

                if (record.IsTerminal) return;
                record.SetStatus(BridgeRequestStatus.Running);

                var uiDoc = app.ActiveUIDocument;
                var doc = uiDoc?.Document;
                if (doc == null)
                {
                    record.Complete(WithWarning(Fail("No active document. Please open a Revit model first.", "no_document"), forcedWriteWarning));
                    return;
                }

                var tool = registry?.GetTool("ExecuteCode");
                if (tool == null)
                {
                    record.Complete(WithWarning(Fail("ExecuteCode tool is not registered.", "no_tool"), forcedWriteWarning));
                    return;
                }

                var result = tool.Execute(doc, uiDoc, new Dictionary<string, object>
                {
                    ["code"] = record.Code,
                    ["description"] = record.Description
                }) as ToolResult;

                ApplyResult(record, result, forcedWriteWarning);
            }
            catch (Exception ex)
            {
                ZexusLogger.Error("[Bridge] runner exception: " + ex);
                record.Complete(Fail("Bridge execution error: " + ex.Message, "runner"));
            }
        }

        private static BridgeCompletionData WithWarning(BridgeCompletionData data, string warning)
        {
            if (string.IsNullOrEmpty(warning)) return data;
            if (data.Warnings == null) data.Warnings = new List<string>();
            data.Warnings.Add(warning);
            return data;
        }

        private static void ApplyResult(BridgeRequestRecord record, ToolResult result, string forcedWriteWarning)
        {
            if (result == null)
            {
                record.Complete(WithWarning(Fail("Tool returned no result.", "no_result"), forcedWriteWarning));
                return;
            }

            var data = result.Data ?? new Dictionary<string, object>();

            if (!result.Success)
            {
                var failureType = data.TryGetValue("failure_type", out var ft) ? ft?.ToString() : null;
                var message = result.Message ?? "Execution failed";

                if (string.Equals(failureType, "compilation", StringComparison.OrdinalIgnoreCase))
                {
                    record.Complete(WithWarning(new BridgeCompletionData
                    {
                        Status = BridgeRequestStatus.Failed,
                        Message = "Compilation failed",
                        CompileErrors = SplitLines(message),
                        ErrorType = "compilation"
                    }, forcedWriteWarning));
                    return;
                }

                if (string.Equals(failureType, "runtime", StringComparison.OrdinalIgnoreCase))
                {
                    record.Complete(WithWarning(new BridgeCompletionData
                    {
                        Status = BridgeRequestStatus.Failed,
                        Message = "Runtime error",
                        RuntimeError = message,
                        ErrorType = "runtime"
                    }, forcedWriteWarning));
                    return;
                }

                record.Complete(WithWarning(new BridgeCompletionData
                {
                    Status = BridgeRequestStatus.Failed,
                    Message = message,
                    RuntimeError = message,
                    ErrorType = string.IsNullOrEmpty(failureType) ? "unknown" : failureType
                }, forcedWriteWarning));
                return;
            }

            // Success: map output + element tracking.
            var createdIds = new List<long>();
            if (data.TryGetValue("sys_created", out var created) && created is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is ElementDetail detail)
                    {
                        createdIds.Add(detail.ElementId);
                    }
                    else if (item is Dictionary<string, object> dict &&
                             dict.TryGetValue("element_id", out var idObj))
                    {
                        if (idObj is long l) createdIds.Add(l);
                        else if (idObj is int i) createdIds.Add(i);
                    }
                }
            }

            var modifiedIds = ExtractLongList(data, "sys_modified_ids");
            var deletedIds = ExtractLongList(data, "sys_deleted_ids");

            var warnings = new List<string>();
            if (!string.IsNullOrEmpty(result.Warning)) warnings.Add(result.Warning);
            if (data.TryGetValue("zero_modification_warning", out var z) && z is bool zb && zb)
            {
                warnings.Add("Code contained a Transaction but no elements were created, modified, or deleted - possible no-op.");
            }
            if (record.IsWriteOperation)
            {
                warnings.Add("Write completed inside a Revit transaction. The bridge does not roll back external side effects.");
            }
            if (!string.IsNullOrEmpty(forcedWriteWarning)) warnings.Add(forcedWriteWarning);

            var payload = new Dictionary<string, object>
            {
                ["message"] = result.Message,
                ["output"] = data.TryGetValue("output", out var output) ? output : "",
                ["return_value"] = data.TryGetValue("return_value", out var rv) ? rv : null,
                ["has_transaction"] = data.TryGetValue("has_transaction", out var ht) ? ht : false
            };

            record.Complete(new BridgeCompletionData
            {
                Status = BridgeRequestStatus.Succeeded,
                Message = result.Message,
                ResultJson = JsonSerializer.Serialize(payload),
                CreatedElementIds = createdIds,
                ModifiedElementIds = modifiedIds,
                DeletedElementIds = deletedIds,
                Warnings = warnings,
                RolledBack = false
            });
        }

        private static List<long> ExtractLongList(Dictionary<string, object> data, string key)
        {
            var result = new List<long>();
            if (data.TryGetValue(key, out var value) && value is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is long l) result.Add(l);
                    else if (item is int i) result.Add(i);
                }
            }
            return result;
        }

        private static List<string> SplitLines(string text)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                lines.Add(line.Trim());
            }
            return lines;
        }

        private static BridgeCompletionData Fail(string message, string errorType)
        {
            return new BridgeCompletionData
            {
                Status = BridgeRequestStatus.Failed,
                Message = message,
                RuntimeError = message,
                ErrorType = errorType
            };
        }
    }
}
