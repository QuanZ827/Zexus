using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace Zexus.Models
{
    public enum ThinkingNodeStatus { Pending, Active, Completed, Failed }

    public class ThinkingChainNode
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public ThinkingNodeStatus Status { get; set; }
        public string IconGlyph { get; set; }
        public Color NodeColor { get; set; }
        public DateTime Timestamp { get; set; }

        // ── Rich data fields ──
        public string ToolName { get; set; }
        public string Description { get; set; }
        public string CodeSnippet { get; set; }
        public string Output { get; set; }
        public Dictionary<string, object> InputParams { get; set; }
        public Dictionary<string, object> ResultData { get; set; }
        public long DurationMs { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class WorkspaceState
    {
        public string TaskName { get; set; }
        public bool IsActive { get; set; }
        public int CompletedSteps { get; set; }
        public int TotalExpectedSteps { get; set; }
        public DateTime StartTime { get; set; }
        public List<ThinkingChainNode> ThinkingChain { get; set; } = new List<ThinkingChainNode>();
    }
}
