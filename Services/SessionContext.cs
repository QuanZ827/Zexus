using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zexus.Services
{
    /// <summary>
    /// Session Context Manager - persists tool call results, supports resume after interruption.
    /// Solves the issue of losing progress after Rate Limit interruption.
    /// </summary>
    public class SessionContext
    {
        private static SessionContext _instance;
        private static readonly object _lock = new object();

        // Current task state
        public TaskState CurrentTask { get; private set; }

        // Tool call history (last 50 calls)
        public List<ToolCallRecord> ToolHistory { get; private set; } = new List<ToolCallRecord>();

        // Intermediate data cache
        public Dictionary<string, object> DataCache { get; private set; } = new Dictionary<string, object>();

        // Last error
        public ErrorInfo LastError { get; private set; }

        private SessionContext() { }

        public static SessionContext Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new SessionContext();
                    }
                }
                return _instance;
            }
        }

        #region Task Management

        /// <summary>
        /// Start a new task
        /// </summary>
        public void StartTask(string taskDescription)
        {
            CurrentTask = new TaskState
            {
                TaskId = Guid.NewGuid().ToString("N").Substring(0, 8),
                Description = taskDescription,
                Status = TaskStatus.InProgress,
                StartTime = DateTime.Now,
                Steps = new List<TaskStep>()
            };
        }

        /// <summary>
        /// Add a task step
        /// </summary>
        public void AddStep(string stepName, string status = "pending")
        {
            if (CurrentTask == null) return;

            CurrentTask.Steps.Add(new TaskStep
            {
                StepNumber = CurrentTask.Steps.Count + 1,
                Name = stepName,
                Status = status,
                StartTime = DateTime.Now
            });
        }

        /// <summary>
        /// Update current step status
        /// </summary>
        public void UpdateCurrentStep(string status, string result = null)
        {
            if (CurrentTask?.Steps == null || CurrentTask.Steps.Count == 0) return;

            var currentStep = CurrentTask.Steps[CurrentTask.Steps.Count - 1];
            currentStep.Status = status;
            currentStep.Result = result;
            currentStep.EndTime = DateTime.Now;
        }

        /// <summary>
        /// Complete the task
        /// </summary>
        public void CompleteTask(string summary)
        {
            if (CurrentTask == null) return;

            CurrentTask.Status = TaskStatus.Completed;
            CurrentTask.EndTime = DateTime.Now;
            CurrentTask.Summary = summary;
        }

        /// <summary>
        /// Mark task as interrupted (e.g. Rate Limit)
        /// </summary>
        public void InterruptTask(string reason)
        {
            if (CurrentTask == null) return;

            CurrentTask.Status = TaskStatus.Interrupted;
            CurrentTask.InterruptReason = reason;
            CurrentTask.InterruptTime = DateTime.Now;
        }

        #endregion

        #region Tool History

        /// <summary>
        /// Record a tool call
        /// </summary>
        public void RecordToolCall(string toolName, Dictionary<string, object> parameters, object result, bool success)
        {
            var record = new ToolCallRecord
            {
                ToolName = toolName,
                Parameters = parameters,
                Result = result,
                Success = success,
                Timestamp = DateTime.Now
            };

            ToolHistory.Add(record);

            // Keep last 50 records
            if (ToolHistory.Count > 50)
            {
                ToolHistory.RemoveAt(0);
            }
        }

        /// <summary>
        /// Get the most recent call result for a specific tool
        /// </summary>
        public ToolCallRecord GetLastToolCall(string toolName)
        {
            for (int i = ToolHistory.Count - 1; i >= 0; i--)
            {
                if (ToolHistory[i].ToolName == toolName)
                    return ToolHistory[i];
            }
            return null;
        }

        #endregion

        #region Data Cache

        /// <summary>
        /// Cache data (e.g. search results, element lists)
        /// </summary>
        public void CacheData(string key, object data)
        {
            DataCache[key] = data;
        }

        /// <summary>
        /// Get cached data
        /// </summary>
        public T GetCachedData<T>(string key)
        {
            if (DataCache.TryGetValue(key, out var data))
            {
                if (data is T typedData)
                    return typedData;

                // Try JSON deserialization
                try
                {
                    var json = JsonSerializer.Serialize(data);
                    return JsonSerializer.Deserialize<T>(json);
                }
                catch { }
            }
            return default;
        }

        /// <summary>
        /// Check if cached data exists for a key
        /// </summary>
        public bool HasCachedData(string key)
        {
            return DataCache.ContainsKey(key);
        }

        #endregion

        #region Error Handling

        /// <summary>
        /// Record an error
        /// </summary>
        public void RecordError(string errorType, string message, bool isRecoverable)
        {
            LastError = new ErrorInfo
            {
                ErrorType = errorType,
                Message = message,
                IsRecoverable = isRecoverable,
                Timestamp = DateTime.Now
            };

            if (errorType == "rate_limit")
            {
                InterruptTask("API Rate Limit exceeded");
            }
        }

        /// <summary>
        /// Check if there is a recoverable interruption
        /// </summary>
        public bool HasRecoverableInterrupt()
        {
            return CurrentTask?.Status == TaskStatus.Interrupted &&
                   LastError?.IsRecoverable == true;
        }

        #endregion

        #region Context Summary for AI

        /// <summary>
        /// Generate a context summary for the AI to understand the current state
        /// </summary>
        public string GenerateContextSummary()
        {
            var summary = new System.Text.StringBuilder();

            if (CurrentTask != null)
            {
                summary.AppendLine($"## Current Task Context");
                summary.AppendLine($"- Task: {CurrentTask.Description}");
                summary.AppendLine($"- Status: {CurrentTask.Status}");

                if (CurrentTask.Steps.Count > 0)
                {
                    summary.AppendLine($"- Progress: {CurrentTask.Steps.Count} steps completed");
                    summary.AppendLine("- Completed Steps:");
                    foreach (var step in CurrentTask.Steps)
                    {
                        summary.AppendLine($"  {step.StepNumber}. {step.Name}: {step.Status}");
                        if (!string.IsNullOrEmpty(step.Result))
                            summary.AppendLine($"     Result: {step.Result}");
                    }
                }

                if (CurrentTask.Status == TaskStatus.Interrupted)
                {
                    summary.AppendLine($"- Warning: Interrupted: {CurrentTask.InterruptReason}");
                    summary.AppendLine("- User said 'continue' - resume from last successful step");
                }
            }

            // Add cached data summary
            if (DataCache.Count > 0)
            {
                summary.AppendLine("\n## Cached Data Available:");
                foreach (var key in DataCache.Keys)
                {
                    var data = DataCache[key];
                    string dataInfo = data is System.Collections.ICollection col
                        ? $"{col.Count} items"
                        : data?.GetType().Name ?? "null";
                    summary.AppendLine($"- {key}: {dataInfo}");
                }
            }

            return summary.ToString();
        }

        #endregion

        #region Reset

        /// <summary>
        /// Clear current session (called when starting a new task)
        /// </summary>
        public void Reset()
        {
            CurrentTask = null;
            DataCache.Clear();
            LastError = null;
            // Keep ToolHistory for analysis
        }

        #endregion
    }

    #region Data Models

    public class TaskState
    {
        public string TaskId { get; set; }
        public string Description { get; set; }
        public TaskStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime? InterruptTime { get; set; }
        public string InterruptReason { get; set; }
        public string Summary { get; set; }
        public List<TaskStep> Steps { get; set; }
    }

    public class TaskStep
    {
        public int StepNumber { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }  // pending, running, completed, failed
        public string Result { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }

    public enum TaskStatus
    {
        NotStarted,
        InProgress,
        Completed,
        Interrupted,
        Failed
    }

    public class ToolCallRecord
    {
        public string ToolName { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public object Result { get; set; }
        public bool Success { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ErrorInfo
    {
        public string ErrorType { get; set; }  // rate_limit, api_error, tool_error
        public string Message { get; set; }
        public bool IsRecoverable { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion
}
