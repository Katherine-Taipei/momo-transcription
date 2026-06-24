using System;

namespace Momo.Core.Entities;

public class SpeakerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public byte[] VoiceprintEmbedding { get; set; } = Array.Empty<byte>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
