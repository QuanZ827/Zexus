using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Zexus.Models;
using Zexus.Services;

namespace Zexus.ViewModels
{
    /// <summary>
    /// Owns the chat message stream and wires AgentService events to MessageViewModel
    /// updates. The View binds an ItemsControl to <see cref="Messages"/> via the
    /// MessageTemplateSelector — adding bubbles in code-behind is no longer needed.
    /// </summary>
    public class ChatViewModel : ViewModelBase
    {
        private readonly AgentService _agentService;

        public ObservableCollection<MessageViewModel> Messages { get; }
            = new ObservableCollection<MessageViewModel>();

        /// <summary>
        /// Images attached via Ctrl+V in the input box. InputBarControl writes here;
        /// SendInternalAsync snapshots and clears them when a turn is dispatched.
        /// </summary>
        public ObservableCollection<ImageAttachment> PendingImages { get; }
            = new ObservableCollection<ImageAttachment>();

        private string _inputText = string.Empty;
        public string InputText
        {
            get => _inputText;
            set
            {
                if (SetProperty(ref _inputText, value))
                    SendCommand?.NotifyCanExecuteChanged();
            }
        }

        private bool _isProcessing;
        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (SetProperty(ref _isProcessing, value))
                {
                    SendCommand?.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(IsInputEnabled));
                }
            }
        }

        /// <summary>Inverted IsProcessing — bind TextBox.IsEnabled here.</summary>
        public bool IsInputEnabled => !_isProcessing;

        private bool _hasMessages;
        public bool HasMessages
        {
            get => _hasMessages;
            private set => SetProperty(ref _hasMessages, value);
        }

        /// <summary>The streaming agent bubble currently being filled. Null when idle.</summary>
        private MessageViewModel _currentStreamingMessage;
        public MessageViewModel CurrentStreamingMessage => _currentStreamingMessage;

        public IAsyncRelayCommand SendCommand { get; }
        public IRelayCommand NewChatCommand { get; }

        /// <summary>
        /// Pre-send hook (sync). Return false to abort the send — e.g. if the API key
        /// isn't configured and the caller wants to show a setup dialog instead.
        /// </summary>
        public Func<bool> BeforeSendAction { get; set; }

        /// <summary>
        /// Post-send hook. Receives the final ChatMessage (assistant or system-role error).
        /// Used by ChatWindow to show the inline confirmation panel when a response is a
        /// confirmation request.
        /// </summary>
        public Action<ChatMessage> AfterSendAction { get; set; }

        public ChatViewModel(AgentService agentService)
        {
            _agentService = agentService;

            SendCommand = new AsyncRelayCommand(
                ct => SendInternalAsync(InputText, null, ct),
                () => !IsProcessing && (!string.IsNullOrWhiteSpace(InputText) || PendingImages.Count > 0));
            NewChatCommand = new RelayCommand(ClearChat);

            Messages.CollectionChanged += (s, e) => HasMessages = Messages.Count > 0;
            PendingImages.CollectionChanged += (s, e) => SendCommand?.NotifyCanExecuteChanged();

            // ── Wire AgentService streaming + tool events ──
            // Each handler marshals to UI thread via RunOnUi.
            _agentService.OnStreamingText += text =>
            {
                var m = _currentStreamingMessage;
                if (m != null) m.AppendStreamingText(text);
            };

            _agentService.OnToolExecuting += (name, input) =>
            {
                var m = _currentStreamingMessage;
                if (m == null) return;
                RunOnUi(() =>
                {
                    m.AddOrUpdateToolCall(name, ToolCallStatus.Executing);
                    m.ShowInlineStatus = true;
                    m.TotalSteps += 1;
                    m.InlineStatusText = "Executing: " + name;

                    // Add a thinking-chain node (collapsed Workspace Panel — Step 6).
                    string description = null, codeSnippet = null;
                    if (input != null)
                    {
                        if (input.TryGetValue("description", out var d)) description = d?.ToString();
                        if (name == "ExecuteCode" && input.TryGetValue("code", out var c)) codeSnippet = c?.ToString();
                    }
                    string title = (name == "ExecuteCode" && !string.IsNullOrEmpty(description))
                        ? description : name;

                    m.ThinkingNodes.Add(new ThinkingChainNode
                    {
                        Id = System.Guid.NewGuid().ToString(),
                        Title = title,
                        Subtitle = $"Running {name}...",
                        Status = ThinkingNodeStatus.Active,
                        Timestamp = System.DateTime.Now,
                        ToolName = name,
                        Description = description,
                        CodeSnippet = codeSnippet,
                        InputParams = input
                    });
                });
            };

            _agentService.OnToolCompleted += (name, result, durationMs) =>
            {
                var m = _currentStreamingMessage;
                if (m == null) return;
                RunOnUi(() =>
                {
                    var status = result != null && result.Success ? ToolCallStatus.Completed : ToolCallStatus.Failed;
                    m.AddOrUpdateToolCall(name, status);
                    m.CompletedSteps += 1;
                    m.InlineStatusText = $"Step {m.CompletedSteps}/{m.TotalSteps}: {name}";

                    // Mark the last active node as completed/failed.
                    ThinkingChainNode active = null;
                    for (int i = m.ThinkingNodes.Count - 1; i >= 0; i--)
                    {
                        if (m.ThinkingNodes[i].Status == ThinkingNodeStatus.Active)
                        { active = m.ThinkingNodes[i]; break; }
                    }
                    if (active != null)
                    {
                        active.Status = result != null && result.Success
                            ? ThinkingNodeStatus.Completed
                            : ThinkingNodeStatus.Failed;
                        active.DurationMs = durationMs;
                        if (result != null)
                        {
                            active.Output = result.Message;
                            active.ResultData = result.Data;
                            if (!result.Success) active.ErrorMessage = result.Message;
                        }
                    }
                });
            };

            _agentService.OnReasoningForThinkingChain += reasoningText =>
            {
                var m = _currentStreamingMessage;
                if (m == null || string.IsNullOrEmpty(reasoningText)) return;
                RunOnUi(() =>
                {
                    m.ThinkingNodes.Add(new ThinkingChainNode
                    {
                        Id = System.Guid.NewGuid().ToString(),
                        Title = "Reasoning",
                        Subtitle = reasoningText,
                        Status = ThinkingNodeStatus.Completed,
                        Timestamp = System.DateTime.Now,
                        ToolName = "_reasoning",
                        Description = reasoningText
                    });
                });
            };

            _agentService.OnProcessingCompleted += msg =>
            {
                var m = _currentStreamingMessage;
                if (m == null || msg == null) return;
                m.SetFinalContent(msg.Content, msg.ToolCalls);
            };
        }

        /// <summary>
        /// External send entry point used by ChatWindow.xaml.cs (current Step 2 bridge).
        /// Step 4 will replace the code-behind call site with the SendCommand binding.
        /// </summary>
        public Task<ChatMessage> SendAsync(string text, List<ImageAttachment> images, CancellationToken ct)
            => SendInternalAsync(text, images, ct);

        private async Task<ChatMessage> SendInternalAsync(string text, List<ImageAttachment> images, CancellationToken ct)
        {
            text = text?.Trim();

            // No explicit images → snapshot from PendingImages (Ctrl+V paste handler in InputBar).
            if (images == null && PendingImages.Count > 0)
            {
                images = new List<ImageAttachment>(PendingImages);
                PendingImages.Clear();
            }

            bool hasText = !string.IsNullOrEmpty(text);
            bool hasImages = images != null && images.Count > 0;
            if (!hasText && !hasImages) return null;

            // Caller's pre-send hook (HideConfirmationButtons, API-key check, etc.)
            if (BeforeSendAction != null && !BeforeSendAction())
                return null;

            IsProcessing = true;

            // User bubble (display the user message immediately)
            var userChat = new ChatMessage { Role = MessageRole.User, Content = text ?? string.Empty };
            // We don't store images on the displayed message — that's a session-only concern.
            Messages.Add(new MessageViewModel(userChat));

            // Clear input only when bound through the command (not when caller passed external text)
            if (text == _inputText) InputText = string.Empty;

            // Streaming placeholder for the agent response
            var agentChat = new ChatMessage { Role = MessageRole.Assistant, Content = string.Empty };
            var streaming = new MessageViewModel(agentChat) { IsStreaming = true };
            Messages.Add(streaming);
            _currentStreamingMessage = streaming;

            try
            {
                var response = await _agentService.ProcessMessageAsync(text ?? string.Empty, ct, images);

                // System-role early returns (API key, errors, cancellation) — replace the
                // streaming placeholder with a system message bubble.
                if (response != null && response.Role == MessageRole.System)
                {
                    RunOnUi(() =>
                    {
                        Messages.Remove(streaming);
                        Messages.Add(new MessageViewModel(response));
                    });
                }

                AfterSendAction?.Invoke(response);
                return response;
            }
            catch (System.OperationCanceledException)
            {
                RunOnUi(() => Messages.Remove(streaming));
                return null;
            }
            finally
            {
                streaming.IsStreaming = false;
                _currentStreamingMessage = null;
                IsProcessing = false;
            }
        }

        /// <summary>
        /// Add a transient system notification bubble (used for "session report exported", etc.).
        /// </summary>
        public void AddSystemNotification(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            RunOnUi(() => Messages.Add(new MessageViewModel(
                new ChatMessage { Role = MessageRole.System, Content = text })));
        }

        private void ClearChat()
        {
            _agentService.NewSession();
            Messages.Clear();
            AddSystemNotification("New conversation started.");
        }
    }
}
