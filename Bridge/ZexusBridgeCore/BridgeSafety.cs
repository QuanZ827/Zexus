using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Zexus.Bridge
{
    /// <summary>
    /// DEVELOPMENT-ONLY heuristic safety checks. String/regex matching is NOT a real
    /// sandbox: compiled code runs with the full privileges of the Revit process.
    /// These checks only catch obvious mistakes and accidental dangerous calls.
    /// </summary>
    public static class BridgeSafety
    {
        private sealed class Rule
        {
            public Regex Pattern;
            public string Reason;
        }

        private static readonly Rule[] Rules = CreateRules();

        private static Rule[] CreateRules()
        {
            var specs = new (string Pattern, string Reason)[]
            {
                (@"\bSystem\.IO\b", "System.IO is blocked (file system access)"),
                (@"\bSystem\.Net\b", "System.Net is blocked (network access)"),
                (@"Process\s*\.\s*Start\s*\(", "Process.Start is blocked (process launch)"),
                (@"\bSystem\.Diagnostics\.Process\b", "System.Diagnostics.Process is blocked"),
                (@"PowerShell", "PowerShell is blocked"),
                (@"cmd\.exe", "cmd.exe is blocked"),
                (@"Assembly\s*\.\s*Load", "Reflection loading of arbitrary assemblies is blocked"),
                (@"\bAppDomain\b", "AppDomain manipulation is blocked"),
                (@"Environment\s*\.\s*Exit\s*\(", "Environment.Exit is blocked"),
                (@"Thread\s*\.\s*Sleep\s*\(", "Thread.Sleep is blocked (use Task.Delay or restructure)"),
                (@"while\s*\(\s*true\s*\)", "Obvious infinite loop (while(true))"),
                (@"for\s*\(\s*;\s*;\s*\)", "Obvious infinite loop (for(;;))")
            };

            var rules = new Rule[specs.Length];
            for (int i = 0; i < specs.Length; i++)
            {
                rules[i] = new Rule
                {
                    Pattern = new Regex(specs[i].Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled),
                    Reason = specs[i].Reason
                };
            }
            return rules;
        }

        /// <summary>Returns safety violations; empty means no obvious danger detected.</summary>
        public static List<string> Check(string code)
        {
            var violations = new List<string>();
            if (string.IsNullOrEmpty(code)) return violations;

            foreach (var rule in Rules)
            {
                if (rule.Pattern.IsMatch(code))
                    violations.Add(rule.Reason);
            }
            return violations;
        }
    }
}
