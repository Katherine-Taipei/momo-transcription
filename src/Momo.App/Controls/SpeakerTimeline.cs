using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Momo.App.Controls;

public class TimelineSegment
{
    public string SpeakerId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public double StartTime { get; set; }
    public double EndTime { get; set; }
}

public class SpeakerTimeline : Control
{
    public static readonly StyledProperty<IEnumerable<TimelineSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<SpeakerTimeline, IEnumerable<TimelineSegment>?>(nameof(Segments));

    public static readonly StyledProperty<double> CurrentTimeProperty =
        AvaloniaProperty.Register<SpeakerTimeline, double>(
            nameof(CurrentTime),
            defaultValue: 0.0,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<SpeakerTimeline, double>(nameof(Duration), defaultValue: 0.0);

    public IEnumerable<TimelineSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double CurrentTime
    {
        get => GetValue(CurrentTimeProperty);
        set => SetValue(CurrentTimeProperty, value);
    }

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    static SpeakerTimeline()
    {
        AffectsRender<SpeakerTimeline>(SegmentsProperty, CurrentTimeProperty, DurationProperty);
    }

    private readonly IBrush[] _colorPalette = new IBrush[]
    {
        new SolidColorBrush(Color.Parse("#8B5CF6")), // Purple
        new SolidColorBrush(Color.Parse("#10B981")), // Green
        new SolidColorBrush(Color.Parse("#F59E0B")), // Orange
        new SolidColorBrush(Color.Parse("#EF4444")), // Red
        new SolidColorBrush(Color.Parse("#3B82F6")), // Blue
        new SolidColorBrush(Color.Parse("#EC4899")), // Pink
        new SolidColorBrush(Color.Parse("#06B6D4")), // Cyan
        new SolidColorBrush(Color.Parse("#10B981")), // Teal
    };

    private IBrush GetSpeakerBrush(string speakerId)
    {
        if (string.IsNullOrEmpty(speakerId)) return _colorPalette[0];
        
        // Simple hash to get stable color
        int hash = 0;
        foreach (char c in speakerId)
        {
            hash += c;
        }
        return _colorPalette[hash % _colorPalette.Length];
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Draw track background
        context.DrawRectangle(
            new SolidColorBrush(Color.Parse("#1A1C29")),
            new Pen(new SolidColorBrush(Color.Parse("#25273C")), 1),
            new Rect(0, 0, bounds.Width, bounds.Height));

        var segments = Segments?.ToList();
        double duration = Duration;

        if (duration <= 0 || segments == null || segments.Count == 0)
        {
            // Draw placeholder text if no segments
            var placeholderText = new FormattedText(
                "No timeline data available",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Inter,Segoe UI,Arial"),
                12,
                new SolidColorBrush(Color.Parse("#6D7090")));
            
            context.DrawText(placeholderText, new Point(10, (bounds.Height - placeholderText.Height) / 2));
            return;
        }

        double scale = bounds.Width / duration;

        // Render each speaker segment block
        foreach (var seg in segments)
        {
            if (seg.EndTime <= seg.StartTime) continue;

            double left = seg.StartTime * scale;
            double width = (seg.EndTime - seg.StartTime) * scale;
            width = Math.Max(width, 1.0); // Make sure it's at least visible

            var rect = new Rect(left, 2, width, bounds.Height - 4);
            var brush = GetSpeakerBrush(seg.SpeakerId);

            // Draw block with rounded corners
            context.DrawRectangle(brush, null, rect, 4, 4);

            // Draw speaker name text inside the block if it fits
            if (width > 50)
            {
                var text = new FormattedText(
                    seg.DisplayName,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Inter,Segoe UI,Arial", FontStyle.Normal, FontWeight.Bold),
                    10,
                    Brushes.White);

                // Clip text inside the rectangle bounds
                using (context.PushClip(rect))
                {
                    context.DrawText(text, new Point(left + 5, (bounds.Height - text.Height) / 2));
                }
            }
        }

        // Draw playhead vertical line
        double playheadX = CurrentTime * scale;
        playheadX = Math.Clamp(playheadX, 0, bounds.Width);

        var playheadPen = new Pen(new SolidColorBrush(Color.Parse("#34D399")), 2); // green line
        context.DrawLine(playheadPen, new Point(playheadX, 0), new Point(playheadX, bounds.Height));

        // Draw a tiny playhead handle at the top
        var playheadHandleBrush = Color.Parse("#34D399");
        context.DrawGeometry(
            new SolidColorBrush(playheadHandleBrush),
            null,
            new PathGeometry
            {
                Figures = new PathFigures
                {
                    new PathFigure
                    {
                        StartPoint = new Point(playheadX - 6, 0),
                        Segments = new PathSegments
                        {
                            new LineSegment { Point = new Point(playheadX + 6, 0) },
                            new LineSegment { Point = new Point(playheadX, 6) }
                        },
                        IsClosed = true
                    }
                }
            });
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        
        var pos = e.GetPosition(this);
        double duration = Duration;
        if (duration <= 0) return;

        double width = Bounds.Width;
        if (width <= 0) width = Width;
        if (double.IsNaN(width) || width <= 0) width = 200;

        double clickedTime = (pos.X / width) * duration;
        clickedTime = Math.Clamp(clickedTime, 0, duration);

        // Find if a segment is clicked, and jump to its start time
        var segments = Segments?.ToList();
        if (segments != null && segments.Count > 0)
        {
            var clickedSegment = segments.FirstOrDefault(s => clickedTime >= s.StartTime && clickedTime <= s.EndTime);
            if (clickedSegment != null)
            {
                CurrentTime = clickedSegment.StartTime;
                e.Handled = true;
                return;
            }
        }

        CurrentTime = clickedTime;
        e.Handled = true;
    }
}
