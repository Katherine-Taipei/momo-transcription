using System;

namespace Momo.Core.Entities;

public class Job
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string MediaFileId { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING"; // 'PENDING', 'RUNNING', 'COMPLETED', 'FAILED', 'RETRY', 'PAUSED', 'CANCELLED'
    public int Priority { get; set; } = 5;
    public int RetryCount { get; set; } = 0;
    public int MaxRetries { get; set; } = 3;
    public string? ErrorMessage { get; set; }
    public bool Diarization { get; set; } = false;
    public bool Alignment { get; set; } = false;
    public string? SelectedGlossaries { get; set; }
    public string? SelectedRole { get; set; }
    public string? SelectedTemplate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Project? Project { get; set; }
    public MediaFile? MediaFile { get; set; }
}
