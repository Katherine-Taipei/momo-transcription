using System;

namespace Momo.Core.Entities;

public class EmbeddingCache
{
    public string TextHash { get; set; } = string.Empty;
    public string EmbeddingJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
