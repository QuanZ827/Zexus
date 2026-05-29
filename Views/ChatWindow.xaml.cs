using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Zexus.Models;
using Zexus.Services;
using Zexus.ViewModels;

using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Visibility = System.Windows.Visibility;
using SysProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Zexus.Views
{
    public partial class ChatWindow : Window
    {
        private readonly AgentService _agentService;
        // Step 2: streaming bubble is now a MessageViewModel owned by ChatViewModel.
        // Auto-scroll tracking — only stay-pinned-to-bottom when user is already there.
        private bool _autoScrollAtBottom = true;

        // Step 4: image paste, MessageInput field, _cancellationTokenSource, _isProcessing
        // all moved to ChatViewModel / InputBarControl. Use Chat.IsProcessing to check state.

        // Step 6: Workspace Panel removed — inline status on agent bubbles instead.
        // Inline Confirmation Panel still uses ChatContainer (set/cleared by Show/HideConfirmationButtons).
        private StackPanel _confirmationPanel;

        // Output records — backing list for MapToolResultToOutputRecord's record-merging dedup logic.
        // The visible list is in RightPanelViewModel.OutputRecords; both stay in sync.
        private readonly List<OutputRecord> _outputRecords = new List<OutputRecord>();

        // Journal totals (running sum across the session) — Right Panel footer reads these.
        private int _journalTxnCount;
        private int _journalTotalCreated;
        private int _journalTotalModified;
        private int _journalTotalDeleted;

        // ─── Design Tokens — forwarded from ThemeManager (Dark/Light) ───
        static Color ColBg         => ThemeManager.ColBg;
        static Color ColSurface    => ThemeManager.ColSurface;
        static Color ColCard       => ThemeManager.ColCard;
        static Color ColBorder     => ThemeManager.ColBorder;
        static Color ColPrimary    => ThemeManager.ColPrimary;
        static Color ColPrimaryLt  => ThemeManager.ColPrimaryLt;
        static Color ColAccent     => ThemeManager.ColAccent;
        static Color ColSuccess    => ThemeManager.ColSuccess;
        static Color ColWarning    => ThemeManager.ColWarning;
        static Color ColError      => ThemeManager.ColError;
        static Color ColText       => ThemeManager.ColText;
        static Color ColTextSec    => ThemeManager.ColTextSec;
        static Color ColMuted      => ThemeManager.ColMuted;
        static Color ColGlass      => ThemeManager.ColGlass;
        static Color ColGlassBorder => ThemeManager.ColGlassBorder;
        static Color ColCodeBg     => ThemeManager.ColCodeBg;

        static readonly FontFamily MainFont = new FontFamily("Google Sans, Segoe UI Variable, Segoe UI, Segoe UI Emoji");
        static readonly FontFamily MonoFont = new FontFamily("Cascadia Code, Consolas, Courier New");

        public ChatWindow()
        {
            // Step 7: register M3 brush resources BEFORE the XAML loads so {DynamicResource}
            // bindings inside MessageTemplates / OutputCardTemplate / the UserControls
            // resolve on first paint.
            ThemeManager.EnsureBrushResources();

            InitializeComponent();

            _agentService = new AgentService();

            // ChatViewModel subscribes to OnStreamingText + OnProcessingCompleted internally
            // and drives the chat bubble MessageViewModels. Code-behind keeps subscriptions
            // for the workspace panel + output cards + status bar (Steps 5-6 migrate those).
            // Sidebar commands route back to private methods on this window — keeps the
            // MessageBox/Window.Topmost/ThemeManager side effects out of the VM.
            var windowVm = new ChatWindowViewModel(
                _agentService,
                newChatAction: NewChatRequested,
                settingsAction: SettingsRequested,
                exportAction: ExportRequested,
                pinAction: pinned => Topmost = pinned,
                themeAction: () => ThemeManager.Toggle(),
                navigateAction: record => Dispatcher.Invoke(() => OnOutputRecordClicked(record)),
                undoAction: record => Dispatcher.Invoke(() => OnRightPanelUndo(record)),
                warningClickAction: desc => Dispatcher.Invoke(() => AutoSendWarningQuery(desc)));

            // ChatViewModel.SendInternalAsync invokes these hooks around each send.
            // BeforeSend: hide pending confirmation panel + abort early if API key is missing.
            // AfterSend: re-display the confirmation panel if the assistant's reply asks for one.
            windowVm.Chat.BeforeSendAction = () =>
            {
                HideConfirmationButtons();
                if (!ConfigManager.IsConfigured())
                {
                    ShowApiKeyPrompt();
                    return false;
                }
                return true;
            };
            windowVm.Chat.AfterSendAction = response =>
            {
                var textToCheck = response?.Content;
                if (!string.IsNullOrEmpty(textToCheck) && IsConfirmationRequest(textToCheck))
                    Dispatcher.Invoke(ShowConfirmationButtons);
            };

            DataContext = windowVm;

            _agentService.OnToolExecuting += OnToolExecuting;
            _agentService.OnStatusChanged += OnStatusChanged;
            _agentService.OnToolCompleted += OnToolCompleted;
            // OnProcessingStarted / OnProcessingCompleted / OnReasoningForThinkingChain were
            // workspace-panel hooks in earlier versions; ChatViewModel owns them now (Step 6).

            // Step 4: image paste + key handling moved to InputBarControl.xaml.cs.

            PositionWindow();
            ThemeManager.ThemeChanged += () => Dispatcher.Invoke(ApplyTheme);

            // Apply theme after visual tree is ready (XAML defaults to dark-mode colors;
            // this ensures Light mode is applied if saved in config, and sets toggle icon)
            Loaded += (s, e) => ApplyTheme();

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

        /// <summary>
        /// Re-apply theme to the structural shell elements that aren't bound to
        /// DynamicResource brushes (outer gradient + ambient base). All templated
        /// content (bubbles, cards, controls) auto-updates via {DynamicResource}, so
        /// the old whole-tree RemapVisualTree walk was removed in Step 8 — it actively
        /// fought the bindings by overwriting bound brushes with one-off SolidColorBrush.
        /// </summary>
        private void ApplyTheme()
        {
            var isDark = ThemeManager.Current == ThemeMode.Dark;

            // Sidebar shows the sun/moon icon — sync IsDarkTheme so it stays correct
            // across explicit ThemeManager.SetTheme calls (not just the sidebar button).
            if (DataContext is ChatWindowViewModel vm && vm.Sidebar != null)
                vm.Sidebar.IsDarkTheme = isDark;

            // Main background base
            MainBgBorder.Background = new SolidColorBrush(ThemeManager.Surface);

            // Outer shell gradient border (light-leak frame)
            var outerGrad = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 1) };
            if (isDark)
            {
                outerGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 0x4D, 0x8E, 0xFE), 0));
                outerGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 0.5));
                outerGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x20, 0xE1, 0x4E, 0xF6), 1));
            }
            else
            {
                outerGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 0x1A, 0x73, 0xE8), 0));
                outerGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, 0xD8, 0xDA, 0xE0), 0.5));
                outerGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x20, 0x6C, 0x47, 0xFF), 1));
            }
            OuterShellBorder.Background = outerGrad;

            // Status bar + Selection inspector surfaces (other surfaces own their theme
            // via DynamicResource on the sub-controls).
            StatusBar.Background = new SolidColorBrush(ThemeManager.GlassPanel);
            SelectionInspectorBar.Background = new SolidColorBrush(ThemeManager.GlassPanel);
        }

        public void UpdateDocumentContext(Autodesk.Revit.DB.Document doc, Services.ModelBriefing briefing = null)
        {
            Dispatcher.Invoke(() =>
            {
                string docName = null, modelLoc = null, modelLocTooltip = null, modelHub = null;
                if (doc != null)
                {
                    docName = doc.Title;

                    string pathName = null;
                    try { pathName = doc.PathName; } catch (Exception ex) { Services.ZexusLogger.Warn($"PathName access: {ex.Message}"); }
                    if (!string.IsNullOrEmpty(pathName))
                    {
                        modelLoc = TruncatePath(pathName, 50);
                        modelLocTooltip = pathName;
                    }

                    modelHub = GetCloudInfo(doc);
                }

                var windowVm = DataContext as ChatWindowViewModel;

                // Push doc strings into the TitleBar VM (binding refreshes the UI).
                windowVm?.UpdateTitleBarDocument(docName, modelLoc, modelLocTooltip, modelHub);

                // Step 5: Right Panel renders Model Health; pass briefing (null → "no document").
                windowVm?.RightPanel?.UpdateFromBriefing(briefing);

                if (doc == null)
                    SelectionInspectorBar.Visibility = Visibility.Collapsed;

                _agentService.EnsureToolRegistryInitialized();
            });
        }

        private void AutoSendWarningQuery(string warningDescription)
        {
            var chat = (DataContext as ChatWindowViewModel)?.Chat;
            if (chat == null || chat.IsProcessing) return;
            chat.InputText = $"Select and highlight elements with this warning: \"{warningDescription}\"";
            if (chat.SendCommand.CanExecute(null)) chat.SendCommand.Execute(null);
        }

        /// <summary>
        /// Called from App.OnIdling when the Revit selection changes.
        /// </summary>
        public void UpdateSelectionInspector(string selectionSummary)
        {
            Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrEmpty(selectionSummary))
                {
                    SelectionInspectorBar.Visibility = Visibility.Collapsed;
                    SelectionInfoText.Text = "";
                }
                else
                {
                    SelectionInfoText.Text = selectionSummary;
                    SelectionInspectorBar.Visibility = Visibility.Visible;
                }
            });
        }

        private static string TruncatePath(string path, int maxLength)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
                return path;

            var parts = path.Replace('/', '\\').Split('\\');
            if (parts.Length <= 2)
                return "..." + path.Substring(path.Length - Math.Min(path.Length, maxLength - 3));

            var result = parts[parts.Length - 1];
            for (int i = parts.Length - 2; i >= 0; i--)
            {
                var candidate = parts[i] + "\\" + result;
                if (candidate.Length > maxLength - 3)
                {
                    result = "...\\" + result;
                    break;
                }
                result = candidate;
            }
            return result;
        }

        private static string GetCloudInfo(Autodesk.Revit.DB.Document doc)
        {
            try
            {
                if (doc.IsModelInCloud)
                {
                    var pathName = doc.PathName;
                    if (!string.IsNullOrEmpty(pathName))
                    {
                        if (pathName.Contains("BIM 360://")) return "BIM 360";
                        if (pathName.Contains("ACC://")) return "ACC";
                    }
                    return "Cloud";
                }
                return "Local";
            }
            catch { return null; }
        }

        // \u2500\u2500 Window chrome (drag/min/max/close), logo loading, and title bar chrome buttons
        //    moved to Views/Controls/TitleBarControl in Step 3. Theme/pin/new-chat/export
        //    moved to SidebarControl with action delegates in the ChatWindowViewModel ctor.

        // Step 4: OnSend, OnInputKeyDown, OnInputPreviewKeyDown, ConvertToPngBytes,
        // UpdateImagePreview, ProcessMessage, _pendingImages, MAX_IMAGES_PER_MESSAGE
        // all moved to Views/Controls/InputBarControl. Send is now triggered by
        // ChatViewModel.SendCommand (button) or InputBarControl's Enter handler.


        // Sidebar commands route here. Theme + pin are inline lambdas in the
        // ChatWindowViewModel ctor (one-liners); the rest are private methods below.

        private void NewChatRequested()
        {
            var windowVm = DataContext as ChatWindowViewModel;
            if (windowVm?.Chat?.IsProcessing == true) return;

            if (MessageBox.Show("Start a new conversation?", "New Chat",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                windowVm?.Chat?.NewChatCommand?.Execute(null);

                _outputRecords.Clear();
                _journalTxnCount = 0;
                _journalTotalCreated = 0;
                _journalTotalModified = 0;
                _journalTotalDeleted = 0;

                windowVm?.RightPanel?.ClearAll();
            }
        }

        /// <summary>
        /// Step 5: dispatcher invoked by OutputRecordViewModel.UndoCommand. Routes to the
        /// existing per-RecordType undo handler (parameter / schedule / view-modified / schedule-delete).
        /// Each handler refreshes the OutputRecord in the Right Panel VM when it completes.
        /// </summary>
        private void OnRightPanelUndo(OutputRecord record)
        {
            if (record == null) return;
            switch (record.RecordType)
            {
                case OutputRecordType.ParameterSet:
                    OnUndoAllParameterChanges(record);
                    break;
                case OutputRecordType.ScheduleModified:
                    OnUndoAllScheduleChanges(record);
                    break;
                case OutputRecordType.ViewModified:
                    OnUndoViewModified(record);
                    break;
                case OutputRecordType.ViewCreated:
                case OutputRecordType.ScheduleCreated:
                    // Delete-schedule semantics for created records.
                    OnDeleteSchedule(record);
                    break;
            }
        }

        private void SettingsRequested()
        {
            if ((DataContext as ChatWindowViewModel)?.Chat?.IsProcessing == true) return;
            ShowSettingsDialog();
        }

        private void ExportRequested()
        {
            try
            {
                // Reports root: %AppData%\Zexus\reports\
                var reportsRoot = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Zexus", "reports");
                if (!System.IO.Directory.Exists(reportsRoot))
                    System.IO.Directory.CreateDirectory(reportsRoot);

                var reporter = SessionReporter.Instance;
                if (reporter.HasData)
                {
                    // Save current session + copy to clipboard
                    var content = reporter.GetReportContent();
                    var filePath = reporter.GenerateReport();

                    if (!string.IsNullOrEmpty(content))
                        Clipboard.SetText(content);

                    if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                    {
                        // Open Explorer and select the just-exported file
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                        AddSystemMessage("Session report exported and copied to clipboard.");
                        return;
                    }
                }

                // No current session data (or save failed) — open the reports folder
                System.Diagnostics.Process.Start("explorer.exe", reportsRoot);
                AddSystemMessage("Opened reports folder — browse previous sessions here.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export report: {ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ProcessMessage was deleted in Step 4. Send is now triggered directly via
        // ChatViewModel.SendCommand (button binding) or InputBarControl's Enter handler.
        // Pre-/post-send hooks (HideConfirmationButtons, API-key prompt, IsConfirmationRequest
        // → ShowConfirmationButtons) are wired up via ChatViewModel.BeforeSendAction /
        // AfterSendAction in the ChatWindow constructor.

        private void OnToolExecuting(string toolName, Dictionary<string, object> input)
        {
            // Tool indicators + thinking-chain nodes are driven by ChatViewModel.
            // Code-behind only owns the status-bar text now.
            Dispatcher.BeginInvoke(new Action(() => SetStatus($"Executing: {toolName}...", true)));
        }

        // Step 6: OnReasoningForThinkingChain handled by ChatViewModel; this hook is gone.

        private void OnStatusChanged(string status)
        {
            Dispatcher.BeginInvoke(new Action(() => SetStatus(status, !status.StartsWith("Complete"))));
        }

        /// <summary>
        /// Adds a system-role bubble to the chat. Now a thin passthrough to ChatViewModel
        /// — bubble layout/styling lives in MessageTemplates.xaml.
        /// </summary>
        private void AddSystemMessage(string message)
        {
            (DataContext as ChatWindowViewModel)?.Chat?.AddSystemNotification(message);
        }

        /// <summary>
        /// Auto-scrolls the chat viewer to the bottom when content grows (new bubble
        /// arrives, or streaming text expands the current one) — but only if the user
        /// was already pinned to the bottom. Lets the user scroll up to read history
        /// without being yanked back.
        /// </summary>
        private void ChatScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Manual vertical scroll → update the pinned flag.
            if (e.ExtentHeightChange == 0 && e.VerticalChange != 0)
            {
                _autoScrollAtBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 1;
                return;
            }

            // Content grew (new message or streaming append) and user was at the bottom.
            if (e.ExtentHeightChange > 0 && _autoScrollAtBottom)
            {
                ChatScrollViewer.ScrollToVerticalOffset(e.ExtentHeight);
            }
        }

        private void SetStatus(string status, bool visible)
        {
            StatusText.Text = status;
            StatusBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            // Input box / send button are bound to ChatViewModel.IsInputEnabled — disabling
            // them here would race the binding. Visibility flag above is enough.
        }

        /// <summary>
        /// Create a custom dark dropdown control (replaces ComboBox which can't be dark-themed in Revit's WPF host).
        /// Returns: (container Border, populate action, getSelectedTag func, setSelected action).
        /// </summary>
        private (Border container, Action<List<KeyValuePair<string, object>>> populate, Func<object> getTag, Action<int> select)
            CreateDarkDropdown(double width, double height, Action<object> onSelectionChanged)
        {
            var darkBg = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x22));
            var darkBgHover = new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x32));
            var textBrush = new SolidColorBrush(ColText);
            var borderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

            object selectedTag = null;

            // Selected item text
            var selectedText = new TextBlock
            {
                Text = "",
                FontSize = 13, FontFamily = MainFont,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                IsHitTestVisible = false
            };

            // Arrow ▼
            var arrow = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse("M 0 0 L 4 4 L 8 0 Z"),
                Fill = textBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                IsHitTestVisible = false
            };

            // Popup items container
            var itemsPanel = new StackPanel();
            var popupBorder = new Border
            {
                Background = darkBg,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                MinWidth = width,
                MaxHeight = 300,
                Child = itemsPanel
            };

            var popup = new System.Windows.Controls.Primitives.Popup
            {
                StaysOpen = false,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                AllowsTransparency = true,
                Child = popupBorder
            };

            // Root container
            var rootGrid = new Grid();
            rootGrid.Children.Add(selectedText);
            rootGrid.Children.Add(arrow);
            rootGrid.Children.Add(popup);

            var container = new Border
            {
                Width = width, Height = height,
                Background = darkBg,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                Child = rootGrid
            };

            popup.PlacementTarget = container;

            // Track popup-just-closed to prevent immediate reopen on same click gesture
            bool dropdownJustClosed = false;
            popup.Closed += (s, e) =>
            {
                dropdownJustClosed = true;
                Dispatcher.BeginInvoke(new Action(() => dropdownJustClosed = false),
                    System.Windows.Threading.DispatcherPriority.Input);
            };

            // MouseDown: block event to prevent dialog DragMove from capturing the mouse
            container.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
            };

            // MouseUp: open popup (avoids StaysOpen=false auto-closing on the same Up event)
            container.MouseLeftButtonUp += (s, e) =>
            {
                if (!dropdownJustClosed)
                    popup.IsOpen = true;
                e.Handled = true;
            };

            // Populate: store tag in Border.Tag for easy retrieval
            void PopulateEnhanced(List<KeyValuePair<string, object>> items)
            {
                itemsPanel.Children.Clear();
                foreach (var item in items)
                {
                    var display = item.Key;
                    var tag = item.Value;

                    var itemText = new TextBlock
                    {
                        Text = display,
                        FontSize = 13, FontFamily = MainFont,
                        Foreground = textBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(10, 0, 10, 0),
                        IsHitTestVisible = false
                    };

                    var itemBorder = new Border
                    {
                        Height = 30,
                        Background = darkBg,
                        Cursor = Cursors.Hand,
                        Tag = tag,
                        Child = itemText
                    };

                    itemBorder.MouseEnter += (s, e) => itemBorder.Background = darkBgHover;
                    itemBorder.MouseLeave += (s, e) => itemBorder.Background = darkBg;
                    itemBorder.MouseLeftButtonUp += (s, e) =>
                    {
                        selectedText.Text = display;
                        selectedTag = tag;
                        popup.IsOpen = false;
                        onSelectionChanged?.Invoke(tag);
                        e.Handled = true;
                    };

                    itemsPanel.Children.Add(itemBorder);
                }
            }

            void SelectByIndex(int index)
            {
                if (index >= 0 && index < itemsPanel.Children.Count)
                {
                    var itemBorder = (Border)itemsPanel.Children[index];
                    var txt = (TextBlock)itemBorder.Child;
                    selectedText.Text = txt.Text;
                    selectedTag = itemBorder.Tag;
                }
            }

            return (container, PopulateEnhanced, () => selectedTag, SelectByIndex);
        }

        private void ShowApiKeyPrompt()
        {
            var currentProvider = ConfigManager.GetProvider();
            var currentModel = ConfigManager.GetModel();

            var dialog = new Window
            {
                Title = "API Key Required",
                Width = 460, Height = 340,
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
                Background = new SolidColorBrush(ColBg),
                CornerRadius = new CornerRadius(16),
                BorderBrush = new SolidColorBrush(ColBorder),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black }
            };

            var grid = new Grid { Margin = new Thickness(24) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: provider selector
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: model selector
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: label
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3: textbox
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4: buttons

            // ── Row 0: Provider selector (custom dark dropdown) ──
            var providerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            providerPanel.Children.Add(new TextBlock
            {
                Text = "Provider",
                Width = 60,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                FontFamily = MainFont,
                Foreground = new SolidColorBrush(ColTextSec),
                VerticalAlignment = VerticalAlignment.Center
            });

            // Forward-declare model dropdown parts (needed in provider's onSelectionChanged)
            Action<List<KeyValuePair<string, object>>> populateModel = null;
            Action<int> selectModel = null;
            Func<object> getModelTag = null;

            // Label refs (updated when provider changes)
            System.Windows.Documents.Run labelTitle = null;
            System.Windows.Documents.Run labelHint = null;

            // Helper: build model items list for a provider, returning the selected index
            (List<KeyValuePair<string, object>> items, int selectedIdx) BuildModelItems(LlmProvider provider, string selectModelId)
            {
                var models = LlmProviderInfo.GetAvailableModels(provider);
                var items = new List<KeyValuePair<string, object>>();
                int selectedIdx = 0;
                for (int i = 0; i < models.Count; i++)
                {
                    items.Add(new KeyValuePair<string, object>(models[i].Value, models[i].Key));
                    if (models[i].Key.Equals(selectModelId, StringComparison.OrdinalIgnoreCase))
                        selectedIdx = i;
                }
                return (items, selectedIdx);
            }

            var (providerDropdown, populateProvider, getProviderTag, selectProvider) = CreateDarkDropdown(200, 32, tag =>
            {
                var selected = (LlmProvider)tag;
                if (labelTitle != null) labelTitle.Text = LlmProviderInfo.GetApiKeyLabel(selected);
                if (labelHint != null) labelHint.Text = LlmProviderInfo.GetApiKeyHint(selected);
                // Repopulate model dropdown
                var (modelItems, modelIdx) = BuildModelItems(selected, LlmProviderInfo.GetDefaultModel(selected));
                populateModel?.Invoke(modelItems);
                selectModel?.Invoke(modelIdx);
            });

            // Populate providers
            var providerItems = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>(LlmProviderInfo.GetDisplayName(LlmProvider.Anthropic), LlmProvider.Anthropic),
                new KeyValuePair<string, object>(LlmProviderInfo.GetDisplayName(LlmProvider.OpenAI), LlmProvider.OpenAI),
                new KeyValuePair<string, object>(LlmProviderInfo.GetDisplayName(LlmProvider.Google), LlmProvider.Google),
            };
            populateProvider(providerItems);

            // Select current provider
            for (int i = 0; i < providerItems.Count; i++)
            {
                if ((LlmProvider)providerItems[i].Value == currentProvider) { selectProvider(i); break; }
            }

            providerPanel.Children.Add(providerDropdown);
            Grid.SetRow(providerPanel, 0);
            grid.Children.Add(providerPanel);

            // ── Row 1: Model selector (custom dark dropdown) ──
            var modelPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            modelPanel.Children.Add(new TextBlock
            {
                Text = "Model",
                Width = 60,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                FontFamily = MainFont,
                Foreground = new SolidColorBrush(ColTextSec),
                VerticalAlignment = VerticalAlignment.Center
            });

            Border modelDropdown;
            (modelDropdown, populateModel, getModelTag, selectModel) = CreateDarkDropdown(200, 32, tag => { /* model selected */ });

            // Populate models for current provider
            var (initModelItems, initModelIdx) = BuildModelItems(currentProvider, currentModel);
            populateModel(initModelItems);
            selectModel(initModelIdx);

            modelPanel.Children.Add(modelDropdown);
            Grid.SetRow(modelPanel, 1);
            grid.Children.Add(modelPanel);

            // ── Row 2: Dynamic label ──
            var label = new TextBlock
            {
                FontSize = 14,
                FontFamily = MainFont,
                Foreground = new SolidColorBrush(ColText),
                Margin = new Thickness(0, 0, 0, 14)
            };
            labelTitle = new System.Windows.Documents.Run(LlmProviderInfo.GetApiKeyLabel(currentProvider)) { FontWeight = FontWeights.SemiBold };
            labelHint = new System.Windows.Documents.Run(LlmProviderInfo.GetApiKeyHint(currentProvider)) { Foreground = new SolidColorBrush(ColMuted), FontSize = 12 };
            label.Inlines.Add(labelTitle);
            label.Inlines.Add(new System.Windows.Documents.LineBreak());
            label.Inlines.Add(labelHint);
            Grid.SetRow(label, 2);
            grid.Children.Add(label);

            // ── Row 3: API key input ──
            var textBox = new TextBox
            {
                Height = 38, FontSize = 13, FontFamily = MainFont,
                Padding = new Thickness(12, 8, 12, 8),
                Background = new SolidColorBrush(ColBg),
                Foreground = new SolidColorBrush(ColText),
                BorderBrush = new SolidColorBrush(ColBorder),
                CaretBrush = new SolidColorBrush(ColText)
            };
            Grid.SetRow(textBox, 3);
            grid.Children.Add(textBox);

            // ── Row 4: Buttons ──
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
                var providerTag = getProviderTag();
                var modelTag = getModelTag();
                var selectedProvider = providerTag != null ? (LlmProvider)providerTag : currentProvider;
                var selectedModel = modelTag?.ToString() ?? LlmProviderInfo.GetDefaultModel(selectedProvider);
                var key = textBox.Text?.Trim();
                if (!string.IsNullOrEmpty(key) && LlmProviderInfo.ValidateApiKey(selectedProvider, key))
                {
                    ConfigManager.SetProvider(selectedProvider);
                    ConfigManager.SetModel(selectedModel);
                    ConfigManager.SetApiKey(key);
                    _agentService.InitializeClient();
                    var modelName = LlmProviderInfo.GetModelDisplayName(selectedProvider, selectedModel);
                    AddSystemMessage($"{LlmProviderInfo.GetDisplayName(selectedProvider)} ({modelName}) configured successfully.");
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

            Grid.SetRow(buttonPanel, 4);
            grid.Children.Add(buttonPanel);

            outerBorder.Child = grid;
            dialog.Content = outerBorder;

            // Allow dragging the dialog
            outerBorder.MouseLeftButtonDown += (s, ev) => { try { dialog.DragMove(); } catch { /* DragMove fails if mouse not pressed — expected */ } };

            dialog.ShowDialog();
        }

        private void ShowSettingsDialog()
        {
            var currentProvider = ConfigManager.GetProvider();
            var providerName = LlmProviderInfo.GetDisplayName(currentProvider);
            var modelName = LlmProviderInfo.GetModelDisplayName(currentProvider, ConfigManager.GetModel());
            var currentKey = ConfigManager.GetApiKey();
            var maskedKey = !string.IsNullOrEmpty(currentKey)
                ? $"{currentKey.Substring(0, Math.Min(10, currentKey.Length))}...{currentKey.Substring(Math.Max(0, currentKey.Length - 4))}"
                : "Not set";

            if (MessageBox.Show($"Provider: {providerName}\nModel: {modelName}\nAPI Key: {maskedKey}\n\nChange settings?", "Settings",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                ShowApiKeyPrompt();
            }
        }

        private void OnToolCompleted(string toolName, ToolResult result, long durationMs)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Step 6: Workspace ThinkingChain handled by ChatViewModel via OnToolCompleted
                // wiring on MessageViewModel.ThinkingNodes. Code-behind only updates Right Panel.

                // ── Generate Output Record (always — Journal Entry is now produced alongside) ──
                var outputRecord = MapToolResultToOutputRecord(toolName, result);
                if (outputRecord != null)
                    (DataContext as ChatWindowViewModel)?.RightPanel?.AddOutputRecord(outputRecord);

                // ── Generate Transaction Journal Entry (write-only ExecuteCode) ──
                if (toolName == "ExecuteCode" && result.Success)
                {
                    var journalEntry = BuildJournalEntry(result);
                    if (journalEntry != null && journalEntry.IsWrite)
                    {
                        _journalTxnCount++;
                        _journalTotalCreated += journalEntry.CreatedCount;
                        _journalTotalModified += journalEntry.ModifiedCount;
                        _journalTotalDeleted += journalEntry.DeletedCount;
                        (DataContext as ChatWindowViewModel)?.RightPanel?.SetJournalStats(
                            _journalTxnCount, _journalTotalCreated, _journalTotalModified, _journalTotalDeleted);
                    }

                    // Auto-navigate if exactly 1 new view was created (zero LLM dependency)
                    if (result.Data.ContainsKey("sys_created")
                        && result.Data["sys_created"] is List<ElementDetail> createdForNav)
                    {
                        var newViews = createdForNav.Where(e => e.IsView).ToList();
                        if (newViews.Count == 1)
                            _ = App.RevitEventHandler.NavigateToViewAsync(newViews[0].ElementId);
                    }
                }
            }));
        }

        // Step 6: OnProcessingStarted / OnProcessingCompleted were workspace-only handlers;
        // ChatViewModel finalizes the streaming message and AddSendAction hooks the rest.

        // ── Step 6: Inline Confirmation Buttons ──

        private bool IsConfirmationRequest(string assistantMessage)
        {
            if (string.IsNullOrEmpty(assistantMessage)) return false;
            var lower = assistantMessage.ToLower();
            var patterns = new[]
            {
                "请确认", "确认是否", "是否执行", "是否继续",
                "shall i proceed", "should i continue", "do you want me to",
                "proceed?", "go ahead?",
                "是否执行此操作", "请在回复中告诉我"
            };
            return patterns.Any(p => lower.Contains(p));
        }

        private void ShowConfirmationButtons()
        {
            HideConfirmationButtons();

            _confirmationPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 8)
            };

            var yesBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x5A, 0x27)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 12, 0),
                Cursor = Cursors.Hand
            };
            yesBtn.Child = new TextBlock
            {
                Text = "✅ 确认执行",
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = MainFont,
                FontWeight = FontWeights.Medium
            };
            yesBtn.MouseLeftButtonDown += (s, e) =>
            {
                HideConfirmationButtons();
                SendMessageProgrammatically("确认");
            };
            yesBtn.MouseEnter += (s, e) => yesBtn.Background = new SolidColorBrush(Color.FromRgb(0x3D, 0x7A, 0x37));
            yesBtn.MouseLeave += (s, e) => yesBtn.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x5A, 0x27));

            var noBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x27, 0x27)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 8, 16, 8),
                Cursor = Cursors.Hand
            };
            noBtn.Child = new TextBlock
            {
                Text = "❌ 取消",
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = MainFont,
                FontWeight = FontWeights.Medium
            };
            noBtn.MouseLeftButtonDown += (s, e) =>
            {
                HideConfirmationButtons();
                SendMessageProgrammatically("取消，不要执行");
            };
            noBtn.MouseEnter += (s, e) => noBtn.Background = new SolidColorBrush(Color.FromRgb(0x7A, 0x37, 0x37));
            noBtn.MouseLeave += (s, e) => noBtn.Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x27, 0x27));

            _confirmationPanel.Children.Add(yesBtn);
            _confirmationPanel.Children.Add(noBtn);

            ChatContainer.Children.Add(_confirmationPanel);
            ChatScrollViewer.ScrollToEnd();
        }

        private void HideConfirmationButtons()
        {
            if (_confirmationPanel != null)
            {
                ChatContainer.Children.Remove(_confirmationPanel);
                _confirmationPanel = null;
            }
        }

        private void SendMessageProgrammatically(string message)
        {
            HideConfirmationButtons();
            var chat = (DataContext as ChatWindowViewModel)?.Chat;
            if (chat == null) return;
            chat.InputText = message;
            if (chat.SendCommand.CanExecute(null)) chat.SendCommand.Execute(null);
        }


        #region Output Preview Panel

        /// <summary>
        /// Only creates output records for NEW artifacts added to the model or exported files.
        /// Edits, queries, and navigation are NOT tracked here — they belong in the thinking chain.
        /// </summary>
        private OutputRecord MapToolResultToOutputRecord(string toolName, ToolResult result)
        {
            if (!result.Success) return null;
            var data = result.Data;

            switch (toolName)
            {
                // ── New view created ──
                case "CreateSchedule":
                {
                    long? viewId = null;
                    if (data != null && data.TryGetValue("schedule_id", out var sid))
                        viewId = ConvertToLong(sid);

                    string catName = data != null && data.TryGetValue("category", out var c) ? c?.ToString() : "";
                    string schedName = data != null && data.TryGetValue("schedule_name", out var sn) ? sn?.ToString() : "Schedule";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ScheduleCreated,
                        Title = schedName,
                        Subtitle = $"Created \u2022 {catName}",
                        IconGlyph = "\U0001F4CA",
                        IconColor = ColSuccess,
                        ToolName = toolName,
                        ViewId = viewId,
                        ScheduleId = viewId, // same as schedule_id from tool result
                        ScheduleName = schedName,
                        Data = data
                    };
                }

                // ── New view created (3D, Section, Elevation, FloorPlan, etc.) ──
                case "CreateView":
                {
                    long? cvViewId = null;
                    if (data != null && data.TryGetValue("view_id", out var cvid))
                        cvViewId = ConvertToLong(cvid);

                    string cvViewName = data != null && data.TryGetValue("view_name", out var cvn) ? cvn?.ToString() : "View";
                    string cvViewType = data != null && data.TryGetValue("view_type", out var cvt) ? cvt?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewCreated,
                        Title = cvViewName,
                        Subtitle = $"Created \u2022 {cvViewType}",
                        IconGlyph = "\U0001F441",
                        IconColor = ColSuccess,
                        ToolName = toolName,
                        ViewId = cvViewId,
                        Data = data
                    };
                }

                // ── View visibility changed (category show/hide) ──
                case "SetCategoryVisibility":
                {
                    string visMode = data != null && data.TryGetValue("mode", out var vm) ? vm?.ToString() : "";
                    string viewName = data != null && data.TryGetValue("view", out var vn) ? vn?.ToString() : "View";
                    bool isTemp = data != null && data.TryGetValue("temporary", out var tmpVal) && Convert.ToBoolean(tmpVal);
                    string persistLabel = isTemp ? "Temp" : "Permanent";

                    // "reset" is itself the undo action — no card needed (it restores default state)
                    if (visMode == "reset") return null;

                    // Build a readable subtitle
                    var catShown = ExtractStringList(data, "categories_shown") ?? ExtractStringList(data, "categories");
                    string catLabel = catShown != null && catShown.Count > 0
                        ? string.Join(", ", catShown.Take(3)) + (catShown.Count > 3 ? $" +{catShown.Count - 3}" : "")
                        : "";
                    string visSub = visMode == "show_only" ? $"Show only \u2022 {catLabel} \u2022 {persistLabel}"
                           : visMode == "hide" ? $"Hidden \u2022 {catLabel} \u2022 {persistLabel}"
                           : $"Visible \u2022 {catLabel} \u2022 {persistLabel}";

                    // Find the parent ViewCreated/ScheduleCreated record that owns this view
                    string parentId = FindParentRecordId(viewName);

                    // Undo = call SetCategoryVisibility with mode="reset" and same temporary flag
                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewModified,
                        Title = viewName,
                        Subtitle = visSub,
                        IconGlyph = "\U0001F441",
                        IconColor = ColAccent,
                        ToolName = toolName,
                        ParentRecordId = parentId,
                        Data = data,
                        CanUndoRecord = true,
                        UndoToolName = "SetCategoryVisibility",
                        UndoData = new Dictionary<string, object>
                        {
                            ["mode"] = "reset",
                            ["temporary"] = isTemp
                        }
                    };
                }

                // ── New parameter created ──
                case "CreateProjectParameter":
                {
                    string paramName = data != null && data.TryGetValue("parameter_name", out var pn) ? pn?.ToString() : "Parameter";
                    string paramType = data != null && data.TryGetValue("parameter_type", out var pt) ? pt?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ParameterCreated,
                        Title = paramName,
                        Subtitle = $"Created \u2022 {paramType}",
                        IconGlyph = "\u270F",
                        IconColor = ColWarning,
                        ToolName = toolName,
                        Data = data
                    };
                }

                // ── File exported ──
                case "ExportDocument":
                {
                    string format = data != null && data.TryGetValue("format", out var f) ? f?.ToString() : "File";
                    string folder = data != null && data.TryGetValue("output_folder", out var fo) ? fo?.ToString() : null;
                    var files = ExtractFileList(data, "output_files");
                    int count = files?.Count ?? 0;

                    string firstFile = files != null && files.Count > 0 ? files[0] : null;
                    if (firstFile != null && folder != null && !System.IO.Path.IsPathRooted(firstFile))
                        firstFile = System.IO.Path.Combine(folder, firstFile);

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.FileExported,
                        Title = $"{format} Export",
                        Subtitle = count == 1 ? System.IO.Path.GetFileName(firstFile ?? "file") : $"{count} files exported",
                        IconGlyph = "\U0001F4C1",
                        IconColor = ColSuccess,
                        ToolName = toolName,
                        FilePath = count == 1 ? firstFile : null,
                        FilePaths = count > 1 ? files : null,
                        FolderPath = folder,
                        Data = data
                    };
                }

                // ── Sheets printed to PDF ──
                case "PrintSheets":
                {
                    var files = ExtractFileList(data, "output_files");
                    string firstFile = files != null && files.Count > 0 ? files[0] : null;

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.FilePrinted,
                        Title = "PDF Print",
                        Subtitle = firstFile != null ? System.IO.Path.GetFileName(firstFile) : "Printed",
                        IconGlyph = "\U0001F5A8",
                        IconColor = ColSuccess,
                        ToolName = toolName,
                        FilePath = firstFile,
                        FilePaths = files != null && files.Count > 1 ? files : null,
                        Data = data
                    };
                }

                // ── Parameter value modified ──
                case "SetElementParameter":
                {
                    // Skip preview mode
                    if (data != null && data.TryGetValue("preview", out var prev) && Convert.ToBoolean(prev))
                        return null;

                    string paramName = data != null && data.TryGetValue("parameter_name", out var pn) ? pn?.ToString() : "Parameter";
                    string oldVal = data != null && data.TryGetValue("old_value", out var ov) ? ov?.ToString() : "";
                    string newVal = data != null && data.TryGetValue("new_value", out var nv) ? nv?.ToString() : "";
                    string elemName = data != null && data.TryGetValue("element_name", out var en) ? en?.ToString() : "";
                    long elemId = 0;
                    if (data != null && data.TryGetValue("element_id", out var eid))
                        elemId = ConvertToLong(eid) ?? 0;

                    var entry = new ParameterChangeEntry
                    {
                        ElementId = elemId,
                        ElementName = elemName,
                        OldValue = oldVal,
                        NewValue = newVal
                    };

                    // Try to aggregate into existing record for same parameter name
                    var existing = _outputRecords.LastOrDefault(r =>
                        r.RecordType == OutputRecordType.ParameterSet &&
                        r.ParameterName == paramName);

                    if (existing != null)
                    {
                        existing.ChangeEntries.Add(entry);
                        existing.Subtitle = $"{existing.ChangeEntries.Count} elements modified";
                        existing.Timestamp = DateTime.Now;
                        return null; // signal caller to just rebuild cards, not add new record
                    }

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ParameterSet,
                        Title = paramName,
                        Subtitle = $"{elemName}: {oldVal} \u2192 {newVal}",
                        IconGlyph = "\u270F",
                        IconColor = ColWarning,
                        ToolName = toolName,
                        ParameterName = paramName,
                        ChangeEntries = new List<ParameterChangeEntry> { entry },
                        Data = data
                    };
                }

                // ── Schedule modifications (aggregated per schedule) ──
                case "AddScheduleField":
                case "FormatScheduleField":
                case "ModifyScheduleFilter":
                case "ModifyScheduleSort":
                {
                    // Skip read-only list operations
                    if (data != null && data.TryGetValue("mode", out var mode) && mode?.ToString() == "list")
                        return null;

                    string schedName = data != null && data.TryGetValue("schedule_name", out var sn) ? sn?.ToString() : null;
                    if (string.IsNullOrEmpty(schedName)) return null;

                    long? schedId = null;
                    if (data != null && data.TryGetValue("schedule_id", out var schId))
                        schedId = ConvertToLong(schId);

                    var changeEntry = BuildScheduleChangeEntry(toolName, data);
                    if (changeEntry == null) return null;

                    // Try to aggregate into existing record for same schedule
                    var existing = _outputRecords.LastOrDefault(r =>
                        r.RecordType == OutputRecordType.ScheduleModified &&
                        r.ScheduleName == schedName);

                    if (existing != null)
                    {
                        existing.ScheduleChangeEntries.Add(changeEntry);
                        existing.Subtitle = FormatScheduleModSummary(existing.ScheduleChangeEntries);
                        existing.Timestamp = DateTime.Now;
                        if (schedId.HasValue && !existing.ScheduleId.HasValue)
                            existing.ScheduleId = schedId;
                        return null; // signal caller to rebuild cards
                    }

                    var entries = new List<ScheduleChangeEntry> { changeEntry };
                    string schedParentId = FindParentRecordIdByElementId(schedId);
                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ScheduleModified,
                        Title = schedName,
                        Subtitle = FormatScheduleModSummary(entries),
                        IconGlyph = "\U0001F4CB",
                        IconColor = ColPrimary,
                        ToolName = toolName,
                        ScheduleName = schedName,
                        ScheduleId = schedId,
                        ParentRecordId = schedParentId,
                        ScheduleChangeEntries = entries,
                        Data = data
                    };
                }

                // ── Code execution — only show card for Actions (has Transaction), skip Queries ──
                case "ExecuteCode":
                {
                    // ── OutputHint routing: if ZEXUS_JSON includes output_type, generate rich cards ──
                    string outputType = data != null && data.TryGetValue("output_type", out var ot)
                        ? ot?.ToString() : null;

                    if (!string.IsNullOrEmpty(outputType))
                        return MapExecuteCodeOutputHint(outputType, data);

                    // ── Fallback: generic ExecuteCode card ──
                    // Query (no Transaction) → no card (thinking chain only)
                    // Action (has Transaction) → generic ⚙ card
                    bool hasTx = data != null && data.TryGetValue("has_transaction", out var htx)
                        && htx is bool htxBool && htxBool;
                    if (!hasTx) return null;

                    string desc = data != null && data.TryGetValue("description", out var d) ? d?.ToString() : null;
                    if (string.IsNullOrEmpty(desc)) return null;

                    string ecOutput = data != null && data.TryGetValue("output", out var o) ? o?.ToString() : "";
                    string retVal = data != null && data.TryGetValue("return_value", out var rv) ? rv?.ToString() : "";

                    string subtitle = !string.IsNullOrEmpty(ecOutput) ? ecOutput.Split('\n')[0] : retVal;
                    if (subtitle != null && subtitle.Length > 60) subtitle = subtitle.Substring(0, 60) + "...";
                    if (string.IsNullOrEmpty(subtitle)) subtitle = "Executed";

                    long? codeViewId = null;
                    if (data != null && data.TryGetValue("view_id", out var codeVid))
                        codeViewId = ConvertToLong(codeVid);

                    // Fallback: if ZEXUS_JSON didn't provide view_id, check sys_created for views
                    if (codeViewId == null && data != null && data.ContainsKey("sys_created")
                        && data["sys_created"] is List<ElementDetail> sysCreated)
                    {
                        var firstView = sysCreated.FirstOrDefault(e => e.IsView);
                        if (firstView != null) codeViewId = firstView.ElementId;
                    }

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.CodeExecuted,
                        Title = desc,
                        Subtitle = subtitle,
                        IconGlyph = "\u2699",
                        IconColor = ColWarning,
                        ToolName = toolName,
                        ViewId = codeViewId,
                        Data = data
                    };
                }

                // ── QC Evidence Pack — view with isolated elements ──
                case "CreateQCEvidencePack":
                {
                    bool isDryRun = data != null && data.TryGetValue("dry_run", out var dr)
                        && dr is bool drBool && drBool;
                    if (isDryRun) return null;

                    long? qcViewId = null;
                    if (data != null && data.TryGetValue("created_view_id", out var qcVid))
                        qcViewId = ConvertToLong(qcVid);

                    string qcViewName = data != null && data.TryGetValue("created_view_name", out var qcVn) ? qcVn?.ToString() : "QC View";
                    string qcElemCount = data != null && data.TryGetValue("element_count", out var qcEc) ? qcEc?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewCreated,
                        Title = qcViewName,
                        Subtitle = $"Created \u2022 {qcElemCount} elements isolated",
                        IconGlyph = "\U0001F441",
                        IconColor = ColSuccess,
                        ToolName = toolName,
                        ViewId = qcViewId,
                        Data = data
                    };
                }

                // ── Color override applied to elements ──
                case "ColorElements":
                {
                    bool isCleared = data != null && data.ContainsKey("cleared_count");
                    if (isCleared) return null;

                    bool isGroupBy = data != null && data.ContainsKey("groups_count");
                    if (isGroupBy)
                    {
                        string ceParam = data.TryGetValue("parameter", out var cep) ? cep?.ToString() : "Parameter";
                        string ceTotalEl = data.TryGetValue("total_elements", out var cete) ? cete?.ToString() : "";
                        string ceGroups = data.TryGetValue("groups_count", out var ceg) ? ceg?.ToString() : "";

                        return new OutputRecord
                        {
                            RecordType = OutputRecordType.ViewModified,
                            Title = $"Color by {ceParam}",
                            Subtitle = $"{ceTotalEl} elements in {ceGroups} groups",
                            IconGlyph = "\U0001F3A8",
                            IconColor = ColAccent,
                            ToolName = toolName,
                            CanUndoRecord = true,
                            Data = data
                        };
                    }

                    string ceCount = data != null && data.TryGetValue("colored_count", out var cec) ? cec?.ToString() : "";
                    string ceColor = data != null && data.TryGetValue("color", out var cecc) ? cecc?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewModified,
                        Title = "Color Override",
                        Subtitle = $"{ceCount} elements colored {ceColor}",
                        IconGlyph = "\U0001F3A8",
                        IconColor = ColAccent,
                        ToolName = toolName,
                        CanUndoRecord = true,
                        Data = data
                    };
                }

                // ── Isolate/hide elements in view ──
                case "IsolateElements":
                {
                    string ieMode = data != null && data.TryGetValue("mode", out var iem) ? iem?.ToString() : "";
                    if (ieMode == "reset") return null;

                    string ieView = data != null && data.TryGetValue("view", out var iev) ? iev?.ToString() : "Current View";
                    string ieCount = data != null && data.TryGetValue("element_count", out var iec) ? iec?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewModified,
                        Title = ieView,
                        Subtitle = $"Isolated \u2022 {ieCount} elements",
                        IconGlyph = "\U0001F441",
                        IconColor = ColAccent,
                        ToolName = toolName,
                        CanUndoRecord = true,
                        UndoToolName = "IsolateElements",
                        UndoData = new Dictionary<string, object> { ["mode"] = "reset" },
                        Data = data
                    };
                }

                // ── Tag elements in view ──
                case "TagElements":
                {
                    string teCount = data != null && data.TryGetValue("tagged_count", out var tec) ? tec?.ToString() : "";
                    string teCat = data != null && data.TryGetValue("category", out var tecat) ? tecat?.ToString() : "";
                    string teTagType = data != null && data.TryGetValue("tag_type_used", out var tett) ? tett?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewModified,
                        Title = $"Tag {teCat}",
                        Subtitle = $"{teCount} elements tagged \u2022 {teTagType}",
                        IconGlyph = "\U0001F3F7",
                        IconColor = ColWarning,
                        ToolName = toolName,
                        Data = data
                    };
                }

                // ── View filter created/applied ──
                case "CreateViewFilter":
                {
                    string vfName = data != null && data.TryGetValue("filter_name", out var vfn) ? vfn?.ToString() : "Filter";
                    string vfRule = data != null && data.TryGetValue("rule", out var vfr) ? vfr?.ToString() : "";
                    string vfViewName = data != null && data.TryGetValue("view_name", out var vfvn) ? vfvn?.ToString() : null;

                    string vfParentId = !string.IsNullOrEmpty(vfViewName) ? FindParentRecordId(vfViewName) : null;

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewModified,
                        Title = vfName,
                        Subtitle = $"Filter \u2022 {vfRule}",
                        IconGlyph = "\U0001F50D",
                        IconColor = ColSuccess,
                        ToolName = toolName,
                        ParentRecordId = vfParentId,
                        Data = data
                    };
                }

                // ── Batch parameter set ──
                case "BatchSetParameter":
                {
                    bool bspPreview = data != null && data.TryGetValue("preview", out var bspp)
                        && bspp is bool bspBool && bspBool;
                    if (bspPreview) return null;

                    string bspParam = data != null && data.TryGetValue("parameter_name", out var bspn) ? bspn?.ToString() : "Parameter";
                    string bspValue = data != null && data.TryGetValue("new_value", out var bspv) ? bspv?.ToString() : "";
                    string bspCount = data != null && data.TryGetValue("success_count", out var bspc) ? bspc?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ParameterSet,
                        Title = bspParam,
                        Subtitle = $"{bspCount} elements set to '{bspValue}'",
                        IconGlyph = "\u270F",
                        IconColor = ColWarning,
                        ToolName = toolName,
                        ParameterName = bspParam,
                        Data = data
                    };
                }

                // Everything else (queries, edits, navigation) → no output record
                default:
                    return null;
            }
        }

        /// <summary>
        /// Route ExecuteCode results with ZEXUS_JSON output_type to rich UI cards.
        /// This bridges the gap when LLM uses ExecuteCode for operations that
        /// predefined tools would normally handle (view creation, visibility changes, etc.).
        /// Falls back to generic ⚙ card for unrecognized output_types.
        /// </summary>
        private OutputRecord MapExecuteCodeOutputHint(string outputType, Dictionary<string, object> data)
        {
            switch (outputType)
            {
                // ── View created via ExecuteCode ──
                case "view_created":
                {
                    long? viewId = data.TryGetValue("view_id", out var vid) ? ConvertToLong(vid) : null;
                    string viewName = data.TryGetValue("view_name", out var vn) ? vn?.ToString() : "View";
                    string viewType = data.TryGetValue("view_type", out var vt) ? vt?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewCreated,
                        Title = viewName,
                        Subtitle = $"Created \u2022 {viewType}",
                        IconGlyph = "\U0001F441",
                        IconColor = ColSuccess,
                        ToolName = "ExecuteCode",
                        ViewId = viewId,
                        Data = data
                    };
                }

                // ── Schedule created via ExecuteCode ──
                case "schedule_created":
                {
                    long? schedId = data.TryGetValue("schedule_id", out var sid) ? ConvertToLong(sid) : null;
                    string schedName = data.TryGetValue("schedule_name", out var sn) ? sn?.ToString() : "Schedule";
                    string catName = data.TryGetValue("category", out var c) ? c?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ScheduleCreated,
                        Title = schedName,
                        Subtitle = $"Created \u2022 {catName}",
                        IconGlyph = "\U0001F4CA",
                        IconColor = ColSuccess,
                        ToolName = "ExecuteCode",
                        ViewId = schedId,
                        ScheduleId = schedId,
                        ScheduleName = schedName,
                        Data = data
                    };
                }

                // ── Sheets created via ExecuteCode ──
                case "sheets_created":
                {
                    int count = data.TryGetValue("created_count", out var cc) ? Convert.ToInt32(cc) : 0;
                    string firstSheet = data.TryGetValue("first_sheet", out var fs) ? fs?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewCreated,
                        Title = count > 1 ? $"{count} Sheets Created" : firstSheet,
                        Subtitle = count > 1 ? $"First: {firstSheet}" : "Created",
                        IconGlyph = "\U0001F4C4",
                        IconColor = ColSuccess,
                        ToolName = "ExecuteCode",
                        Data = data
                    };
                }

                // ── Elements modified (batch) via ExecuteCode ──
                case "elements_modified":
                {
                    string desc = data.TryGetValue("description", out var d) ? d?.ToString() : "Modified";
                    int modCount = data.TryGetValue("modified_count", out var mc) ? Convert.ToInt32(mc) : 0;
                    string paramName = data.TryGetValue("parameter_name", out var pn) ? pn?.ToString() : null;

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.CodeExecuted,
                        Title = !string.IsNullOrEmpty(paramName) ? paramName : desc,
                        Subtitle = $"{modCount} elements modified",
                        IconGlyph = "\u270F",
                        IconColor = ColWarning,
                        ToolName = "ExecuteCode",
                        Data = data
                    };
                }

                // ── Elements deleted via ExecuteCode ──
                case "elements_deleted":
                {
                    int delCount = data.TryGetValue("deleted_count", out var dc) ? Convert.ToInt32(dc) : 0;
                    string desc = data.TryGetValue("description", out var d) ? d?.ToString() : "Deleted";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.CodeExecuted,
                        Title = desc,
                        Subtitle = $"{delCount} elements deleted",
                        IconGlyph = "\U0001F5D1",
                        IconColor = ColError,
                        ToolName = "ExecuteCode",
                        Data = data
                    };
                }

                // ── File exported via ExecuteCode ──
                case "file_exported":
                {
                    string filePath = data.TryGetValue("file_path", out var fp) ? fp?.ToString() : null;
                    string format = data.TryGetValue("format", out var f) ? f?.ToString() : "File";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.FileExported,
                        Title = $"{format} Export",
                        Subtitle = filePath != null ? System.IO.Path.GetFileName(filePath) : "Exported",
                        IconGlyph = "\U0001F4C1",
                        IconColor = ColSuccess,
                        ToolName = "ExecuteCode",
                        FilePath = filePath,
                        Data = data
                    };
                }

                // ── Unrecognized output_type: generic card with view_id if available ──
                default:
                {
                    string desc = data.TryGetValue("description", out var d) ? d?.ToString() : outputType;
                    long? viewId = data.TryGetValue("view_id", out var vid) ? ConvertToLong(vid) : null;

                    string ecOutput = data.TryGetValue("output", out var o) ? o?.ToString() : "";
                    string subtitle = !string.IsNullOrEmpty(ecOutput) ? ecOutput.Split('\n')[0] : "Executed";
                    if (subtitle.Length > 60) subtitle = subtitle.Substring(0, 60) + "...";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.CodeExecuted,
                        Title = desc,
                        Subtitle = subtitle,
                        IconGlyph = "\u2699",
                        IconColor = ColWarning,
                        ToolName = "ExecuteCode",
                        ViewId = viewId,
                        Data = data
                    };
                }
            }
        }


        private TransactionJournalEntry BuildJournalEntry(ToolResult result)
        {
            if (result?.Data == null) return null;

            bool hasTransaction = result.Data.ContainsKey("has_transaction") && result.Data["has_transaction"] is bool ht && ht;

            // Extract description from result data
            string desc = result.Data.ContainsKey("description") ? result.Data["description"]?.ToString() : null;
            if (!string.IsNullOrEmpty(desc) && desc.StartsWith("AI Agent: "))
                desc = desc.Substring("AI Agent: ".Length);

            // Get side effect counts
            int created = 0, modified = 0, deleted = 0;
            if (result.Data.ContainsKey("created_count") && result.Data["created_count"] is long cc) created = (int)cc;
            if (result.Data.ContainsKey("modified_count") && result.Data["modified_count"] is long mc) modified = (int)mc;
            if (result.Data.ContainsKey("deleted_count") && result.Data["deleted_count"] is long dc) deleted = (int)dc;

            var entry = new TransactionJournalEntry
            {
                TurnIndex = 0,
                Timestamp = DateTime.Now,
                Description = desc ?? "Code execution",
                IsWrite = hasTransaction,
                Success = result.Success,
                CreatedCount = created,
                ModifiedCount = modified,
                DeletedCount = deleted
            };

            // System-level element tracking (from DocumentChanged)
            if (result.Data.ContainsKey("sys_created") && result.Data["sys_created"] is List<ElementDetail> createdElements)
                entry.CreatedElements = createdElements;
            if (result.Data.ContainsKey("sys_modified_ids") && result.Data["sys_modified_ids"] is List<long> modIds)
                entry.ModifiedElements = modIds.Select(id => new ElementDetail { ElementId = id }).ToList();
            if (result.Data.ContainsKey("sys_deleted_ids") && result.Data["sys_deleted_ids"] is List<long> delIds)
                entry.DeletedElements = delIds.Select(id => new ElementDetail { ElementId = id }).ToList();

            // Optional ZEXUS_JSON enrichment
            if (result.Data.ContainsKey("output_type"))
                entry.OutputType = result.Data["output_type"]?.ToString();
            if (result.Data.ContainsKey("view_id"))
            {
                try { entry.ViewId = Convert.ToInt64(result.Data["view_id"]); }
                catch { /* non-fatal */ }
            }

            return entry;
        }


        private async void OnOutputRecordClicked(OutputRecord record)
        {
            try
            {
                if (record.ViewId.HasValue)
                {
                    // Navigate to view in Revit — direct API, no tool registry dependency
                    await App.RevitEventHandler.NavigateToViewAsync(record.ViewId.Value);
                }
                else if (record.FilePath != null && System.IO.File.Exists(record.FilePath))
                {
                    SysProcess.Start(new ProcessStartInfo(record.FilePath) { UseShellExecute = true });
                }
                else if (record.FilePaths != null && record.FilePaths.Count > 0)
                {
                    // Open the folder containing the files
                    string folder = record.FolderPath;
                    if (string.IsNullOrEmpty(folder) && record.FilePaths.Count > 0)
                        folder = System.IO.Path.GetDirectoryName(record.FilePaths[0]);

                    if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
                        SysProcess.Start("explorer.exe", folder);
                }
                else if (!string.IsNullOrEmpty(record.FolderPath) && System.IO.Directory.Exists(record.FolderPath))
                {
                    SysProcess.Start("explorer.exe", record.FolderPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Output record click error: {ex.Message}");
            }
        }

        /// <summary>
        /// Selects and highlights an element in Revit when user clicks on an entry row
        /// in the expanded parameter change detail table.
        /// Uses SelectElements tool which will select the element (blue highlight) and zoom to fit.
        /// </summary>
        private async void OnElementEntryClicked(long elementId)
        {
            try
            {
                if (elementId <= 0) return;

                SetStatus($"Selecting element #{elementId}...", true);

                var args = new Dictionary<string, object>
                {
                    ["element_ids"] = new List<long> { elementId },
                    ["zoom_to_fit"] = true,
                    ["clear_previous"] = true
                };

                await App.RevitEventHandler.ExecuteToolAsync("SelectElements", args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Element highlight click error: {ex.Message}");
            }
            finally
            {
                SetStatus("", false);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Undo Parameter Changes — inline confirmation + execution
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Shows an inline confirmation bar at the bottom of the card for "Undo All" action.
        /// </summary>
        private async void OnUndoAllParameterChanges(OutputRecord record)
        {
            try
            {
                var pending = record.ChangeEntries.Where(e => !e.IsReverted).ToList();
                if (pending.Count == 0) return;

                SetStatus($"Reverting {pending.Count} changes...", true);

                int successCount = 0;
                foreach (var entry in pending)
                {
                    var args = new Dictionary<string, object>
                    {
                        ["element_id"] = entry.ElementId,
                        ["parameter_name"] = record.ParameterName,
                        ["value"] = entry.OldValue
                    };

                    var result = await App.RevitEventHandler.ExecuteToolAsync("SetElementParameter", args);
                    var toolResult = result as Models.ToolResult;
                    if (toolResult != null && toolResult.Success)
                    {
                        entry.IsReverted = true;
                        successCount++;
                    }
                    else
                    {
                        string errMsg = toolResult?.Message ?? "Unknown error";
                        System.Diagnostics.Debug.WriteLine($"[Zexus] Undo failed for #{entry.ElementId}: {errMsg}");
                    }
                }

                UpdateRecordSubtitleAfterRevert(record);
                (DataContext as ChatWindowViewModel)?.RightPanel?.RefreshRecord(record);

                SetStatus($"Reverted {successCount}/{pending.Count} changes", false);
                await System.Threading.Tasks.Task.Delay(2000);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Undo all error: {ex.Message}");
            }
            finally
            {
                SetStatus("", false);
            }
        }

        /// <summary>
        /// Updates the subtitle of a ParameterSet record after one or more entries have been reverted.
        /// </summary>
        private void UpdateRecordSubtitleAfterRevert(OutputRecord record)
        {
            if (record.ChangeEntries == null) return;

            int total = record.ChangeEntries.Count;
            int reverted = record.RevertedCount;

            if (record.IsFullyReverted)
            {
                record.Subtitle = $"All {total} changes reverted";
            }
            else if (reverted > 0)
            {
                record.Subtitle = $"{total} elements modified ({reverted} reverted)";
            }
            else
            {
                record.Subtitle = $"{total} elements modified";
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Undo/Delete Schedule Changes — inline confirmation + execution
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Shows an inline confirmation bar for "Undo All" on a ScheduleModified card.
        /// </summary>
        private async void OnUndoViewModified(OutputRecord record)
        {
            try
            {
                if (record.UndoToolName == null || record.UndoData == null)
                {
                    System.Diagnostics.Debug.WriteLine("[Zexus] ViewModified undo: missing tool name or data");
                    return;
                }

                SetStatus($"Resetting visibility on \"{record.Title}\"...", true);

                var result = await App.RevitEventHandler.ExecuteToolAsync(record.UndoToolName, record.UndoData);
                var toolResult = result as Models.ToolResult;

                if (toolResult != null && toolResult.Success)
                {
                    record.IsRecordReverted = true;
                    record.Subtitle = "Reverted \u2022 " + record.Subtitle;
                    (DataContext as ChatWindowViewModel)?.RightPanel?.RefreshRecord(record);
                }
                else
                {
                    string errMsg = toolResult?.Message ?? "Unknown error";
                    System.Diagnostics.Debug.WriteLine($"[Zexus] ViewModified undo failed: {errMsg}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] ViewModified undo error: {ex.Message}");
            }
            finally
            {
                SetStatus("", false);
            }
        }

        /// <summary>
        /// Reverts a single schedule change by calling the undo tool with reversed parameters.
        /// </summary>
        private async void OnUndoAllScheduleChanges(OutputRecord record)
        {
            try
            {
                var pending = record.ScheduleChangeEntries
                    .Where(e => e.CanUndo && !e.IsReverted && e.UndoToolName != null && e.UndoData != null)
                    .ToList();
                if (pending.Count == 0) return;

                SetStatus($"Reverting {pending.Count} schedule changes...", true);

                int successCount = 0;
                foreach (var entry in pending)
                {
                    var result = await App.RevitEventHandler.ExecuteToolAsync(entry.UndoToolName, entry.UndoData);
                    var toolResult = result as Models.ToolResult;

                    if (toolResult != null && toolResult.Success)
                    {
                        entry.IsReverted = true;
                        successCount++;
                    }
                    else
                    {
                        string errMsg = toolResult?.Message ?? "Unknown error";
                        System.Diagnostics.Debug.WriteLine($"[Zexus] Schedule undo failed for {entry.FieldName}: {errMsg}");
                    }
                }

                UpdateScheduleRecordSubtitle(record);
                (DataContext as ChatWindowViewModel)?.RightPanel?.RefreshRecord(record);

                SetStatus($"Reverted {successCount}/{pending.Count} schedule changes", false);
                await System.Threading.Tasks.Task.Delay(2000);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Schedule undo all error: {ex.Message}");
            }
            finally
            {
                SetStatus("", false);
            }
        }

        /// <summary>
        /// Deletes a schedule or view element from the Revit document via ExecuteCode.
        /// Works for both ScheduleCreated (uses ScheduleId) and ViewCreated (uses ViewId) records.
        /// </summary>
        private async void OnDeleteSchedule(OutputRecord record)
        {
            try
            {
                // Resolve the element ID — try ScheduleId first, then ViewId
                long? elementId = record.ScheduleId ?? record.ViewId;
                if (!elementId.HasValue) return;

                string entityType = record.RecordType == OutputRecordType.ViewCreated ? "view" : "schedule";
                SetStatus($"Deleting {entityType} \"{record.Title}\"...", true);

                long eid = elementId.Value;

                // ── Step 1: If target is the active view, switch away first ──
                // IMPORTANT: Must be a separate ExecuteToolAsync call because Revit
                // does not finalize ActiveView changes within the same External Event.
                string switchCode = $@"
ElementId eid;
var ctorLong = typeof(ElementId).GetConstructor(new[] {{ typeof(long) }});
if (ctorLong != null)
    eid = (ElementId)ctorLong.Invoke(new object[] {{ (long){eid} }});
else
    eid = new ElementId((int){eid});

if (doc.ActiveView != null && doc.ActiveView.Id == eid)
{{
    var candidates = new FilteredElementCollector(doc)
        .OfClass(typeof(View))
        .Cast<View>()
        .Where(v => !v.IsTemplate && v.Id != eid
                  && v.ViewType != ViewType.Internal
                  && v.ViewType != ViewType.Undefined
                  && v.ViewType != ViewType.DrawingSheet
                  && v.ViewType != ViewType.ProjectBrowser
                  && v.ViewType != ViewType.SystemBrowser)
        .ToList();

    var altView = candidates.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan)
               ?? candidates.FirstOrDefault(v => v.ViewType == ViewType.ThreeD)
               ?? candidates.FirstOrDefault(v => v.ViewType == ViewType.CeilingPlan)
               ?? candidates.FirstOrDefault(v => v.ViewType == ViewType.Section)
               ?? candidates.FirstOrDefault(v => v.ViewType == ViewType.Elevation)
               ?? candidates.FirstOrDefault();

    if (altView == null)
        throw new InvalidOperationException(""Cannot delete — no alternative view available"");

    uiDoc.ActiveView = altView;
    return ""switched"";
}}
return ""not_active"";";

                var switchArgs = new Dictionary<string, object> { ["code"] = switchCode };
                var switchResult = await App.RevitEventHandler.ExecuteToolAsync("ExecuteCode", switchArgs);
                var switchToolResult = switchResult as Models.ToolResult;

                if (switchToolResult != null && !switchToolResult.Success)
                {
                    string errMsg = switchToolResult.Message ?? "Unknown error";
                    System.Diagnostics.Debug.WriteLine($"[Zexus] Switch away failed: {errMsg}");
                    SetStatus($"Delete failed: {errMsg}", false);
                    await System.Threading.Tasks.Task.Delay(3000);
                    return;
                }

                // ── Step 2: Delete the element (now guaranteed not active) ──
                string deleteCode = $@"
ElementId eid;
var ctorLong = typeof(ElementId).GetConstructor(new[] {{ typeof(long) }});
if (ctorLong != null)
    eid = (ElementId)ctorLong.Invoke(new object[] {{ (long){eid} }});
else
    eid = new ElementId((int){eid});

var elem = doc.GetElement(eid);
if (elem == null) throw new InvalidOperationException(""Element not found: {eid}"");

using (var tx = new Transaction(doc, ""Delete {entityType}""))
{{
    tx.Start();
    doc.Delete(eid);
    tx.Commit();
}}
return ""Deleted"";";

                var deleteArgs = new Dictionary<string, object> { ["code"] = deleteCode };
                var result = await App.RevitEventHandler.ExecuteToolAsync("ExecuteCode", deleteArgs);
                var toolResult = result as Models.ToolResult;

                if (toolResult != null && toolResult.Success)
                {
                    record.IsDeleted = true;
                    record.Subtitle = "Deleted";
                    // Cascade grayout to child records (e.g., ScheduleModified, ViewModified)
                    CascadeDeleteToChildren(record.Id);
                    (DataContext as ChatWindowViewModel)?.RightPanel?.RefreshRecord(record);
                }
                else
                {
                    string errMsg = toolResult?.Message ?? "Unknown error";
                    System.Diagnostics.Debug.WriteLine($"[Zexus] Schedule delete failed: {errMsg}");
                    SetStatus($"Delete failed: {errMsg}", false);
                    await System.Threading.Tasks.Task.Delay(3000);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Schedule delete error: {ex.Message}");
            }
            finally
            {
                SetStatus("", false);
            }
        }

        /// <summary>
        /// Updates the subtitle of a ScheduleModified record after one or more entries have been reverted.
        /// </summary>
        private void UpdateScheduleRecordSubtitle(OutputRecord record)
        {
            if (record.ScheduleChangeEntries == null) return;

            if (record.IsScheduleFullyReverted)
            {
                record.Subtitle = "All changes reverted";
            }
            else
            {
                int reverted = record.ScheduleRevertedCount;
                string summary = FormatScheduleModSummary(record.ScheduleChangeEntries);
                if (reverted > 0)
                    record.Subtitle = $"{summary} ({reverted} reverted)";
                else
                    record.Subtitle = summary;
            }
        }

        /// <summary>
        /// Safely extracts a List&lt;string&gt; from a result data dictionary value.
        /// Handles both List&lt;string&gt; and List&lt;object&gt; (type varies between net48/net8.0).
        /// </summary>
        private List<string> ExtractFileList(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var obj) || obj == null)
                return null;

            if (obj is List<string> strList)
                return strList;

            if (obj is System.Collections.IEnumerable enumerable)
            {
                var result = new List<string>();
                foreach (var item in enumerable)
                {
                    if (item != null)
                        result.Add(item.ToString());
                }
                return result.Count > 0 ? result : null;
            }

            return null;
        }

        private long? ConvertToLong(object val)
        {
            if (val == null) return null;
            if (val is long l) return l;
            if (val is int i) return i;
            if (long.TryParse(val.ToString(), out long parsed)) return parsed;
            return null;
        }

        /// <summary>
        /// Extracts a List of strings from tool result data (handles both List&lt;string&gt; and List&lt;object&gt;).
        /// Reuses the same logic as ExtractFileList but with a different name for clarity.
        /// </summary>
        private List<string> ExtractStringList(Dictionary<string, object> data, string key)
        {
            return ExtractFileList(data, key);
        }

        /// <summary>
        /// Finds the parent record (ViewCreated/ScheduleCreated) that matches a view/schedule name.
        /// Used to set ParentRecordId on child records (ViewModified, ScheduleModified).
        /// </summary>
        private string FindParentRecordId(string viewOrScheduleName)
        {
            if (string.IsNullOrEmpty(viewOrScheduleName)) return null;

            // Search for the most recent ViewCreated or ScheduleCreated record with matching title
            var parent = _outputRecords.LastOrDefault(r =>
                (r.RecordType == OutputRecordType.ViewCreated || r.RecordType == OutputRecordType.ScheduleCreated) &&
                !r.IsDeleted &&
                string.Equals(r.Title, viewOrScheduleName, StringComparison.OrdinalIgnoreCase));

            return parent?.Id;
        }

        /// <summary>
        /// Finds the parent record by element ID (ScheduleId or ViewId).
        /// Used when child records have the schedule/view ID but not necessarily the name.
        /// </summary>
        private string FindParentRecordIdByElementId(long? elementId)
        {
            if (!elementId.HasValue) return null;

            var parent = _outputRecords.LastOrDefault(r =>
                (r.RecordType == OutputRecordType.ScheduleCreated && r.ScheduleId == elementId) ||
                (r.RecordType == OutputRecordType.ViewCreated && r.ViewId == elementId));

            return parent?.Id;
        }

        /// <summary>
        /// When a host record (ViewCreated/ScheduleCreated) is deleted, cascade grayout
        /// to all child records that reference it via ParentRecordId.
        /// </summary>
        private void CascadeDeleteToChildren(string parentRecordId)
        {
            if (string.IsNullOrEmpty(parentRecordId)) return;

            foreach (var record in _outputRecords)
            {
                if (record.ParentRecordId == parentRecordId && !record.IsDeleted)
                {
                    record.IsDeleted = true;
                    record.Subtitle = "Host deleted \u2022 " + record.Subtitle;
                }
            }
        }

        /// <summary>
        /// Builds a ScheduleChangeEntry from a schedule modification tool's result data.
        /// </summary>
        private ScheduleChangeEntry BuildScheduleChangeEntry(string toolName, Dictionary<string, object> data)
        {
            if (data == null) return null;

            string fieldName = data.TryGetValue("field_name", out var fn) ? fn?.ToString() : "";

            switch (toolName)
            {
                case "AddScheduleField":
                {
                    // Determine action from result data
                    bool alreadyExists = data.TryGetValue("already_exists", out var ae) && Convert.ToBoolean(ae);
                    if (alreadyExists) return null; // skip warnings

                    string schedName = data.TryGetValue("schedule_name", out var sn2) ? sn2?.ToString() : "";

                    bool isRemove = data.TryGetValue("removed_field", out var rf) && rf != null;
                    if (isRemove)
                    {
                        // Removed field — cannot re-add automatically (would need field_name + position)
                        return new ScheduleChangeEntry
                        {
                            ChangeType = "Field", Action = "removed",
                            FieldName = rf?.ToString() ?? fieldName,
                            Detail = "",
                            CanUndo = false
                        };
                    }

                    bool isReorder = data.ContainsKey("from_position") && data.ContainsKey("to_position");
                    if (isReorder)
                    {
                        // Reorder — reverse by swapping from/to positions
                        return new ScheduleChangeEntry
                        {
                            ChangeType = "Field", Action = "reordered",
                            FieldName = fieldName,
                            Detail = $"{data["from_position"]} \u2192 {data["to_position"]}",
                            CanUndo = true,
                            UndoAction = "undo",
                            UndoToolName = "AddScheduleField",
                            UndoData = new Dictionary<string, object>
                            {
                                ["schedule_name"] = schedName,
                                ["field_name"] = fieldName,
                                ["mode"] = "reorder",
                                ["position"] = data["from_position"]
                            }
                        };
                    }

                    // Default: field added — undo by removing
                    string pos = data.TryGetValue("position", out var p) ? $"pos {p}" : "";
                    bool hidden = data.TryGetValue("is_hidden", out var h) && Convert.ToBoolean(h);
                    string detail = hidden ? $"{pos}, hidden" : pos;
                    string addedFieldName = data.TryGetValue("column_header", out var ch) ? ch?.ToString() ?? fieldName : fieldName;

                    return new ScheduleChangeEntry
                    {
                        ChangeType = "Field", Action = "added",
                        FieldName = addedFieldName,
                        Detail = detail.Trim().TrimStart(',').Trim(),
                        CanUndo = true,
                        UndoAction = "undo",
                        UndoToolName = "AddScheduleField",
                        UndoData = new Dictionary<string, object>
                        {
                            ["schedule_name"] = schedName,
                            ["field_name"] = fieldName,
                            ["mode"] = "remove"
                        }
                    };
                }

                case "FormatScheduleField":
                {
                    var changes = new List<string>();
                    if (data.TryGetValue("changes", out var ch) && ch is System.Collections.IEnumerable changeList)
                    {
                        foreach (var c in changeList)
                            if (c != null) changes.Add(c.ToString());
                    }

                    string fmtSchedName = data.TryGetValue("schedule_name", out var fsn) ? fsn?.ToString() : "";

                    // Build undo data from old_values returned by FormatScheduleField
                    Dictionary<string, object> undoData = null;
                    if (data.TryGetValue("old_values", out var ov) && ov is Dictionary<string, object> oldVals && oldVals.Count > 0)
                    {
                        undoData = new Dictionary<string, object>
                        {
                            ["schedule_name"] = fmtSchedName,
                            ["field_name"] = fieldName
                        };
                        // Copy old values as the new target values for reversal
                        foreach (var kv in oldVals)
                            undoData[kv.Key] = kv.Value;
                    }

                    return new ScheduleChangeEntry
                    {
                        ChangeType = "Format", Action = "set",
                        FieldName = fieldName,
                        Detail = changes.Count > 0 ? string.Join(", ", changes) : "",
                        CanUndo = undoData != null,
                        UndoAction = "undo",
                        UndoToolName = "FormatScheduleField",
                        UndoData = undoData
                    };
                }

                case "ModifyScheduleFilter":
                {
                    string filtSchedName = data.TryGetValue("schedule_name", out var ftsn) ? ftsn?.ToString() : "";
                    bool isRemove = data.ContainsKey("removed_index");
                    bool isClear = data.ContainsKey("cleared");

                    if (isClear)
                    {
                        int cleared = data.TryGetValue("cleared", out var cl) ? Convert.ToInt32(cl) : 0;
                        return cleared > 0 ? new ScheduleChangeEntry
                        {
                            ChangeType = "Filter", Action = "cleared",
                            FieldName = "", Detail = $"{cleared} filter(s)",
                            CanUndo = false // cannot restore cleared filters
                        } : null;
                    }

                    if (isRemove)
                    {
                        // Removed filter — cannot re-add without knowing original params
                        return new ScheduleChangeEntry
                        {
                            ChangeType = "Filter", Action = "removed",
                            FieldName = fieldName, Detail = "",
                            CanUndo = false
                        };
                    }

                    // Filter added — undo by removing it
                    string op = data.TryGetValue("operator", out var opv) ? opv?.ToString() : "";
                    string val = data.TryGetValue("value", out var v) ? v?.ToString() : "";

                    return new ScheduleChangeEntry
                    {
                        ChangeType = "Filter", Action = "added",
                        FieldName = fieldName,
                        Detail = $"{op} {val}".Trim(),
                        CanUndo = true,
                        UndoAction = "undo",
                        UndoToolName = "ModifyScheduleFilter",
                        UndoData = new Dictionary<string, object>
                        {
                            ["schedule_name"] = filtSchedName,
                            ["field_name"] = fieldName,
                            ["mode"] = "remove"
                        }
                    };
                }

                case "ModifyScheduleSort":
                {
                    string sortSchedName = data.TryGetValue("schedule_name", out var stsn) ? stsn?.ToString() : "";
                    bool isRemove = data.ContainsKey("removed_index");
                    bool isClear = data.ContainsKey("cleared");

                    if (isClear)
                    {
                        int cleared = data.TryGetValue("cleared", out var cl) ? Convert.ToInt32(cl) : 0;
                        return cleared > 0 ? new ScheduleChangeEntry
                        {
                            ChangeType = "Sort", Action = "cleared",
                            FieldName = "", Detail = $"{cleared} sort(s)",
                            CanUndo = false // cannot restore cleared sorts
                        } : null;
                    }

                    if (isRemove)
                    {
                        // Removed sort — cannot re-add without knowing original params
                        return new ScheduleChangeEntry
                        {
                            ChangeType = "Sort", Action = "removed",
                            FieldName = fieldName, Detail = "",
                            CanUndo = false
                        };
                    }

                    // Sort added — undo by removing it
                    string order = data.TryGetValue("sort_order", out var so) ? so?.ToString() : "";
                    return new ScheduleChangeEntry
                    {
                        ChangeType = "Sort", Action = "added",
                        FieldName = fieldName,
                        Detail = order,
                        CanUndo = true,
                        UndoAction = "undo",
                        UndoToolName = "ModifyScheduleSort",
                        UndoData = new Dictionary<string, object>
                        {
                            ["schedule_name"] = sortSchedName,
                            ["field_name"] = fieldName,
                            ["mode"] = "remove"
                        }
                    };
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// Generates a concise summary like "3 fields added, 1 filter set, 1 sort added".
        /// </summary>
        private string FormatScheduleModSummary(List<ScheduleChangeEntry> entries)
        {
            if (entries == null || entries.Count == 0) return "Modified";

            var counts = new Dictionary<string, int>();
            foreach (var e in entries)
            {
                var key = $"{e.ChangeType} {e.Action}";
                if (counts.ContainsKey(key))
                    counts[key]++;
                else
                    counts[key] = 1;
            }

            var parts = new List<string>();
            foreach (var kv in counts)
            {
                parts.Add(kv.Value == 1 ? $"1 {kv.Key}" : $"{kv.Value} {kv.Key}");
            }

            return string.Join(", ", parts);
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            // ChatViewModel.SendCommand (AsyncRelayCommand) owns its own CancellationTokenSource
            // and auto-cancels on dispose; calling Dispose on _agentService is enough here.
            _agentService?.Dispose();
            base.OnClosed(e);
        }
    }
}
