using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Zexus.Bridge
{
    public enum BridgeRequestStatus
    {
        Queued = 0,
        WaitingForRevit = 1,
        PendingConfirmation = 2,
        Running = 3,
        Succeeded = 4,
        Failed = 5,
        Rejected = 6,
        Expired = 7,
        Cancelled = 8
    }

    /// <summary>Data used to complete a request (set once from the executor).</summary>
    public sealed class BridgeCompletionData
    {
        public BridgeRequestStatus Status;
        public string Message;
        public string ResultJson;
        public List<string> CompileErrors;
        public string RuntimeError;
        public List<long> CreatedElementIds;
        public List<long> ModifiedElementIds;
        public List<long> DeletedElementIds;
        public bool RolledBack;
        public List<string> Warnings;
        public string ErrorType;
    }

    /// <summary>
    /// Thread-safe, immutable-fields request record. Status and results are guarded
    /// by a lock; once a request reaches a terminal status it can never change again.
    /// </summary>
    public sealed class BridgeRequestRecord
    {
        private readonly object _sync = new object();

        public string RequestId { get; }
        public string Description { get; }
        public string Code { get; }
        public bool IsWriteOperation { get; }
        public int TimeoutSeconds { get; }
        public DateTime CreatedAtUtc { get; }
        public DateTime DeadlineUtc { get; }

        public BridgeRequestStatus Status { get; private set; }
        public DateTime? StartedAtUtc { get; private set; }
        public DateTime? CompletedAtUtc { get; private set; }
        public string Message { get; private set; }
        public string ResultJson { get; private set; }
        public List<string> CompileErrors { get; private set; }
        public string RuntimeError { get; private set; }
        public List<long> CreatedElementIds { get; private set; }
        public List<long> ModifiedElementIds { get; private set; }
        public List<long> DeletedElementIds { get; private set; }
        public long ElapsedMilliseconds { get; private set; }
        public bool RolledBack { get; private set; }
        public List<string> Warnings { get; private set; }
        public string ErrorType { get; private set; }

        public BridgeRequestRecord(
            string requestId,
            string description,
            string code,
            bool isWriteOperation,
            int timeoutSeconds,
            int confirmationTimeoutSeconds)
        {
            RequestId = requestId;
            Description = description ?? "";
            Code = code ?? "";
            IsWriteOperation = isWriteOperation;
            TimeoutSeconds = timeoutSeconds;
            CreatedAtUtc = DateTime.UtcNow;
            var total = TimeSpan.FromSeconds(timeoutSeconds + (isWriteOperation ? confirmationTimeoutSeconds : 0));
            DeadlineUtc = CreatedAtUtc.Add(total);
            Status = BridgeRequestStatus.Queued;
            CompileErrors = new List<string>();
            CreatedElementIds = new List<long>();
            ModifiedElementIds = new List<long>();
            DeletedElementIds = new List<long>();
            Warnings = new List<string>();
        }

        public bool IsTerminal
        {
            get
            {
                lock (_sync)
                {
                    return IsTerminalStatus(Status);
                }
            }
        }

        private static bool IsTerminalStatus(BridgeRequestStatus s)
        {
            return s == BridgeRequestStatus.Succeeded || s == BridgeRequestStatus.Failed ||
                   s == BridgeRequestStatus.Rejected || s == BridgeRequestStatus.Expired ||
                   s == BridgeRequestStatus.Cancelled;
        }

        /// <summary>Set a non-terminal status. No-op after the request is terminal.</summary>
        public void SetStatus(BridgeRequestStatus status, string message = null)
        {
            lock (_sync)
            {
                if (IsTerminalStatus(Status)) return;
                Status = status;
                if (message != null) Message = message;
                if (status == BridgeRequestStatus.Running && StartedAtUtc == null)
                    StartedAtUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Complete the request. No-op if already terminal.</summary>
        public void Complete(BridgeCompletionData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            lock (_sync)
            {
                if (IsTerminalStatus(Status)) return;

                if (Status == BridgeRequestStatus.Running && StartedAtUtc == null)
                    StartedAtUtc = DateTime.UtcNow;

                Status = data.Status;
                CompletedAtUtc = DateTime.UtcNow;
                Message = data.Message;
                ResultJson = data.ResultJson;
                ErrorType = data.ErrorType;
                RolledBack = data.RolledBack;
                if (data.CompileErrors != null) CompileErrors = data.CompileErrors;
                if (data.RuntimeError != null) RuntimeError = data.RuntimeError;
                if (data.CreatedElementIds != null) CreatedElementIds = data.CreatedElementIds;
                if (data.ModifiedElementIds != null) ModifiedElementIds = data.ModifiedElementIds;
                if (data.DeletedElementIds != null) DeletedElementIds = data.DeletedElementIds;
                if (data.Warnings != null) Warnings = data.Warnings;

                var started = StartedAtUtc ?? CreatedAtUtc;
                ElapsedMilliseconds = (long)((CompletedAtUtc.Value - started).TotalMilliseconds);
            }
        }

        /// <summary>
        /// Cancel while queued/waiting. Running and pending-confirmation requests
        /// cannot be cancelled by the caller. Safe to race with the executor.
        /// </summary>
        public bool TryCancel(string message, out string error)
        {
            error = null;
            lock (_sync)
            {
                if (IsTerminalStatus(Status))
                {
                    error = "Request is already in a terminal state";
                    return false;
                }
                if (Status == BridgeRequestStatus.Running)
                {
                    error = "Request is already running and cannot be cancelled";
                    return false;
                }
                if (Status == BridgeRequestStatus.PendingConfirmation)
                {
                    error = "Request is waiting for user confirmation and cannot be cancelled by the caller";
                    return false;
                }

                Status = BridgeRequestStatus.Cancelled;
                CompletedAtUtc = DateTime.UtcNow;
                Message = message ?? "Cancelled by caller before execution";
                var started = StartedAtUtc ?? CreatedAtUtc;
                ElapsedMilliseconds = (long)((CompletedAtUtc.Value - started).TotalMilliseconds);
                return true;
            }
        }

        public BridgeRequestSnapshot ToSnapshot()
        {
            lock (_sync)
            {
                object result = null;
                if (!string.IsNullOrEmpty(ResultJson))
                {
                    try
                    {
                        using (var doc = JsonDocument.Parse(ResultJson))
                            result = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
                    }
                    catch
                    {
                        result = ResultJson;
                    }
                }

                return new BridgeRequestSnapshot
                {
                    RequestId = RequestId,
                    Status = BridgeAuditLog.StatusName(Status),
                    Result = result,
                    CompileErrors = new List<string>(CompileErrors ?? new List<string>()),
                    RuntimeError = RuntimeError,
                    CreatedElementIds = new List<long>(CreatedElementIds ?? new List<long>()),
                    ModifiedElementIds = new List<long>(ModifiedElementIds ?? new List<long>()),
                    DeletedElementIds = new List<long>(DeletedElementIds ?? new List<long>()),
                    ElapsedMilliseconds = ElapsedMilliseconds,
                    RolledBack = RolledBack,
                    Warnings = new List<string>(Warnings ?? new List<string>()),
                    Message = Message,
                    ErrorType = ErrorType,
                    Description = Description,
                    IsWriteOperation = IsWriteOperation,
                    TimeoutSeconds = TimeoutSeconds,
                    CreatedAtUtc = CreatedAtUtc.ToString("o"),
                    DeadlineUtc = DeadlineUtc.ToString("o"),
                    CodeHash = BridgeAuditLog.CodeHash(Code)
                };
            }
        }
    }

    /// <summary>Immutable snapshot returned by GET /requests/{id}.</summary>
    public sealed class BridgeRequestSnapshot
    {
        public string RequestId { get; set; }
        public string Status { get; set; }
        public object Result { get; set; }
        public List<string> CompileErrors { get; set; }
        public string RuntimeError { get; set; }
        public List<long> CreatedElementIds { get; set; }
        public List<long> ModifiedElementIds { get; set; }
        public List<long> DeletedElementIds { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public bool RolledBack { get; set; }
        public List<string> Warnings { get; set; }
        public string Message { get; set; }
        public string ErrorType { get; set; }
        public string Description { get; set; }
        public bool IsWriteOperation { get; set; }
        public int TimeoutSeconds { get; set; }
        public string CreatedAtUtc { get; set; }
        public string DeadlineUtc { get; set; }
        public string CodeHash { get; set; }
    }
}
