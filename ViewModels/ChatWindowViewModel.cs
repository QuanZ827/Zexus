using System;
using Zexus.Models;
using Zexus.Services;

namespace Zexus.ViewModels
{
    /// <summary>
    /// Top-level ViewModel. Owns Chat (Step 2), TitleBar + Sidebar (Step 3),
    /// RightPanel (Step 5).
    /// </summary>
    public class ChatWindowViewModel : ViewModelBase
    {
        public ChatViewModel Chat { get; }
        public TitleBarViewModel TitleBar { get; }
        public SidebarViewModel Sidebar { get; }
        public RightPanelViewModel RightPanel { get; }

        public ChatWindowViewModel(
            AgentService agentService,
            Action newChatAction = null,
            Action settingsAction = null,
            Action exportAction = null,
            Action<bool> pinAction = null,
            Action themeAction = null,
            Action<OutputRecord> navigateAction = null,
            Action<OutputRecord> undoAction = null,
            Action<string> warningClickAction = null)
        {
            Chat = new ChatViewModel(agentService);
            TitleBar = new TitleBarViewModel();
            Sidebar = new SidebarViewModel(
                newChatAction: newChatAction ?? (() => Chat.NewChatCommand.Execute(null)),
                settingsAction: settingsAction,
                exportAction: exportAction,
                pinAction: pinAction,
                themeAction: themeAction);
            RightPanel = new RightPanelViewModel(
                onNavigate: navigateAction,
                onUndoRecord: undoAction,
                onWarningClicked: warningClickAction);
        }

        /// <summary>
        /// Passthrough from ChatWindow.xaml.cs (called by App.cs on document open/close).
        /// Pre-extracted strings keep this VM Revit-API-free.
        /// </summary>
        public void UpdateTitleBarDocument(string docName, string modelLocation,
            string modelLocationTooltip, string modelHub)
        {
            TitleBar.UpdateDocument(docName, modelLocation, modelLocationTooltip, modelHub);
        }
    }
}
