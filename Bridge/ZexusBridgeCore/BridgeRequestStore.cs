using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Zexus.Bridge
{
    /// <summary>Thread-safe store of bridge requests, keyed by request id.</summary>
    public sealed class BridgeRequestStore
    {
        private readonly ConcurrentDictionary<string, BridgeRequestRecord> _records =
            new ConcurrentDictionary<string, BridgeRequestRecord>(StringComparer.Ordinal);

        public BridgeRequestRecord Create(
            string description,
            string code,
            bool isWriteOperation,
            int timeoutSeconds,
            int confirmationTimeoutSeconds)
        {
            var id = "req_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var record = new BridgeRequestRecord(
                id, description, code, isWriteOperation, timeoutSeconds, confirmationTimeoutSeconds);
            _records[id] = record;
            return record;
        }

        public bool TryGet(string requestId, out BridgeRequestRecord record)
        {
            return _records.TryGetValue(requestId, out record);
        }

        /// <summary>Number of requests that still occupy a queue slot.</summary>
        public int CountActive()
        {
            int count = 0;
            foreach (var kv in _records)
            {
                if (!kv.Value.IsTerminal) count++;
            }
            return count;
        }

        public bool TryCancel(string requestId, out string error)
        {
            error = null;
            if (!_records.TryGetValue(requestId, out var record))
            {
                error = "Unknown request id";
                return false;
            }
            return record.TryCancel("Cancelled by caller before execution", out error);
        }

        public IReadOnlyCollection<BridgeRequestRecord> Snapshot()
        {
            return _records.Values.ToList();
        }
    }
}
