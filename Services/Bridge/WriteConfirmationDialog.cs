using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Zexus.Bridge;

namespace Zexus.Services.Bridge
{
    public enum ConfirmationResult
    {
        Approved,
        Denied,
        TimedOut
    }

    /// <summary>
    /// Modal write-confirmation dialog shown on the Revit UI thread. Only the Revit
    /// user can approve a write operation; the external MCP client cannot.
    /// Auto-denies after the bridge confirmation timeout.
    /// </summary>
    public sealed class WriteConfirmationDialog
    {
        private readonly BridgeRequestRecord _record;
        private readonly string _extraMessage;
        private readonly int _timeoutSeconds;
        private DispatcherTimer _timer;
        private ConfirmationResult _result = ConfirmationResult.Denied;
        private int _secondsLeft;
        private TextBlock _countdownText;

        private WriteConfirmationDialog(BridgeRequestRecord record, string extraMessage)
        {
            _record = record;
            _extraMessage = extraMessage;
            _timeoutSeconds = Math.Max(10, BridgeHostConfirmationTimeout());
        }

        private static int BridgeHostConfirmationTimeout()
        {
            // Default 120s; env override for testing.
            var env = Environment.GetEnvironmentVariable("ZEXUS_BRIDGE_CONFIRM_TIMEOUT_SEC");
            return int.TryParse(env, out int v) && v > 0 ? v : 120;
        }

        public static ConfirmationResult Show(BridgeRequestRecord record, string extraMessage)
        {
            var dialog = new WriteConfirmationDialog(record, extraMessage);
            return dialog.Run();
        }

        private ConfirmationResult Run()
        {
            var window = BuildWindow();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) =>
            {
                _secondsLeft--;
                if (_countdownText != null)
                {
                    _countdownText.Text = _secondsLeft > 0
                        ? "Auto-deny in " + _secondsLeft + "s"
                        : "Auto-deny in 0s";
                }
                if (_secondsLeft <= 0)
                {
                    _result = ConfirmationResult.TimedOut;
                    _timer.Stop();
                    window.Close();
                }
            };
            _secondsLeft = _timeoutSeconds;
            _timer.Start();

            window.ShowDialog();
            _timer.Stop();
            return _result;
        }

        private Window BuildWindow()
        {
            var window = new Window
            {
                Title = "Zexus Bridge - Confirm Write Operation",
                Width = 720,
                Height = 560,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = Brushes.White,
                Foreground = Brushes.Black
            };

            var root = new DockPanel { Margin = new Thickness(16) };

            var header = new TextBlock
            {
                Text = "An AI agent wants to MODIFY your Revit model.",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x2A, 0x2A)),
                TextWrapping = TextWrapping.Wrap
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            if (!string.IsNullOrEmpty(_extraMessage))
            {
                var policyNote = new TextBlock
                {
                    Text = _extraMessage,
                    Margin = new Thickness(0, 8, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.DarkOrange
                };
                DockPanel.SetDock(policyNote, Dock.Top);
                root.Children.Add(policyNote);
            }

            var descLabel = new TextBlock
            {
                Text = "Description:",
                Margin = new Thickness(0, 12, 0, 4),
                FontWeight = FontWeights.SemiBold
            };
            DockPanel.SetDock(descLabel, Dock.Top);
            root.Children.Add(descLabel);

            var descBox = new TextBox
            {
                Text = string.IsNullOrEmpty(_record.Description) ? "(no description)" : _record.Description,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                MaxHeight = 48,
                Padding = new Thickness(6)
            };
            DockPanel.SetDock(descBox, Dock.Top);
            root.Children.Add(descBox);

            var codeLabel = new TextBlock
            {
                Text = "Code summary:",
                Margin = new Thickness(0, 8, 0, 4),
                FontWeight = FontWeights.SemiBold
            };
            DockPanel.SetDock(codeLabel, Dock.Top);
            root.Children.Add(codeLabel);

            var codePreview = new TextBox
            {
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(6),
                Text = TruncateCode(_record.Code, 2400)
            };
            root.Children.Add(codePreview);

            var buttonBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            DockPanel.SetDock(buttonBar, Dock.Bottom);
            root.Children.Add(buttonBar);

            _countdownText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0),
                Foreground = Brushes.Gray
            };
            buttonBar.Children.Add(_countdownText);

            var denyButton = new Button
            {
                Content = "Deny",
                Width = 100,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0)
            };
            denyButton.Click += (s, e) =>
            {
                _result = ConfirmationResult.Denied;
                window.Close();
            };
            buttonBar.Children.Add(denyButton);

            var approveButton = new Button
            {
                Content = "Approve & Execute",
                Width = 150,
                Height = 32,
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x7A, 0x2A)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold
            };
            approveButton.Click += (s, e) =>
            {
                _result = ConfirmationResult.Approved;
                window.Close();
            };
            buttonBar.Children.Add(approveButton);

            window.Content = root;
            return window;
        }

        private static string TruncateCode(string code, int max)
        {
            if (string.IsNullOrEmpty(code)) return "(no code)";
            return code.Length <= max ? code : code.Substring(0, max) + "\n... (truncated)";
        }
    }
}
