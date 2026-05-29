using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Zexus.Views.Controls
{
    /// <summary>
    /// Title bar chrome (logo + name + version + doc context + minimize/maximize/close).
    /// Window-chrome event handlers live in code-behind because they manipulate the
    /// parent <see cref="Window"/> directly — pure UI, no MVVM benefit.
    /// </summary>
    public partial class TitleBarControl : UserControl
    {
        public TitleBarControl()
        {
            InitializeComponent();
            LoadLogoIcon();
            Loaded += OnLoaded;
        }

        private Window ParentWindow => Window.GetWindow(this);

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var w = ParentWindow;
            if (w != null)
            {
                w.StateChanged += (s, _) => UpdateMaxRestoreGlyph(w.WindowState);
                UpdateMaxRestoreGlyph(w.WindowState);
            }
        }

        private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximizeRestore();
                return;
            }
            try { ParentWindow?.DragMove(); }
            catch (InvalidOperationException) { /* Already in drag — ignore */ }
        }

        private void OnMinimize(object sender, RoutedEventArgs e)
        {
            var w = ParentWindow;
            if (w != null) w.WindowState = WindowState.Minimized;
        }

        private void OnMaximizeRestore(object sender, RoutedEventArgs e) => ToggleMaximizeRestore();

        private void OnClose(object sender, RoutedEventArgs e) => ParentWindow?.Close();

        private void ToggleMaximizeRestore()
        {
            var w = ParentWindow;
            if (w == null) return;
            w.WindowState = w.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void UpdateMaxRestoreGlyph(WindowState state)
        {
            // 0xE922 = Maximize glyph, 0xE923 = Restore glyph (Segoe MDL2 Assets)
            MaxRestoreBtn.Content = state == WindowState.Maximized ? "" : "";
            MaxRestoreBtn.ToolTip = state == WindowState.Maximized ? "Restore" : "Maximize";
        }

        /// <summary>Load embedded PNG for the title bar logo (icon_32 → 20×20 display).</summary>
        private void LoadLogoIcon()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("Zexus.Resources.icon_32.png"))
                {
                    if (stream == null) return;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                    bmp.Freeze();
                    LogoImage.Source = bmp;
                }
            }
            catch (IOException) { /* Resource missing — fail silently */ }
            catch (Exception) { /* Decoder error — fail silently */ }
        }
    }
}
