using System;

namespace Momo.Core.Entities;

public class Revision
{
    public string Id { get; set; } = string.Empty;
    public string TranscriptId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string SnapshotText { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";

    public Transcript? Transcript { get; set; }
}
