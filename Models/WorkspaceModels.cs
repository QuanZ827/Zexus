using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace Zexus.Models
{
    public enum ThinkingNodeStatus { Pending, Active, Completed, Failed }

    // ─── Output Preview Panel Models ───

    public enum OutputRecordType
    {
        ScheduleCreated,
        ParameterCreated,
        ParameterSet,
        FileExported,
        FilePrinted
    }

    /// <summary>
    /// One row inside a batch parameter change record.
    /// </summary>
    public class ParameterChangeEntry
    {
        public long ElementId { get; set; }
        public string ElementName { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
    }

    public class OutputRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public OutputRecordType RecordType { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string IconGlyph { get; set; }
        public Color IconColor { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string ToolName { get; set; }

        // ── Navigation fields ──
        public long? ViewId { get; set; }
        public string FilePath { get; set; }
        public List<string> FilePaths { get; set; }
        public string FolderPath { get; set; }

        // ── Batch parameter changes (expandable) ──
        public string ParameterName { get; set; }
        public List<ParameterChangeEntry> ChangeEntries { get; set; }

        // ── Full result data ──
        public Dictionary<string, object> Data { get; set; }

        // ── Computed ──
        public bool IsClickable => ViewId.HasValue || FilePath != null || (FilePaths != null && FilePaths.Count > 0);
        public bool IsExpandable => ChangeEntries != null && ChangeEntries.Count > 0;
    }

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
