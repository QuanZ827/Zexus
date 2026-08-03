using System;
using Zexus.Bridge;

namespace Zexus.Services.Bridge
{
    /// <summary>
    /// IBridgeExecutor implementation for Revit: hands bridge requests to the
    /// queue-based RevitEventHandler so they run on the Revit UI thread.
    /// </summary>
    public sealed class RevitBridgeExecutor : IBridgeExecutor
    {
        public bool IsDocumentOpen => App.IsDocumentOpen;

        public string HealthDetail =>
            "Revit " + App.RevitVersion +
            (App.IsDocumentOpen ? ", document open" : ", no document open") +
            ", approval mode " + BridgeApprovalSettings.Mode;

        public void Submit(BridgeRequestRecord record)
        {
            if (App.RevitEventHandler == null || App.RevitExternalEvent == null)
            {
                record.Complete(new BridgeCompletionData
                {
                    Status = BridgeRequestStatus.Failed,
                    Message = "Revit event handler not initialized (plugin may not be loaded)",
                    ErrorType = "submit"
                });
                return;
            }

            var request = new RevitRequest
            {
                Type = RevitRequestType.BridgeExecute,
                BridgeRecord = record
            };

            App.RevitEventHandler.EnqueueRevitRequest(request);
        }
    }
}
