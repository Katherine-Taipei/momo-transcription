using System;

namespace Momo.Core.Entities;

public class AudioChunk
{
    public string Id { get; set; } = string.Empty;
    public string MediaFileId { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING"; // 'PENDING', 'COMPLETED', 'FAILED'
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MediaFile? MediaFile { get; set; }
}
