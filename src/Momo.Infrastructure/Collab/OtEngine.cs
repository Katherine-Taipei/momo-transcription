using System;

namespace Momo.Infrastructure.Collab;

public class OtOperation
{
    public string ClientId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "insert", "delete", "noop"
    public int Position { get; set; }
    public string Text { get; set; } = string.Empty;
    public int Revision { get; set; }
    public int ParagraphIndex { get; set; }
}

public static class OtEngine
{
    public static (OtOperation op1Prime, OtOperation op2Prime) Transform(OtOperation op1, OtOperation op2)
    {
        var op1Prime = new OtOperation
        {
            ClientId = op1.ClientId,
            Type = op1.Type,
            Position = op1.Position,
            Text = op1.Text,
            Revision = op1.Revision + 1,
            ParagraphIndex = op1.ParagraphIndex
        };

        var op2Prime = new OtOperation
        {
            ClientId = op2.ClientId,
            Type = op2.Type,
            Position = op2.Position,
            Text = op2.Text,
            Revision = op2.Revision + 1,
            ParagraphIndex = op2.ParagraphIndex
        };

        if (op1.Type == "noop" || op2.Type == "noop")
        {
            return (op1Prime, op2Prime);
        }

        if (op1.Type == "insert" && op2.Type == "insert")
        {
            if (op1.Position < op2.Position)
            {
                op2Prime.Position += op1.Text.Length;
            }
            else if (op1.Position > op2.Position)
            {
                op1Prime.Position += op2.Text.Length;
            }
            else
            {
                // Tie breaker by ClientId
                if (string.Compare(op1.ClientId, op2.ClientId, StringComparison.Ordinal) < 0)
                {
                    op2Prime.Position += op1.Text.Length;
                }
                else
                {
                    op1Prime.Position += op2.Text.Length;
                }
            }
        }
        else if (op1.Type == "insert" && op2.Type == "delete")
        {
            int insPos = op1.Position;
            int delPos = op2.Position;
            int delLen = op2.Text.Length;

            if (insPos <= delPos)
            {
                op2Prime.Position += op1.Text.Length;
            }
            else if (insPos >= delPos + delLen)
            {
                op1Prime.Position -= delLen;
            }
            else
            {
                // Insert is inside deleted region
                op1Prime.Position = delPos;
                op2Prime.Position += op1.Text.Length;
            }
        }
        else if (op1.Type == "delete" && op2.Type == "insert")
        {
            var (p2, p1) = Transform(op2, op1);
            op1Prime = p1;
            op2Prime = p2;
        }
        else if (op1.Type == "delete" && op2.Type == "delete")
        {
            int pos1 = op1.Position;
            int len1 = op1.Text.Length;
            int pos2 = op2.Position;
            int len2 = op2.Text.Length;

            if (pos1 < pos2)
            {
                if (pos1 + len1 <= pos2)
                {
                    op2Prime.Position -= len1;
                }
                else if (pos1 + len1 > pos2 && pos1 + len1 < pos2 + len2)
                {
                    op2Prime.Position = pos1;
                    op2Prime.Text = op2.Text.Substring(pos1 + len1 - pos2);
                }
                else
                {
                    op2Prime.Type = "noop";
                    op2Prime.Text = string.Empty;
                }
            }
            else if (pos1 > pos2)
            {
                if (pos2 + len2 <= pos1)
                {
                    op1Prime.Position -= len2;
                }
                else if (pos2 + len2 > pos1 && pos2 + len2 < pos1 + len1)
                {
                    op1Prime.Position = pos2;
                    op1Prime.Text = op1.Text.Substring(pos2 + len2 - pos1);
                }
                else
                {
                    op1Prime.Type = "noop";
                    op1Prime.Text = string.Empty;
                }
            }
            else
            {
                if (len1 < len2)
                {
                    op1Prime.Type = "noop";
                    op1Prime.Text = string.Empty;
                    op2Prime.Text = op2.Text.Substring(len1);
                }
                else if (len1 > len2)
                {
                    op2Prime.Type = "noop";
                    op2Prime.Text = string.Empty;
                    op1Prime.Text = op1.Text.Substring(len2);
                }
                else
                {
                    op1Prime.Type = "noop";
                    op1Prime.Text = string.Empty;
                    op2Prime.Type = "noop";
                    op2Prime.Text = string.Empty;
                }
            }
        }

        return (op1Prime, op2Prime);
    }

    public static string Apply(string text, OtOperation op)
    {
        if (op.Type == "noop" || string.IsNullOrEmpty(op.Type)) return text;

        if (op.Type == "insert")
        {
            int pos = Math.Clamp(op.Position, 0, text.Length);
            return text.Insert(pos, op.Text);
        }

        if (op.Type == "delete")
        {
            int pos = Math.Clamp(op.Position, 0, text.Length);
            int len = Math.Clamp(op.Text.Length, 0, text.Length - pos);
            return text.Remove(pos, len);
        }

        return text;
    }
}
