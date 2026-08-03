using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace Zexus.Services.Bridge
{
    /// <summary>Ribbon command that shows bridge status (URL, token file, audit log).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class BridgeStatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var status = BridgeHost.StatusSummary();

            var dialog = new TaskDialog("Zexus Bridge")
            {
                MainInstruction = status.StartsWith("Bridge is running")
                    ? "Zexus Bridge is running"
                    : "Zexus Bridge is NOT running",
                MainContent = status,
                FooterText = "Full access applies only to this Revit session. Restarting Revit resets Ask approval."
            };

            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                "Ask approval", "Show a confirmation dialog before every model write.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                "Full access", "Automatically approve model writes for this Revit session.");
            dialog.CommonButtons = TaskDialogCommonButtons.Close;

            var selected = dialog.Show();
            if (selected == TaskDialogResult.CommandLink1)
            {
                BridgeApprovalSettings.SetMode(BridgeApprovalMode.AskApproval);
                TaskDialog.Show("Zexus Bridge", "Approval mode is now Ask approval.");
            }
            else if (selected == TaskDialogResult.CommandLink2)
            {
                var warning = new TaskDialog("Enable Full Access?")
                {
                    MainInstruction = "Allow automatic Revit model writes for this session?",
                    MainContent =
                        "MCP client requests will execute without individual approval dialogs. " +
                        "Safety scanning, loopback authentication, audit logging, and ExternalEvent dispatch remain enabled.",
                    MainIcon = TaskDialogIcon.TaskDialogIconWarning,
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                    FooterText = "This permission is cleared automatically when Revit closes."
                };
                if (warning.Show() == TaskDialogResult.Yes)
                {
                    BridgeApprovalSettings.SetMode(BridgeApprovalMode.FullAccess);
                    TaskDialog.Show("Zexus Bridge", "Full access is enabled for this Revit session.");
                }
            }

            return Result.Succeeded;
        }
    }
}
