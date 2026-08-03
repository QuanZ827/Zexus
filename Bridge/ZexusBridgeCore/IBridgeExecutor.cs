namespace Zexus.Bridge
{
    /// <summary>
    /// Abstraction over "run this request against the target application".
    /// In Zexus this is implemented by RevitBridgeExecutor, which enqueues the
    /// record into the Revit ExternalEvent queue so all API access happens on
    /// the Revit UI thread. Tests use a fake executor.
    /// </summary>
    public interface IBridgeExecutor
    {
        /// <summary>True when a document is open (cached; never touches the target UI thread).</summary>
        bool IsDocumentOpen { get; }

        /// <summary>Short human-readable detail for /health (e.g. Revit version, queue state).</summary>
        string HealthDetail { get; }

        /// <summary>
        /// Hand the record to the target application. The implementation updates the
        /// record status and completes it with results. Must not block.
        /// </summary>
        void Submit(BridgeRequestRecord record);
    }
}
