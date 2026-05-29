using System.Collections.Generic;
using System.Linq;

namespace Zexus.Models
{
    /// <summary>
    /// Structured Revit selection snapshot. Built by App.BuildSelectionInfo on the Revit
    /// thread (no WPF types — safe to hand across to the UI thread). Replaces the flat
    /// summary string the Selection Inspector used in v0.2, while keeping
    /// <see cref="SummaryLine"/> for SessionContext / Working Memory backward compat.
    /// </summary>
    public class SelectionInfo
    {
        // ── Single element ──
        public string CategoryName { get; set; }       // e.g. "Walls"
        public string FamilyAndType { get; set; }       // e.g. "Basic Wall: Interior - 135mm"
        public long ElementId { get; set; }
        public string LevelName { get; set; }           // null if not available
        public string WorksetName { get; set; }         // null if not workshared
        public string Mark { get; set; }                // null if no mark parameter

        // ── Multi-select ──
        public int TotalCount { get; set; }
        public Dictionary<string, int> CategoryBreakdown { get; set; } // category → count

        // ── Flat summary (kept for SessionContext backward compat) ──
        public string SummaryLine { get; set; }

        // ── Computed display helpers (let XAML reuse BoolToVisibilityConverter) ──
        public bool IsSingleElement => TotalCount == 1;

        public bool HasLevel => !string.IsNullOrEmpty(LevelName);
        public bool HasWorkset => !string.IsNullOrEmpty(WorksetName);
        public bool HasMark => !string.IsNullOrEmpty(Mark);

        /// <summary>Primary line under "ACTIVE SELECTION": family/type for single, count for multi.</summary>
        public string PrimaryLine => IsSingleElement
            ? (string.IsNullOrEmpty(FamilyAndType) ? (CategoryName ?? "Element") : FamilyAndType)
            : $"{TotalCount} elements selected";

        public string ElementIdLabel => $"Id: {ElementId}";

        /// <summary>Icon-container glyph: category initial for single, count for multi.</summary>
        public string IconText => IsSingleElement
            ? (string.IsNullOrEmpty(CategoryName) ? "?" : CategoryName.Substring(0, 1).ToUpperInvariant())
            : TotalCount.ToString();

        /// <summary>"Walls(5)  ·  Floors(4)  ·  Doors(3)" for the multi-select line.</summary>
        public string CategoryBreakdownText
        {
            get
            {
                if (CategoryBreakdown == null || CategoryBreakdown.Count == 0) return "";
                var parts = CategoryBreakdown
                    .OrderByDescending(kv => kv.Value)
                    .Take(5)
                    .Select(kv => $"{kv.Key}({kv.Value})");
                var text = string.Join("  ·  ", parts);
                if (CategoryBreakdown.Count > 5) text += $"  +{CategoryBreakdown.Count - 5} more";
                return text;
            }
        }
    }
}
