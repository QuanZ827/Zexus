using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Zexus.Models;
using Zexus.Services;

using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Visibility = System.Windows.Visibility;

namespace Zexus.Views
{
    public partial class ChatWindow : Window
    {
        private readonly AgentService _agentService;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isProcessing;
        private Border _currentStreamingBubble;
        private TextBlock _currentStreamingText;

        // ─── Workspace State ───
        private WorkspaceState _workspaceState;
        private StackPanel _wsChainContainer;

        // ─── Design Tokens (matching reference palette) ───
        static readonly Color ColBg         = Color.FromRgb(0x08, 0x08, 0x0e);   // #08080e  background
        static readonly Color ColSurface    = Color.FromRgb(0x12, 0x12, 0x1c);   // #12121c  surface
        static readonly Color ColCard       = Color.FromRgb(0x1e, 0x1e, 0x2e);   // #1e1e2e  card
        static readonly Color ColBorder     = Color.FromRgb(0x2a, 0x2a, 0x35);   // #2a2a35  subtle border
        static readonly Color ColPrimary    = Color.FromRgb(0x63, 0x66, 0xf1);   // #6366f1  indigo
        static readonly Color ColPrimaryLt  = Color.FromRgb(0x81, 0x8c, 0xf8);   // #818cf8  indigo light
        static readonly Color ColAccent     = Color.FromRgb(0xa8, 0x55, 0xf7);   // #a855f7  purple
        static readonly Color ColSuccess    = Color.FromRgb(0x10, 0xb9, 0x81);   // #10b981  green
        static readonly Color ColWarning    = Color.FromRgb(0xf5, 0x9e, 0x0b);   // #f59e0b  amber
        static readonly Color ColError      = Color.FromRgb(0xef, 0x44, 0x44);   // #ef4444  red
        static readonly Color ColText       = Color.FromRgb(0xe2, 0xe8, 0xf0);   // #e2e8f0  primary text
        static readonly Color ColTextSec    = Color.FromRgb(0x94, 0xa3, 0xb8);   // #94a3b8  secondary text
        static readonly Color ColMuted      = Color.FromRgb(0x64, 0x74, 0x8b);   // #64748b  muted
        static readonly Color ColGlass      = Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF); // rgba(255,255,255,0.05)
        static readonly Color ColGlassBorder = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF); // rgba(255,255,255,0.08)
        static readonly Color ColCodeBg     = Color.FromRgb(0x0a, 0x0a, 0x12);   // very dark code bg

        static readonly FontFamily MainFont = new FontFamily("Segoe UI, Segoe UI Emoji");
        static readonly FontFamily MonoFont = new FontFamily("Cascadia Code, Consolas, Courier New");

        public ChatWindow()
        {
            InitializeComponent();

            _agentService = new AgentService();
            _agentService.OnStreamingText += OnStreamingTextReceived;
            _agentService.OnToolExecuting += OnToolExecuting;
            _agentService.OnStatusChanged += OnStatusChanged;
            _agentService.OnProcessingStarted += OnProcessingStarted;
            _agentService.OnToolCompleted += OnToolCompleted;
            _agentService.OnProcessingCompleted += OnProcessingCompleted;

            PositionWindow();

            _agentService.EnsureToolRegistryInitialized();

            if (!ConfigManager.IsConfigured())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowApiKeyPrompt()));
            }
        }

        private void PositionWindow()
        {
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            if (Width > screenWidth - 40) Width = screenWidth - 40;
            Left = screenWidth - Width - 20;
            Top = (screenHeight - Height) / 2;
        }

        public void UpdateDocumentContext(Autodesk.Revit.DB.Document doc)
        {
            Dispatcher.Invoke(() =>
            {
                DocumentLabel.Text = doc != null ? $"Document: {doc.Title}" : "No document loaded";
                _agentService.EnsureToolRegistryInitialized();
            });
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                DragMove();
            }
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        private void OnSend(object sender, RoutedEventArgs e) => ProcessMessage();

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                ProcessMessage();
            }
        }

        private void OnNewChat(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;

            if (MessageBox.Show("Start a new conversation?", "New Chat",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _agentService.NewSession();
                while (ChatContainer.Children.Count > 1)
                    ChatContainer.Children.RemoveAt(1);
                AddSystemMessage("New conversation started.");
                CollapseWorkspace();
                _workspaceState = null;
            }
        }

        private void OnTogglePin(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            PinBtn.Content = Topmost ? "Pinned" : "Pin";
        }

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;
            ShowSettingsDialog();
        }

        private void OnExportLog(object sender, RoutedEventArgs e)
        {
            try
            {
                var report = UsageTracker.GenerateReport();
                if (string.IsNullOrEmpty(report))
                {
                    MessageBox.Show("No usage data yet. Start using the agent and data will be collected automatically.",
                        "Export Log", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Clipboard.SetText(report);
                AddSystemMessage("Usage report copied to clipboard.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export log: {ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ProcessMessage()
        {
            var message = MessageInput.Text?.Trim();
            if (string.IsNullOrEmpty(message)) return;

            if (!ConfigManager.IsConfigured())
            {
                ShowApiKeyPrompt();
                return;
            }

            // Cancel any ongoing request before starting new one
            if (_isProcessing && _cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                if (_currentStreamingBubble != null)
                {
                    ChatContainer.Children.Remove(_currentStreamingBubble);
                    _currentStreamingBubble = null;
                    _currentStreamingText = null;
                }
                await System.Threading.Tasks.Task.Delay(100);
            }

            _agentService.EnsureToolRegistryInitialized();

            _isProcessing = true;
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                AddUserBubble(message);
                MessageInput.Text = "";
                SetStatus("Thinking...", true);
                CreateStreamingBubble();

                var response = await _agentService.ProcessMessageAsync(message, _cancellationTokenSource.Token);

                FinalizeStreamingBubble(response);
            }
            catch (OperationCanceledException)
            {
                if (_currentStreamingBubble != null)
                {
                    ChatContainer.Children.Remove(_currentStreamingBubble);
                    _currentStreamingBubble = null;
                    _currentStreamingText = null;
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Error: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
                SetStatus("", false);
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        private void OnStreamingTextReceived(string text)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_currentStreamingText != null)
                {
                    _currentStreamingText.Text += text;
                    ChatScrollViewer.ScrollToEnd();
                }
                else
                {
                    // Streaming bubble was not yet created or was removed — recreate it
                    // This prevents silent text drops during tool execution cycles
                    CreateStreamingBubble();
                    if (_currentStreamingText != null)
                    {
                        _currentStreamingText.Text = text;
                        ChatScrollViewer.ScrollToEnd();
                    }
                }
            }));
        }

        private void OnToolExecuting(string toolName, Dictionary<string, object> input)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SetStatus($"Executing: {toolName}...", true);

                if (_currentStreamingBubble != null)
                {
                    var content = _currentStreamingBubble.Child as StackPanel;
                    if (content != null)
                    {
                        // Tool execution indicator with accent color
                        var toolPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };

                        // Small colored dot
                        var dot = new Border
                        {
                            Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                            Background = new SolidColorBrush(ColPrimaryLt),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 6, 0)
                        };
                        toolPanel.Children.Add(dot);

                        var toolLabel = new TextBlock
                        {
                            Text = toolName,
                            FontSize = 11,
                            FontFamily = MainFont,
                            Foreground = new SolidColorBrush(ColPrimaryLt),
                        };
                        toolPanel.Children.Add(toolLabel);

                        content.Children.Insert(content.Children.Count - 1, toolPanel);
                    }
                }

                // ── Update Workspace thinking chain ──
                if (_workspaceState != null)
                {
                    // Mark previous active node as completed
                    var activeNode = _workspaceState.ThinkingChain.LastOrDefault(n => n.Status == ThinkingNodeStatus.Active);
                    if (activeNode != null)
                        activeNode.Status = ThinkingNodeStatus.Completed;

                    var (title, color, icon) = MapToolToThinkingNode(toolName);

                    // Extract description and code from input
                    string description = null;
                    string codeSnippet = null;
                    if (input != null)
                    {
                        if (input.ContainsKey("description"))
                            description = input["description"]?.ToString();
                        if (toolName == "ExecuteCode" && input.ContainsKey("code"))
                            codeSnippet = input["code"]?.ToString();
                    }

                    _workspaceState.ThinkingChain.Add(new ThinkingChainNode
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = title,
                        Subtitle = $"Running {toolName}...",
                        Status = ThinkingNodeStatus.Active,
                        NodeColor = color,
                        IconGlyph = icon,
                        Timestamp = DateTime.Now,
                        ToolName = toolName,
                        Description = description,
                        CodeSnippet = codeSnippet,
                        InputParams = input
                    });

                    _workspaceState.TotalExpectedSteps++;
                    RebuildThinkingChain();
                }
            }));
        }

        private void OnStatusChanged(string status)
        {
            Dispatcher.BeginInvoke(new Action(() => SetStatus(status, status != "Complete")));
        }

        private void AddUserBubble(string message)
        {
            var bubble = CreateBubble(message, true, null);
            ChatContainer.Children.Add(bubble);
            ChatScrollViewer.ScrollToEnd();
        }

        private void AddSystemMessage(string message)
        {
            var bubble = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(40, 4, 40, 10),
                Background = new SolidColorBrush(ColSurface),
                BorderBrush = new SolidColorBrush(ColGlassBorder),
                BorderThickness = new Thickness(1, 1, 1, 1),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            bubble.Child = new TextBlock
            {
                Text = message,
                FontSize = 12,
                FontFamily = MainFont,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(ColMuted),
                TextAlignment = TextAlignment.Center
            };

            ChatContainer.Children.Add(bubble);
            ChatScrollViewer.ScrollToEnd();
        }

        private Border CreateBubble(string message, bool isUser, System.Collections.Generic.List<ToolCall> toolCalls)
        {
            var bubble = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16, 14, 16, 14),
                BorderThickness = new Thickness(1, 1, 1, 1)
            };

            if (isUser)
            {
                // User bubble: primary indigo, right-aligned
                bubble.Margin = new Thickness(60, 2, 0, 10);
                bubble.HorizontalAlignment = HorizontalAlignment.Right;
                bubble.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x63, 0x66, 0xf1)); // indigo 15%
                bubble.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x63, 0x66, 0xf1)); // indigo 19%
            }
            else
            {
                // Agent bubble: glass surface, left-aligned
                bubble.Margin = new Thickness(0, 2, 60, 10);
                bubble.HorizontalAlignment = HorizontalAlignment.Left;
                bubble.Background = new SolidColorBrush(ColGlass);
                bubble.BorderBrush = new SolidColorBrush(ColGlassBorder);
            }

            var content = new StackPanel { Orientation = Orientation.Vertical };

            // Role label
            content.Children.Add(new TextBlock
            {
                Text = isUser ? "You" : "Agent",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(isUser ? ColPrimaryLt : ColAccent),
                Margin = new Thickness(0, 0, 0, 6),
                FontFamily = MainFont
            });

            // Tool call indicators
            if (toolCalls != null)
            {
                foreach (var call in toolCalls)
                {
                    var statusColor = call.Status == ToolCallStatus.Completed ? ColSuccess :
                                      call.Status == ToolCallStatus.Failed ? ColError :
                                      ColWarning;

                    var toolPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

                    // Status dot
                    toolPanel.Children.Add(new Border
                    {
                        Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                        Background = new SolidColorBrush(statusColor),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0)
                    });

                    toolPanel.Children.Add(new TextBlock
                    {
                        Text = call.Name,
                        FontSize = 11,
                        FontFamily = MainFont,
                        Foreground = new SolidColorBrush(statusColor),
                    });

                    content.Children.Add(toolPanel);
                }
            }

            // Message text
            content.Children.Add(new TextBlock
            {
                Text = message ?? "(No response)",
                FontSize = 13.5,
                FontFamily = MainFont,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(ColText),
                LineHeight = 20
            });

            bubble.Child = content;
            return bubble;
        }

        private void CreateStreamingBubble()
        {
            _currentStreamingBubble = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 2, 60, 10),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(ColGlass),
                BorderBrush = new SolidColorBrush(ColGlassBorder),
                BorderThickness = new Thickness(1)
            };

            var content = new StackPanel { Orientation = Orientation.Vertical };

            content.Children.Add(new TextBlock
            {
                Text = "Agent",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColAccent),
                Margin = new Thickness(0, 0, 0, 6),
                FontFamily = MainFont
            });

            _currentStreamingText = new TextBlock
            {
                Text = "",
                FontSize = 13.5,
                FontFamily = MainFont,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(ColText),
                LineHeight = 20
            };
            content.Children.Add(_currentStreamingText);

            _currentStreamingBubble.Child = content;
            ChatContainer.Children.Add(_currentStreamingBubble);
            ChatScrollViewer.ScrollToEnd();
        }

        private void FinalizeStreamingBubble(ChatMessage response)
        {
            if (_currentStreamingText != null && string.IsNullOrEmpty(_currentStreamingText.Text))
            {
                // Case 1: Streaming bubble was created but no text arrived — replace with final bubble
                ChatContainer.Children.Remove(_currentStreamingBubble);

                if (response.Role == MessageRole.System)
                {
                    AddSystemMessage(response.Content);
                }
                else
                {
                    var bubble = CreateBubble(response.Content, false, response.ToolCalls);
                    ChatContainer.Children.Add(bubble);
                }
            }
            else if (_currentStreamingText != null && response.ToolCalls != null && response.ToolCalls.Count > 0)
            {
                // Case 2: Has streaming text AND tool calls — replace with merged bubble
                ChatContainer.Children.Remove(_currentStreamingBubble);
                var bubble = CreateBubble(response.Content ?? _currentStreamingText.Text, false, response.ToolCalls);
                ChatContainer.Children.Add(bubble);
            }
            else if (_currentStreamingText != null)
            {
                // Case 3: Has streaming text but NO tool calls — finalize the bubble with final content
                ChatContainer.Children.Remove(_currentStreamingBubble);
                var finalContent = response.Content ?? _currentStreamingText.Text;
                var bubble = CreateBubble(finalContent, false, response.ToolCalls);
                ChatContainer.Children.Add(bubble);
            }

            _currentStreamingBubble = null;
            _currentStreamingText = null;
            ChatScrollViewer.ScrollToEnd();
        }

        private void SetStatus(string status, bool visible)
        {
            StatusText.Text = status;
            StatusBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            SendBtn.IsEnabled = !visible;
            MessageInput.IsEnabled = !visible;
        }

        private void ShowApiKeyPrompt()
        {
            var currentProvider = ConfigManager.GetProvider();

            var dialog = new Window
            {
                Title = "API Key Required",
                Width = 460, Height = 290,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize
            };

            // Outer border with glassmorphism
            var outerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x1c)),
                CornerRadius = new CornerRadius(16),
                BorderBrush = new SolidColorBrush(ColBorder),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black }
            };

            var grid = new Grid { Margin = new Thickness(24) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: provider selector
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: label
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: textbox
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3: buttons

            // ── Row 0: Provider selector ──
            var providerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            providerPanel.Children.Add(new TextBlock
            {
                Text = "Provider",
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                FontFamily = MainFont,
                Foreground = new SolidColorBrush(ColTextSec),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            var providerCombo = new ComboBox
            {
                Width = 200, Height = 32, FontSize = 13, FontFamily = MainFont,
                Background = new SolidColorBrush(ColBg),
                Foreground = new SolidColorBrush(ColText),
                BorderBrush = new SolidColorBrush(ColBorder)
            };

            // ComboBoxItem uses dark foreground for the white dropdown popup
            var dropdownFg = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x2e)); // dark text on light popup
            providerCombo.Items.Add(new ComboBoxItem { Content = LlmProviderInfo.GetDisplayName(LlmProvider.Anthropic), Tag = LlmProvider.Anthropic, Foreground = dropdownFg });
            providerCombo.Items.Add(new ComboBoxItem { Content = LlmProviderInfo.GetDisplayName(LlmProvider.OpenAI), Tag = LlmProvider.OpenAI, Foreground = dropdownFg });
            providerCombo.Items.Add(new ComboBoxItem { Content = LlmProviderInfo.GetDisplayName(LlmProvider.Google), Tag = LlmProvider.Google, Foreground = dropdownFg });

            // Select current provider
            for (int i = 0; i < providerCombo.Items.Count; i++)
            {
                if ((LlmProvider)((ComboBoxItem)providerCombo.Items[i]).Tag == currentProvider)
                {
                    providerCombo.SelectedIndex = i;
                    break;
                }
            }

            providerPanel.Children.Add(providerCombo);
            Grid.SetRow(providerPanel, 0);
            grid.Children.Add(providerPanel);

            // ── Row 1: Dynamic label ──
            var label = new TextBlock
            {
                FontSize = 14,
                FontFamily = MainFont,
                Foreground = new SolidColorBrush(ColText),
                Margin = new Thickness(0, 0, 0, 14)
            };
            var labelTitle = new System.Windows.Documents.Run(LlmProviderInfo.GetApiKeyLabel(currentProvider)) { FontWeight = FontWeights.SemiBold };
            var labelHint = new System.Windows.Documents.Run(LlmProviderInfo.GetApiKeyHint(currentProvider)) { Foreground = new SolidColorBrush(ColMuted), FontSize = 12 };
            label.Inlines.Add(labelTitle);
            label.Inlines.Add(new System.Windows.Documents.LineBreak());
            label.Inlines.Add(labelHint);
            Grid.SetRow(label, 1);
            grid.Children.Add(label);

            // Update label when provider changes
            providerCombo.SelectionChanged += (s2, ev2) =>
            {
                var selected = (LlmProvider)((ComboBoxItem)providerCombo.SelectedItem).Tag;
                labelTitle.Text = LlmProviderInfo.GetApiKeyLabel(selected);
                labelHint.Text = LlmProviderInfo.GetApiKeyHint(selected);
            };

            // ── Row 2: API key input ──
            var textBox = new TextBox
            {
                Height = 38, FontSize = 13, FontFamily = MainFont,
                Padding = new Thickness(12, 8, 12, 8),
                Background = new SolidColorBrush(ColBg),
                Foreground = new SolidColorBrush(ColText),
                BorderBrush = new SolidColorBrush(ColBorder),
                CaretBrush = new SolidColorBrush(ColText)
            };
            Grid.SetRow(textBox, 2);
            grid.Children.Add(textBox);

            // ── Row 3: Buttons ──
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var saveBtn = new Button
            {
                Content = "Save", Width = 80, Height = 34,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(ColPrimary),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontFamily = MainFont, FontSize = 13, FontWeight = FontWeights.SemiBold
            };
            saveBtn.Click += (s, ev) =>
            {
                var selectedProvider = (LlmProvider)((ComboBoxItem)providerCombo.SelectedItem).Tag;
                var key = textBox.Text?.Trim();
                if (!string.IsNullOrEmpty(key) && LlmProviderInfo.ValidateApiKey(selectedProvider, key))
                {
                    ConfigManager.SetProvider(selectedProvider);
                    ConfigManager.SetApiKey(key);
                    _agentService.InitializeClient();
                    AddSystemMessage($"{LlmProviderInfo.GetDisplayName(selectedProvider)} configured successfully.");
                    dialog.Close();
                }
                else
                {
                    MessageBox.Show(LlmProviderInfo.GetApiKeyValidationError(selectedProvider), "Invalid Key",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            buttonPanel.Children.Add(saveBtn);

            var cancelBtn = new Button
            {
                Content = "Cancel", Width = 80, Height = 34,
                Background = new SolidColorBrush(ColCard),
                Foreground = new SolidColorBrush(ColTextSec),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontFamily = MainFont, FontSize = 13
            };
            cancelBtn.Click += (s, ev) => dialog.Close();
            buttonPanel.Children.Add(cancelBtn);

            Grid.SetRow(buttonPanel, 3);
            grid.Children.Add(buttonPanel);

            outerBorder.Child = grid;
            dialog.Content = outerBorder;

            // Allow dragging the dialog
            outerBorder.MouseLeftButtonDown += (s, ev) => { try { dialog.DragMove(); } catch { } };

            dialog.ShowDialog();
        }

        private void ShowSettingsDialog()
        {
            var currentProvider = ConfigManager.GetProvider();
            var providerName = LlmProviderInfo.GetDisplayName(currentProvider);
            var currentKey = ConfigManager.GetApiKey();
            var maskedKey = !string.IsNullOrEmpty(currentKey)
                ? $"{currentKey.Substring(0, Math.Min(10, currentKey.Length))}...{currentKey.Substring(Math.Max(0, currentKey.Length - 4))}"
                : "Not set";

            if (MessageBox.Show($"Provider: {providerName}\nModel: {ConfigManager.GetModel()}\nAPI Key: {maskedKey}\n\nChange settings?", "Settings",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                ShowApiKeyPrompt();
            }
        }

        #region Workspace Panel

        private void OnProcessingStarted(string userMessage)
        {
            Dispatcher.BeginInvoke(new Action(() => ShowWorkspace(userMessage)));
        }

        private void OnToolCompleted(string toolName, ToolResult result, long durationMs)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_workspaceState == null) return;

                // Mark active node as completed or failed, and store rich data
                var activeNode = _workspaceState.ThinkingChain.LastOrDefault(n => n.Status == ThinkingNodeStatus.Active);
                if (activeNode != null)
                {
                    activeNode.Status = result.Success ? ThinkingNodeStatus.Completed : ThinkingNodeStatus.Failed;
                    activeNode.Output = result.Message;
                    activeNode.ResultData = result.Data;
                    activeNode.DurationMs = durationMs;
                    activeNode.Subtitle = result.Message;

                    if (!result.Success)
                        activeNode.ErrorMessage = result.Message;
                }

                _workspaceState.CompletedSteps++;
                RebuildThinkingChain();
            }));
        }

        private void OnProcessingCompleted(ChatMessage response)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_workspaceState == null) return;

                foreach (var node in _workspaceState.ThinkingChain)
                    if (node.Status == ThinkingNodeStatus.Active)
                        node.Status = ThinkingNodeStatus.Completed;

                _workspaceState.ThinkingChain.Add(new ThinkingChainNode
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = "Task Complete",
                    Subtitle = "Done",
                    Status = ThinkingNodeStatus.Completed,
                    NodeColor = ColSuccess,
                    IconGlyph = "✓",
                    Timestamp = DateTime.Now,
                    ToolName = "_complete"
                });

                _workspaceState.IsActive = false;
                RebuildThinkingChain();
            }));
        }

        private void ShowWorkspace(string taskName)
        {
            _workspaceState = new WorkspaceState
            {
                TaskName = taskName.Length > 80 ? taskName.Substring(0, 80) + "..." : taskName,
                IsActive = true,
                StartTime = DateTime.Now,
                CompletedSteps = 0,
                TotalExpectedSteps = 0
            };

            _workspaceState.ThinkingChain.Add(new ThinkingChainNode
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Understand Intent",
                Subtitle = taskName.Length > 80 ? taskName.Substring(0, 80) + "..." : taskName,
                Status = ThinkingNodeStatus.Active,
                NodeColor = Color.FromRgb(0xec, 0x48, 0x99),
                IconGlyph = "◉",
                Timestamp = DateTime.Now,
                ToolName = "_intent"
            });

            BuildWorkspaceUI();

            WorkspaceColumnDef.Width = new GridLength(420);
            WorkspaceSplitter.Visibility = Visibility.Visible;
            WorkspacePanel.Visibility = Visibility.Visible;

            if (ActualWidth < 800) Width = 1040;
        }

        private void CollapseWorkspace()
        {
            WorkspaceColumnDef.Width = new GridLength(0);
            WorkspaceSplitter.Visibility = Visibility.Collapsed;
            WorkspacePanel.Visibility = Visibility.Collapsed;
        }

        private void BuildWorkspaceUI()
        {
            WorkspaceContainer.Children.Clear();

            // ── Section Header ──
            var header = new TextBlock
            {
                Text = "THINKING CHAIN",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColAccent),
                Margin = new Thickness(4, 4, 0, 10),
                FontFamily = MainFont
            };
            WorkspaceContainer.Children.Add(header);

            // ── Thinking Chain Container ──
            _wsChainContainer = new StackPanel();
            RebuildThinkingChain();
            WorkspaceContainer.Children.Add(_wsChainContainer);
        }

        private void RebuildThinkingChain()
        {
            if (_wsChainContainer == null || _workspaceState == null) return;
            _wsChainContainer.Children.Clear();

            for (int i = 0; i < _workspaceState.ThinkingChain.Count; i++)
            {
                var node = _workspaceState.ThinkingChain[i];
                bool isLast = (i == _workspaceState.ThinkingChain.Count - 1);

                var card = BuildThinkingNodeCard(node, isLast);
                _wsChainContainer.Children.Add(card);
            }
        }

        /// <summary>
        /// Builds a rich card for a single thinking chain node.
        /// </summary>
        private FrameworkElement BuildThinkingNodeCard(ThinkingChainNode node, bool isLast)
        {
            var wrapper = new StackPanel();

            // ── Status color ──
            var statusColor = node.Status == ThinkingNodeStatus.Completed ? ColSuccess
                : node.Status == ThinkingNodeStatus.Active ? node.NodeColor
                : node.Status == ThinkingNodeStatus.Failed ? ColError
                : ColMuted;

            // ── Card border ──
            var card = new Border
            {
                Background = new SolidColorBrush(ColGlass),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 0)
            };

            if (node.Status == ThinkingNodeStatus.Failed)
                card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, ColError.R, ColError.G, ColError.B));
            else if (node.Status == ThinkingNodeStatus.Active)
                card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, node.NodeColor.R, node.NodeColor.G, node.NodeColor.B));
            else
                card.BorderBrush = new SolidColorBrush(ColGlassBorder);

            var content = new StackPanel();

            // ── Row 1: Status circle + Title + Duration ──
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Status circle
            var circle = new Border
            {
                Width = 18, Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(0x28, statusColor.R, statusColor.G, statusColor.B)),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (node.Status == ThinkingNodeStatus.Active)
            {
                circle.BorderBrush = new SolidColorBrush(statusColor);
                circle.BorderThickness = new Thickness(1.5);
            }
            circle.Child = new TextBlock
            {
                Text = node.Status == ThinkingNodeStatus.Completed ? "✓"
                     : node.Status == ThinkingNodeStatus.Failed ? "✗"
                     : node.IconGlyph ?? "?",
                FontSize = 8, FontFamily = MainFont,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(statusColor)
            };
            Grid.SetColumn(circle, 0);
            headerGrid.Children.Add(circle);

            // Title
            var titleText = new TextBlock
            {
                Text = node.Title,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(node.Status == ThinkingNodeStatus.Pending ? ColMuted : ColText),
                FontFamily = MainFont,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            Grid.SetColumn(titleText, 1);
            headerGrid.Children.Add(titleText);

            // Duration badge
            if (node.DurationMs > 0)
            {
                var durationStr = node.DurationMs >= 1000
                    ? $"{node.DurationMs / 1000.0:F1}s"
                    : $"{node.DurationMs}ms";

                var durationBadge = new TextBlock
                {
                    Text = durationStr,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(ColMuted),
                    FontFamily = MainFont,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(durationBadge, 2);
                headerGrid.Children.Add(durationBadge);
            }

            content.Children.Add(headerGrid);

            // ── Row 2: Description (if present) ──
            if (!string.IsNullOrEmpty(node.Description))
            {
                content.Children.Add(new TextBlock
                {
                    Text = node.Description,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColTextSec),
                    FontFamily = MainFont,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24, 4, 0, 0)
                });
            }

            // ── Row 3: Input params summary (for non-ExecuteCode tools) ──
            if (node.ToolName != "ExecuteCode" && node.ToolName != "_intent" && node.ToolName != "_complete"
                && node.InputParams != null && node.InputParams.Count > 0)
            {
                var paramStr = FormatInputParams(node.ToolName, node.InputParams);
                if (!string.IsNullOrEmpty(paramStr))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = paramStr,
                        FontSize = 10.5,
                        Foreground = new SolidColorBrush(ColMuted),
                        FontFamily = MainFont,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(24, 3, 0, 0)
                    });
                }
            }

            // ── Row 4: Code block (ExecuteCode only) ──
            if (node.ToolName == "ExecuteCode" && !string.IsNullOrEmpty(node.CodeSnippet))
            {
                var codeBlock = BuildCodeBlock(node.CodeSnippet);
                codeBlock.Margin = new Thickness(4, 6, 0, 0);
                content.Children.Add(codeBlock);
            }

            // ── Row 5: Output/Result ──
            if (node.Status == ThinkingNodeStatus.Completed && !string.IsNullOrEmpty(node.Output)
                && node.ToolName != "_intent" && node.ToolName != "_complete")
            {
                var resultPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 6, 0, 0) };
                resultPanel.Children.Add(new TextBlock
                {
                    Text = "✓ ",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColSuccess),
                    FontFamily = MainFont,
                    VerticalAlignment = VerticalAlignment.Top
                });
                resultPanel.Children.Add(new TextBlock
                {
                    Text = node.Output,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColTextSec),
                    FontFamily = MainFont,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 340
                });
                content.Children.Add(resultPanel);
            }

            // ── Row 5b: Error message (failed nodes) ──
            if (node.Status == ThinkingNodeStatus.Failed && !string.IsNullOrEmpty(node.ErrorMessage))
            {
                var errorPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 6, 0, 0) };
                errorPanel.Children.Add(new TextBlock
                {
                    Text = "✗ ",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColError),
                    FontFamily = MainFont,
                    VerticalAlignment = VerticalAlignment.Top
                });
                errorPanel.Children.Add(new TextBlock
                {
                    Text = node.ErrorMessage.Length > 200 ? node.ErrorMessage.Substring(0, 200) + "..." : node.ErrorMessage,
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(ColError),
                    FontFamily = MonoFont,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 340
                });
                content.Children.Add(errorPanel);
            }

            // ── Active status subtitle ──
            if (node.Status == ThinkingNodeStatus.Active)
            {
                content.Children.Add(new TextBlock
                {
                    Text = node.Subtitle ?? "Processing...",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColMuted),
                    FontFamily = MainFont,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(24, 4, 0, 0)
                });
            }

            card.Child = content;
            wrapper.Children.Add(card);

            // ── Connecting line ──
            if (!isLast)
            {
                var line = new Border
                {
                    Width = 2, Height = 12,
                    Background = new SolidColorBrush(ColBorder),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(16, 0, 0, 0)
                };
                wrapper.Children.Add(line);
            }

            return wrapper;
        }

        /// <summary>
        /// Builds a monospace code block with dark background.
        /// </summary>
        private Border BuildCodeBlock(string code)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(ColCodeBg),
                BorderBrush = new SolidColorBrush(ColBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8)
            };

            border.Child = new TextBlock
            {
                Text = code,
                FontSize = 10.5,
                FontFamily = MonoFont,
                Foreground = new SolidColorBrush(ColTextSec),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16
            };

            return border;
        }

        /// <summary>
        /// Extracts the most meaningful input parameters for a one-line summary.
        /// </summary>
        private string FormatInputParams(string toolName, Dictionary<string, object> input)
        {
            if (input == null || input.Count == 0) return null;

            var parts = new List<string>();
            try
            {
                switch (toolName)
                {
                    case "SearchElements":
                        if (input.ContainsKey("category")) parts.Add($"category: {input["category"]}");
                        if (input.ContainsKey("filter_param")) parts.Add($"filter: {input["filter_param"]} {(input.ContainsKey("filter_operator") ? input["filter_operator"] : "=")} {(input.ContainsKey("filter_value") ? input["filter_value"] : "")}");
                        break;

                    case "SetElementParameter":
                        if (input.ContainsKey("element_id")) parts.Add($"id: {input["element_id"]}");
                        if (input.ContainsKey("parameter_name")) parts.Add($"param: {input["parameter_name"]}");
                        if (input.ContainsKey("value")) parts.Add($"value: {input["value"]}");
                        break;

                    case "GetParameterValues":
                        if (input.ContainsKey("category")) parts.Add($"category: {input["category"]}");
                        if (input.ContainsKey("parameter_name")) parts.Add($"param: {input["parameter_name"]}");
                        break;

                    case "CreateProjectParameter":
                        if (input.ContainsKey("parameter_name")) parts.Add($"name: {input["parameter_name"]}");
                        if (input.ContainsKey("data_type")) parts.Add($"type: {input["data_type"]}");
                        if (input.ContainsKey("binding")) parts.Add($"binding: {input["binding"]}");
                        break;

                    case "AddScheduleField":
                        if (input.ContainsKey("schedule_name")) parts.Add($"schedule: {input["schedule_name"]}");
                        if (input.ContainsKey("mode")) parts.Add($"mode: {input["mode"]}");
                        if (input.ContainsKey("field_name")) parts.Add($"field: {input["field_name"]}");
                        break;

                    case "ModifyScheduleFilter":
                        if (input.ContainsKey("schedule_name")) parts.Add($"schedule: {input["schedule_name"]}");
                        if (input.ContainsKey("mode")) parts.Add($"mode: {input["mode"]}");
                        if (input.ContainsKey("field_name")) parts.Add($"field: {input["field_name"]}");
                        if (input.ContainsKey("operator")) parts.Add($"op: {input["operator"]}");
                        break;

                    case "ModifyScheduleSort":
                        if (input.ContainsKey("schedule_name")) parts.Add($"schedule: {input["schedule_name"]}");
                        if (input.ContainsKey("mode")) parts.Add($"mode: {input["mode"]}");
                        if (input.ContainsKey("field_name")) parts.Add($"field: {input["field_name"]}");
                        break;

                    case "SelectElements":
                    case "IsolateElements":
                        if (input.ContainsKey("element_ids"))
                        {
                            var ids = input["element_ids"];
                            if (ids is System.Collections.ICollection col)
                                parts.Add($"{col.Count} elements");
                            else
                                parts.Add($"elements: {ids}");
                        }
                        if (input.ContainsKey("mode")) parts.Add($"mode: {input["mode"]}");
                        break;

                    case "GetModelOverview":
                        parts.Add("full model scan");
                        break;

                    case "GetWarnings":
                        if (input.ContainsKey("category")) parts.Add($"category: {input["category"]}");
                        break;

                    case "ListSheets":
                    case "ListViews":
                        // Minimal params
                        break;

                    case "PrintSheets":
                        if (input.ContainsKey("sheet_numbers")) parts.Add($"sheets: {input["sheet_numbers"]}");
                        if (input.ContainsKey("output_path")) parts.Add($"→ {input["output_path"]}");
                        break;

                    case "ExportDocument":
                        if (input.ContainsKey("format")) parts.Add($"format: {input["format"]}");
                        if (input.ContainsKey("output_folder")) parts.Add($"→ {input["output_folder"]}");
                        break;

                    default:
                        // Generic: show first 2 key-value pairs
                        foreach (var kv in input.Take(2))
                        {
                            if (kv.Key != "description")
                            {
                                var val = kv.Value?.ToString() ?? "";
                                if (val.Length > 30) val = val.Substring(0, 30) + "...";
                                parts.Add($"{kv.Key}: {val}");
                            }
                        }
                        break;
                }
            }
            catch { }

            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }


        // ── Tool name → thinking chain display mapping ──
        private (string title, Color color, string icon) MapToolToThinkingNode(string toolName)
        {
            switch (toolName)
            {
                case "GetModelOverview": return ("Query Model", Color.FromRgb(0x63, 0x66, 0xf1), "Q");
                case "SearchElements": return ("Search Elements", Color.FromRgb(0x63, 0x66, 0xf1), "S");
                case "GetParameterValues": return ("Query Parameters", Color.FromRgb(0x63, 0x66, 0xf1), "P");
                case "GetSelection": return ("Read Selection", Color.FromRgb(0x63, 0x66, 0xf1), "R");
                case "GetWarnings": return ("Check Warnings", Color.FromRgb(0xf5, 0x9e, 0x0b), "W");
                case "SelectElements": return ("Select Elements", Color.FromRgb(0xa8, 0x55, 0xf7), "H");
                case "IsolateElements": return ("Isolate View", Color.FromRgb(0xa8, 0x55, 0xf7), "I");
                case "SetElementParameter": return ("Set Parameter", Color.FromRgb(0xf5, 0x9e, 0x0b), "W");
                case "CreateProjectParameter": return ("Create Param", Color.FromRgb(0xf5, 0x9e, 0x0b), "P");
                case "AddScheduleField": return ("Schedule Field", Color.FromRgb(0xa8, 0x55, 0xf7), "F");
                case "ModifyScheduleFilter": return ("Schedule Filter", Color.FromRgb(0xa8, 0x55, 0xf7), "F");
                case "ModifyScheduleSort": return ("Schedule Sort", Color.FromRgb(0xa8, 0x55, 0xf7), "S");
                case "ExecuteCode": return ("Execute Code", Color.FromRgb(0xf5, 0x9e, 0x0b), "X");
                case "ListSheets": return ("List Sheets", Color.FromRgb(0x10, 0xb9, 0x81), "S");
                case "ListViews": return ("List Views", Color.FromRgb(0x10, 0xb9, 0x81), "V");
                case "PrintSheets": return ("Print PDF", Color.FromRgb(0x10, 0xb9, 0x81), "P");
                case "ExportDocument": return ("Export", Color.FromRgb(0x10, 0xb9, 0x81), "E");
                default: return ("Execute", Color.FromRgb(0x94, 0xa3, 0xb8), "?");
            }
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            _agentService?.Dispose();
            base.OnClosed(e);
        }
    }
}
