using System.Collections.Generic;

namespace Zexus.Models
{
    public class ApiResponse
    {
        public bool Success { get; set; }
        public string Text { get; set; }
        public string Error { get; set; }
        public string StopReason { get; set; }
        public List<ToolUse> ToolCalls { get; set; } = new List<ToolUse>();
    }

    public class ToolUse
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Dictionary<string, object> Input { get; set; }
    }
}
