using System;

namespace Momo.Core.Entities;

public class JobCheckpoint
{
    public string Id { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string PipelineStage { get; set; } = string.Empty;
    public int ChunkIndexOffset { get; set; } = 0;
    public int TotalChunksCount { get; set; } = 0;
    public string? StatePayload { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Job? Job { get; set; }
}
