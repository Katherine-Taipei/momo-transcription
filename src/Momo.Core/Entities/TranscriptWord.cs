namespace Momo.Core.Entities;

public class TranscriptWord
{
    public int Id { get; set; }
    public string TranscriptId { get; set; } = string.Empty;
    public string Word { get; set; } = string.Empty;
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public string SpeakerId { get; set; } = "Speaker_00";
    public double Confidence { get; set; }

    public Transcript? Transcript { get; set; }
}
