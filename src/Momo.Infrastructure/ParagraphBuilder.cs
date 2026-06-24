using System;
using System.Collections.Generic;
using System.Text;
using Momo.Core.Entities;

namespace Momo.Infrastructure;

public class ParagraphBuilder
{
    public class ParagraphItem
    {
        public string Speaker { get; set; } = "Speaker_00";
        public double StartTime { get; set; }
        public double EndTime { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public static List<ParagraphItem> BuildFromWords(List<TranscriptWord> words)
    {
        var result = new List<ParagraphItem>();
        if (words == null || words.Count == 0) return result;

        char[] delimiters = { '。', '！', '？', '.', '!', '?' };

        var currentSentenceSb = new StringBuilder();
        string currentSpeaker = words[0].SpeakerId;
        double sentenceStart = words[0].StartTime;
        double sentenceEnd = words[0].EndTime;

        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w.Word)) continue;

            // If speaker changes, flush first
            if (w.SpeakerId != currentSpeaker)
            {
                if (currentSentenceSb.Length > 0)
                {
                    result.Add(new ParagraphItem
                    {
                        Speaker = currentSpeaker,
                        StartTime = sentenceStart,
                        EndTime = sentenceEnd,
                        Text = currentSentenceSb.ToString().Trim()
                    });
                    currentSentenceSb.Clear();
                }
                currentSpeaker = w.SpeakerId;
                sentenceStart = w.StartTime;
            }

            // Append word to StringBuilder
            if (currentSentenceSb.Length > 0)
            {
                char lastChar = currentSentenceSb[currentSentenceSb.Length - 1];
                char firstChar = w.Word[0];
                // Add space for English words/alphanumeric transitions
                if (lastChar < 128 && lastChar != ' ' && firstChar < 128)
                {
                    currentSentenceSb.Append(' ');
                }
            }
            else
            {
                // Start of a new sentence
                sentenceStart = w.StartTime;
            }

            currentSentenceSb.Append(w.Word);
            sentenceEnd = w.EndTime;

            // Check if we should flush based on delimiters, duration, or length
            double duration = sentenceEnd - sentenceStart;
            bool hasDelimiter = false;
            foreach (char c in w.Word)
            {
                foreach (char d in delimiters)
                {
                    if (c == d)
                    {
                        hasDelimiter = true;
                        break;
                    }
                }
                if (hasDelimiter) break;
            }

            bool lengthExceeded = currentSentenceSb.Length > 120;
            bool durationExceeded = duration >= 6.0;

            if (hasDelimiter || durationExceeded || lengthExceeded)
            {
                result.Add(new ParagraphItem
                {
                    Speaker = currentSpeaker,
                    StartTime = sentenceStart,
                    EndTime = sentenceEnd,
                    Text = currentSentenceSb.ToString().Trim()
                });
                currentSentenceSb.Clear();
            }
        }

        // Flush remaining
        if (currentSentenceSb.Length > 0)
        {
            result.Add(new ParagraphItem
            {
                Speaker = currentSpeaker,
                StartTime = sentenceStart,
                EndTime = sentenceEnd,
                Text = currentSentenceSb.ToString().Trim()
            });
        }

        // De-duplicate contiguous identical paragraphs
        var deduplicated = new List<ParagraphItem>();
        foreach (var item in result)
        {
            if (string.IsNullOrWhiteSpace(item.Text)) continue;

            if (deduplicated.Count > 0)
            {
                var last = deduplicated[deduplicated.Count - 1];
                string lastNormalized = NormalizeText(last.Text);
                string itemNormalized = NormalizeText(item.Text);

                if (last.Speaker == item.Speaker && lastNormalized == itemNormalized)
                {
                    last.EndTime = item.EndTime;
                    continue;
                }
            }
            deduplicated.Add(item);
        }

        return deduplicated;
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }
}
