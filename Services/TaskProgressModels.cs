using System;
using System.Collections.Generic;

namespace PlumbobForge.Backend.Services;

public enum TaskStepState
{
    Pending,
    Running,
    Completed,
    Warning,
    Error
}

public interface ITaskProgressReporter
{
    void StartStep(string stepId, string title, string? badge = null, double progress = 0.0);
    void UpdateStep(string stepId, string? title = null, string? badge = null, double? progress = null, TaskStepState? state = null);
    void CompleteStep(string stepId, string? finalBadge = null);
    void WarningStep(string stepId, string title, string badge, IEnumerable<string>? details = null);
    void ErrorStep(string stepId, string title, string? errorMessage = null);
}
