using System;
using System.Collections.Generic;

namespace Momo.Core.Entities;

public class Transcript
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string MediaFileId { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Project? Project { get; set; }
    public MediaFile? MediaFile { get; set; }
    public ICollection<TranscriptWord> TranscriptWords { get; set; } = new List<TranscriptWord>();
}
