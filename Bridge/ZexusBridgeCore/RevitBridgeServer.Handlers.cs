using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Zexus.Bridge
{
    public sealed partial class RevitBridgeServer
    {
        private const string ServiceVersion = "0.1.0";

        private void HandleContext(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var path = request.Url.AbsolutePath.TrimEnd('/');
                if (path.Length == 0) path = "/";

                if (path == "/health")
                {
                    HandleHealth(context);
                    return;
                }

                if (!IsAuthorized(request))
                {
                    WriteJson(context, 401, new { error = "Unauthorized", detail = "Missing or invalid token" });
                    return;
                }

                if (path == "/execute")
                {
                    if (request.HttpMethod == "POST")
                    {
                        HandleExecute(context);
                        return;
                    }
                    WriteJson(context, 405, new { error = "Method not allowed", detail = "Use POST /execute" });
                    return;
                }

                if (path.StartsWith("/requests/", StringComparison.Ordinal))
                {
                    var id = path.Substring("/requests/".Length);
                    if (string.IsNullOrEmpty(id))
                    {
                        WriteJson(context, 400, new { error = "Bad request", detail = "Missing request id" });
                        return;
                    }
                    if (request.HttpMethod == "GET")
                    {
                        HandleGetRequest(context, id);
                        return;
                    }
                    if (request.HttpMethod == "DELETE")
                    {
                        HandleCancelRequest(context, id);
                        return;
                    }
                    WriteJson(context, 405, new { error = "Method not allowed" });
                    return;
                }

                WriteJson(context, 404, new { error = "Not found", detail = "Unknown endpoint: " + path });
            }
            catch (Exception ex)
            {
                _audit.Write("http_error", null, null, ex.Message);
                TryWriteJson(context, 500, new { error = "Internal server error", detail = ex.Message });
            }
        }

        private bool IsAuthorized(HttpListenerRequest request)
        {
            if (string.IsNullOrEmpty(_token)) return false;

            var header = request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return ConstantTimeEquals(header.Substring(7).Trim(), _token);

            return false;
        }

        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private void HandleHealth(HttpListenerContext context)
        {
            var counts = new Dictionary<string, int>();
            foreach (var r in _store.Snapshot())
            {
                var name = BridgeAuditLog.StatusName(r.Status);
                counts.TryGetValue(name, out int c);
                counts[name] = c + 1;
            }

            WriteJson(context, 200, new
            {
                ok = true,
                service = "zexus-revit-bridge",
                version = ServiceVersion,
                port = Port,
                uptimeSeconds = (long)(DateTime.UtcNow - _startedAtUtc).TotalSeconds,
                documentOpen = _executor.IsDocumentOpen,
                executor = _executor.HealthDetail,
                tokenRequired = true,
                queue = new
                {
                    active = _store.CountActive(),
                    max = _options.MaxQueuedRequests,
                    byStatus = counts
                }
            });
        }

        private void HandleExecute(HttpListenerContext context)
        {
            string body;
            try
            {
                body = ReadBody(context.Request);
            }
            catch (BridgeBodyTooLarge)
            {
                WriteJson(context, 413, new { error = "Payload too large", detail = "Max body " + _options.MaxBodyBytes + " bytes" });
                return;
            }

            JsonElement root;
            try
            {
                using (var doc = JsonDocument.Parse(body))
                    root = doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                WriteJson(context, 400, new { error = "Invalid JSON", detail = ex.Message });
                return;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                WriteJson(context, 400, new { error = "Invalid request", detail = "Body must be a JSON object" });
                return;
            }

            var allowedFields = new HashSet<string>(StringComparer.Ordinal)
            {
                "description",
                "code",
                "isWriteOperation",
                "timeoutSeconds"
            };
            foreach (var property in root.EnumerateObject())
            {
                if (!allowedFields.Contains(property.Name))
                {
                    WriteJson(context, 400, new
                    {
                        error = "Invalid request",
                        detail = "Unknown field: " + property.Name
                    });
                    return;
                }
            }

            var code = GetString(root, "code");
            if (string.IsNullOrWhiteSpace(code))
            {
                WriteJson(context, 400, new { error = "Missing field", detail = "'code' is required and must be non-empty" });
                return;
            }

            if (root.TryGetProperty("description", out var descriptionElement) &&
                descriptionElement.ValueKind != JsonValueKind.String)
            {
                WriteJson(context, 400, new { error = "Invalid field", detail = "'description' must be a string" });
                return;
            }
            if (root.TryGetProperty("isWriteOperation", out var writeElement) &&
                writeElement.ValueKind != JsonValueKind.True &&
                writeElement.ValueKind != JsonValueKind.False)
            {
                WriteJson(context, 400, new { error = "Invalid field", detail = "'isWriteOperation' must be a boolean" });
                return;
            }
            if (root.TryGetProperty("timeoutSeconds", out var timeoutElement) &&
                (timeoutElement.ValueKind != JsonValueKind.Number ||
                 !timeoutElement.TryGetInt32(out var requestedTimeout) ||
                 requestedTimeout < 1 ||
                 requestedTimeout > _options.MaxTimeoutSeconds))
            {
                WriteJson(context, 400, new
                {
                    error = "Invalid field",
                    detail = "'timeoutSeconds' must be an integer from 1 through " + _options.MaxTimeoutSeconds
                });
                return;
            }

            var description = GetString(root, "description") ?? "";
            var isWrite = GetBool(root, "isWriteOperation") ?? false;
            var timeoutSeconds = GetInt(root, "timeoutSeconds") ?? _options.DefaultTimeoutSeconds;

            var violations = BridgeSafety.Check(code);
            if (violations.Count > 0)
            {
                _audit.Write("request_blocked_safety", null, "rejected",
                    "Blocked patterns: " + string.Join("; ", violations));
                WriteJson(context, 400, new
                {
                    error = "Blocked by safety policy",
                    detail = "Code contains patterns blocked in this development build.",
                    blocked_patterns = violations,
                    note = "String matching is NOT a security sandbox; this is a heuristic guard only."
                });
                return;
            }

            if (_store.CountActive() >= _options.MaxQueuedRequests)
            {
                WriteJson(context, 429, new
                {
                    error = "Queue full",
                    detail = "Maximum active requests reached (" + _options.MaxQueuedRequests + "). Try again later."
                });
                return;
            }

            var record = _store.Create(description, code, isWrite, timeoutSeconds, _options.ConfirmationTimeoutSeconds);
            _audit.WriteCreated(record);

            try
            {
                _executor.Submit(record);
            }
            catch (Exception ex)
            {
                record.Complete(new BridgeCompletionData
                {
                    Status = BridgeRequestStatus.Failed,
                    Message = "Failed to submit request to Revit: " + ex.Message,
                    ErrorType = "submit"
                });
                _audit.WriteTransition(record, "request_submit_failed");
            }

            WriteJson(context, 202, new { requestId = record.RequestId, status = BridgeAuditLog.StatusName(record.Status) });
        }

        private void HandleGetRequest(HttpListenerContext context, string id)
        {
            if (_store.TryGet(id, out var record))
            {
                WriteJson(context, 200, record.ToSnapshot());
                return;
            }
            WriteJson(context, 404, new { error = "Not found", detail = "Unknown request id: " + id });
        }

        private void HandleCancelRequest(HttpListenerContext context, string id)
        {
            if (!_store.TryGet(id, out var record))
            {
                WriteJson(context, 404, new { error = "Not found", detail = "Unknown request id: " + id });
                return;
            }

            if (record.TryCancel("Cancelled by caller before execution", out var error))
            {
                _audit.WriteTransition(record, "request_cancelled");
                WriteJson(context, 200, record.ToSnapshot());
                return;
            }

            WriteJson(context, 409, new { error = "Cannot cancel", detail = error });
        }

        private string ReadBody(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                var buffer = new char[8192];
                var sb = new StringBuilder();
                int total = 0;
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > _options.MaxBodyBytes)
                        throw new BridgeBodyTooLarge();
                    sb.Append(buffer, 0, read);
                }
                return sb.ToString();
            }
        }

        private static string GetString(JsonElement obj, string name)
        {
            if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
            return null;
        }

        private static bool? GetBool(JsonElement obj, string name)
        {
            if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True) return true;
            if (obj.TryGetProperty(name, out var el2) && el2.ValueKind == JsonValueKind.False) return false;
            return null;
        }

        private static int? GetInt(JsonElement obj, string name)
        {
            if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i))
                return i;
            return null;
        }

        private void WriteJson(HttpListenerContext context, int statusCode, object payload)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, BridgeJson.Options);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.Close();
            }
            catch
            {
                // client may have disconnected
            }
        }

        private void TryWriteJson(HttpListenerContext context, int statusCode, object payload)
        {
            try { WriteJson(context, statusCode, payload); } catch { }
        }
    }

    internal sealed class BridgeBodyTooLarge : Exception
    {
    }
}
