using System;
using System.Collections.Generic;

namespace Momo.Core.Entities;

public class MediaFile
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public double? DurationSeconds { get; set; }
    public long FileSizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Project? Project { get; set; }
    public ICollection<AudioChunk> AudioChunks { get; set; } = new List<AudioChunk>();
}
