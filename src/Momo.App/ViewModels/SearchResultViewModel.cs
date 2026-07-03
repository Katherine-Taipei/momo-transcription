using System;
using System.Text.Json.Serialization;

namespace Momo.App.ViewModels;

public class SearchResultViewModel : ViewModelBase
{
    [JsonPropertyName("paragraph_id")]
    public string ParagraphId { get; set; } = string.Empty;

    [JsonPropertyName("paragraph_index")]
    public int ParagraphIndex { get; set; }

    [JsonPropertyName("context")]
    public string Context { get; set; } = string.Empty;

    [JsonPropertyName("start_index")]
    public int StartIndex { get; set; }

    [JsonPropertyName("end_index")]
    public int EndIndex { get; set; }
}
