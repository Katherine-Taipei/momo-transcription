using System;

namespace Momo.Core.Entities;

public class MomoTask
{
    public string Id { get; set; } = string.Empty;
    public string TranscriptId { get; set; } = string.Empty;
    public string ParagraphId { get; set; } = string.Empty;
    public string Assignee { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Status { get; set; } = "open";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Transcript? Transcript { get; set; }
}
