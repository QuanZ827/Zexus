using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Zexus.Bridge
{
    /// <summary>
    /// Heuristic detection of write-like Revit API patterns. Used to force
    /// confirmation even when the caller declared isWriteOperation=false.
    /// This is NOT a complete safety analysis; it can both miss and over-match.
    /// </summary>
    public static class BridgeWriteSignal
    {
        private sealed class Pattern
        {
            public Regex Regex;
            public string Name;
        }

        private static readonly Pattern[] Patterns = new[]
        {
            P(@"\bnew\s+(Autodesk\.Revit\.DB\.)?Transaction\b", "Transaction"),
            P(@"\bSubTransaction\b", "SubTransaction"),
            P(@"\bTransactionGroup\b", "TransactionGroup"),
            P(@"\.\s*Start\s*\(", ".Start("),
            P(@"\.\s*Commit\s*\(", ".Commit("),
            P(@"\bdoc(ument)?\s*\.\s*Delete\s*\(", "Document.Delete"),
            P(@"\.\s*Set\s*\(", ".Set("),
            P(@"\bWall\.Create\b", "Wall.Create"),
            P(@"\bNewFamilyInstance\b", "NewFamilyInstance"),
            P(@"\bFamilyInstance\b", "FamilyInstance"),
            P(@"\b(Level|Grid|Floor|Roof|Room|Ceiling|Column)\s*\.\s*Create\b", "Element.Create"),
            P(@"\bCreate\.New\w+\b", "Create.New*")
        };

        private static Pattern P(string regex, string name)
        {
            return new Pattern
            {
                Regex = new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.Compiled),
                Name = name
            };
        }

        public static bool HasWriteSignal(string code)
        {
            return DetectedPatterns(code).Count > 0;
        }

        public static List<string> DetectedPatterns(string code)
        {
            var detected = new List<string>();
            if (string.IsNullOrEmpty(code)) return detected;
            foreach (var pattern in Patterns)
            {
                if (pattern.Regex.IsMatch(code)) detected.Add(pattern.Name);
            }
            return detected;
        }
    }
}
