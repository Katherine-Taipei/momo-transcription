using System;

namespace Momo.Core.Entities;

public class TelemetryLog
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
