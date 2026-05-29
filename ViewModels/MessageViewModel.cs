using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Zexus.Models;

namespace Zexus.ViewModels
{
    /// <summary>
    /// Wraps a <see cref="ChatMessage"/> with view-state used by the chat bubble
    /// DataTemplates: streaming flag, tool-call indicators, optional inline status
    /// (Steps 5/6 — collapsed Workspace Panel).
    /// </summary>
    public class MessageViewModel : ViewModelBase
    {
        public MessageRole Role { get; }
        public DateTime Timestamp { get; }

        public bool IsUser => Role == MessageRole.User;
        public bool IsAssistant => Role == MessageRole.Assistant;
        public bool IsSystem => Role == MessageRole.System;

        /// <summary>"You" for user, "Agent" for assistant, empty for system.</summary>
        public string RoleLabel => Role switch
        {
            MessageRole.User => "You",
            MessageRole.Assistant => "Agent",
            _ => string.Empty
        };

        // ── Streaming text content ──
        private string _content;
        public string Content
        {
            get => _content;
            set => SetProperty(ref _content, value);
        }

        private bool _isStreaming;
        public bool IsStreaming
        {
            get => _isStreaming;
            set => SetProperty(ref _isStreaming, value);
        }

        // ── Tool calls (status indicators inside the bubble) ──
        public ObservableCollection<ToolCallViewModel> ToolCalls { get; }
            = new ObservableCollection<ToolCallViewModel>();

        // ── Inline Status (Step 6: replaces Workspace Panel) ──
        private bool _showInlineStatus;
        public bool ShowInlineStatus
        {
            get => _showInlineStatus;
            set => SetProperty(ref _showInlineStatus, value);
        }

        private string _inlineStatusText;
        public string InlineStatusText
        {
            get => _inlineStatusText;
            set => SetProperty(ref _inlineStatusText, value);
        }

        private int _completedSteps;
        public int CompletedSteps
        {
            get => _completedSteps;
            set => SetProperty(ref _completedSteps, value);
        }

        private int _totalSteps;
        public int TotalSteps
        {
            get => _totalSteps;
            set => SetProperty(ref _totalSteps, value);
        }

        private bool _isLogsExpanded;
        public bool IsLogsExpanded
        {
            get => _isLogsExpanded;
            set => SetProperty(ref _isLogsExpanded, value);
        }

        public ObservableCollection<ThinkingChainNode> ThinkingNodes { get; }
            = new ObservableCollection<ThinkingChainNode>();

        // ── Commands ──
        public IRelayCommand CopyCommand { get; }
        public IRelayCommand ToggleLogsCommand { get; }

        public MessageViewModel(ChatMessage msg)
        {
            if (msg == null) throw new ArgumentNullException(nameof(msg));
            Role = msg.Role;
            _content = msg.Content ?? string.Empty;
            Timestamp = msg.Timestamp;

            if (msg.ToolCalls != null)
            {
                foreach (var tc in msg.ToolCalls)
                    ToolCalls.Add(new ToolCallViewModel(tc));
            }

            CopyCommand = new RelayCommand(() =>
            {
                try { System.Windows.Clipboard.SetText(_content ?? string.Empty); }
                catch { /* clipboard contention — non-fatal */ }
            });
            ToggleLogsCommand = new RelayCommand(() => IsLogsExpanded = !IsLogsExpanded);
        }

        /// <summary>Append a streaming text chunk on the UI thread.</summary>
        public void AppendStreamingText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            RunOnUi(() => Content = (_content ?? string.Empty) + text);
        }

        /// <summary>Replace content and tool calls with the final values from the API.</summary>
        public void SetFinalContent(string finalContent, List<ToolCall> finalToolCalls)
        {
            RunOnUi(() =>
            {
                if (!string.IsNullOrEmpty(finalContent) || string.IsNullOrEmpty(_content))
                    Content = finalContent ?? string.Empty;

                if (finalToolCalls != null)
                {
                    var existingNames = ToolCalls.Select(tc => tc.Name).ToList();
                    foreach (var tc in finalToolCalls)
                    {
                        var existing = ToolCalls.FirstOrDefault(t => t.Name == tc.Name && t.Status != tc.Status);
                        if (existing != null) existing.Status = tc.Status;
                        else if (!existingNames.Contains(tc.Name)) ToolCalls.Add(new ToolCallViewModel(tc));
                    }
                }
            });
        }

        /// <summary>Add a tool-call indicator while streaming (called from OnToolExecuting wiring).</summary>
        public void AddOrUpdateToolCall(string name, ToolCallStatus status)
        {
            RunOnUi(() =>
            {
                var existing = ToolCalls.FirstOrDefault(tc => tc.Name == name && tc.Status != ToolCallStatus.Completed);
                if (existing != null) existing.Status = status;
                else ToolCalls.Add(new ToolCallViewModel(new ToolCall { Name = name, Status = status }));
            });
        }
    }

    /// <summary>
    /// Lightweight VM wrapper for <see cref="ToolCall"/> so DataTemplate triggers
    /// on Status (Executing → warning, Completed → success, Failed → error).
    /// </summary>
    public class ToolCallViewModel : ViewModelBase
    {
        public string Name { get; }

        private ToolCallStatus _status;
        public ToolCallStatus Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public ToolCallViewModel(ToolCall tc)
        {
            Name = tc?.Name ?? "";
            _status = tc?.Status ?? ToolCallStatus.Pending;
        }
    }
}
