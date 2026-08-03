using System.Threading;

namespace Zexus.Services.Bridge
{
    public enum BridgeApprovalMode
    {
        AskApproval = 0,
        FullAccess = 1
    }

    /// <summary>
    /// In-memory approval mode for the current Revit process. It is deliberately
    /// not persisted, so every Revit restart returns to AskApproval.
    /// </summary>
    public static class BridgeApprovalSettings
    {
        private static int _mode = (int)BridgeApprovalMode.AskApproval;

        public static BridgeApprovalMode Mode =>
            (BridgeApprovalMode)Volatile.Read(ref _mode);

        public static bool IsFullAccess => Mode == BridgeApprovalMode.FullAccess;

        public static void SetMode(BridgeApprovalMode mode)
        {
            Interlocked.Exchange(ref _mode, (int)mode);
            ZexusLogger.Info("[Bridge] approval mode changed to " + mode);
        }

        public static void Reset()
        {
            SetMode(BridgeApprovalMode.AskApproval);
        }
    }
}
