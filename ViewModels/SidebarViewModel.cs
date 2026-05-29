using System;
using CommunityToolkit.Mvvm.Input;

namespace Zexus.ViewModels
{
    /// <summary>
    /// 56px sidebar icon rail. Commands are injected from <see cref="ChatWindowViewModel"/>
    /// — most delegate back to code-behind for now (Window.Topmost, ThemeManager.Toggle,
    /// MessageBox-based settings dialog). Step 7 reorganizes per the Stitch layout
    /// (Export → Right Panel, Theme/Pin → Settings flyout).
    /// </summary>
    public class SidebarViewModel : ViewModelBase
    {
        public IRelayCommand NewChatCommand { get; }
        public IRelayCommand SettingsCommand { get; }
        public IRelayCommand ExportCommand { get; }
        public IRelayCommand PinCommand { get; }
        public IRelayCommand ThemeCommand { get; }

        private bool _isPinned;
        public bool IsPinned
        {
            get => _isPinned;
            set => SetProperty(ref _isPinned, value);
        }

        private bool _isDarkTheme = true;
        public bool IsDarkTheme
        {
            get => _isDarkTheme;
            set => SetProperty(ref _isDarkTheme, value);
        }

        private int _activeAlerts;
        public int ActiveAlerts
        {
            get => _activeAlerts;
            set => SetProperty(ref _activeAlerts, value);
        }

        public SidebarViewModel(
            Action newChatAction,
            Action settingsAction,
            Action exportAction,
            Action<bool> pinAction,
            Action themeAction)
        {
            // Sidebar New Chat = confirm + Chat clear + workspace/output cleanup
            // (assembled in ChatWindow.xaml.cs). Use Chat.NewChatCommand directly when
            // you want a no-prompt clear.
            NewChatCommand = new RelayCommand(() => newChatAction?.Invoke());
            SettingsCommand = new RelayCommand(() => settingsAction?.Invoke());
            ExportCommand = new RelayCommand(() => exportAction?.Invoke());

            PinCommand = new RelayCommand(() =>
            {
                IsPinned = !IsPinned;
                pinAction?.Invoke(IsPinned);
            });

            ThemeCommand = new RelayCommand(() =>
            {
                IsDarkTheme = !IsDarkTheme;
                themeAction?.Invoke();
            });
        }
    }
}
