using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Zexus.Bridge
{
    /// <summary>
    /// JSON-lines audit log for bridge activity. The request token is never written.
    /// </summary>
    public sealed class BridgeAuditLog : IDisposable
    {
        private readonly object _lock = new object();
        private readonly string _path;
        private StreamWriter _writer;

        public BridgeAuditLog(string path)
        {
            _path = path;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _writer = new StreamWriter(path, append: true, Encoding.UTF8);
                _writer.AutoFlush = true;
            }
            catch
            {
                _writer = null; // audit must never crash the bridge
            }
        }

        public void WriteStart(int port, string bindHost)
        {
            WriteLine("{\"ev\":\"bridge_started\",\"port\":" + port +
                ",\"bindHost\":" + JsonString(bindHost) +
                ",\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id + "}");
        }

        public void WriteStop()
        {
            WriteLine("{\"ev\":\"bridge_stopped\"}");
        }

        public void WriteCreated(BridgeRequestRecord record)
        {
            WriteLine("{\"ev\":\"request_created\",\"requestId\":" + JsonString(record.RequestId) +
                ",\"status\":" + JsonString(StatusName(record.Status)) +
                ",\"description\":" + JsonString(Truncate(record.Description, 160)) +
                ",\"codeHash\":" + JsonString(CodeHash(record.Code)) +
                ",\"codeLength\":" + (record.Code?.Length ?? 0) +
                ",\"isWrite\":" + (record.IsWriteOperation ? "true" : "false") +
                ",\"timeoutSeconds\":" + record.TimeoutSeconds + "}");
        }

        public void WriteTransition(BridgeRequestRecord record, string eventName, string message = null)
        {
            var warnings = record.Warnings != null && record.Warnings.Count > 0 ? string.Join(" | ", record.Warnings) : null;
            WriteLine("{\"ev\":" + JsonString(eventName) +
                ",\"requestId\":" + JsonString(record.RequestId) +
                ",\"status\":" + JsonString(StatusName(record.Status)) +
                ",\"errorType\":" + JsonString(record.ErrorType) +
                ",\"elapsedMs\":" + record.ElapsedMilliseconds +
                ",\"warnings\":" + JsonString(warnings) +
                ",\"message\":" + JsonString(Truncate(message ?? record.Message, 300)) + "}");
        }

        public void Write(string eventName, string requestId, string status, string message)
        {
            WriteLine("{\"ev\":" + JsonString(eventName) +
                ",\"requestId\":" + JsonString(requestId) +
                ",\"status\":" + JsonString(status) +
                ",\"message\":" + JsonString(Truncate(message, 300)) + "}");
        }

        private void WriteLine(string line)
        {
            if (_writer == null) return;
            lock (_lock)
            {
                try
                {
                    _writer.WriteLine(line);
                }
                catch
                {
                    // ignore audit write failures
                }
            }
        }

        private static string JsonString(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u" + ((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string StatusName(BridgeRequestStatus status)
        {
            switch (status)
            {
                case BridgeRequestStatus.Queued: return "queued";
                case BridgeRequestStatus.WaitingForRevit: return "waiting_for_revit";
                case BridgeRequestStatus.PendingConfirmation: return "pending_confirmation";
                case BridgeRequestStatus.Running: return "running";
                case BridgeRequestStatus.Succeeded: return "succeeded";
                case BridgeRequestStatus.Failed: return "failed";
                case BridgeRequestStatus.Rejected: return "rejected";
                case BridgeRequestStatus.Expired: return "expired";
                case BridgeRequestStatus.Cancelled: return "cancelled";
                default: return status.ToString().ToLowerInvariant();
            }
        }

        public static string CodeHash(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            try
            {
                using (var sha = SHA256.Create())
                {
                    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(code));
                    var sb = new StringBuilder(16);
                    for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return "";
            }
        }

        internal static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}
