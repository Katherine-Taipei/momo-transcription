using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Momo.App.ViewModels;

namespace Momo.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
        {
            vm.ScrollToParagraphRequested += OnScrollToParagraphRequested;
        }
    }

    private void OnScrollToParagraphRequested(int index, int startIdx, int length)
    {
        ScrollToParagraph(index);
        
        var itemsControl = this.FindControl<ItemsControl>("ParagraphsItemsControl");
        if (itemsControl != null)
        {
            // Wait for visual containers to realize/scroll
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var panel = itemsControl.Presenter?.Panel;
                if (panel != null && index >= 0 && index < panel.Children.Count)
                {
                    var border = panel.Children[index] as Border;
                    if (border != null)
                    {
                        var textBox = FindVisualChild<TextBox>(border);
                        if (textBox != null)
                        {
                            textBox.Focus();
                            textBox.SelectionStart = startIdx;
                            textBox.SelectionEnd = startIdx + length;
                        }
                    }
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void ScrollToParagraph(int index)
    {
        var itemsControl = this.FindControl<ItemsControl>("ParagraphsItemsControl");
        var scrollViewer = this.FindControl<ScrollViewer>("ParagraphScrollViewer");
        if (itemsControl != null && scrollViewer != null)
        {
            var panel = itemsControl.Presenter?.Panel;
            if (panel != null && index >= 0 && index < panel.Children.Count)
            {
                var child = panel.Children[index];
                var visualChild = child as Avalonia.Visual;
                if (visualChild != null)
                {
                    var relativePos = visualChild.TranslatePoint(new Avalonia.Point(0, 0), scrollViewer.Presenter?.Content as Avalonia.Visual);
                    if (relativePos.HasValue)
                    {
                        scrollViewer.Offset = new Avalonia.Vector(scrollViewer.Offset.X, relativePos.Value.Y);
                    }
                }
            }
        }
    }

    private T? FindVisualChild<T>(Avalonia.Visual visual) where T : class
    {
        if (visual is T target) return target;
        foreach (var child in visual.GetVisualChildren())
        {
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private void Window_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.KeyModifiers == Avalonia.Input.KeyModifiers.Control && e.Key == Avalonia.Input.Key.F)
        {
            var searchTextBox = this.FindControl<TextBox>("SearchTextBox");
            if (searchTextBox != null)
            {
                searchTextBox.Focus();
                e.Handled = true;
            }
        }
    }

    private void TextBox_KeyUp(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        OnCursorMoved(sender);
    }

    private void TextBox_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        OnCursorMoved(sender);
    }

    private void OnCursorMoved(object? sender)
    {
        if (sender is TextBox tb && tb.DataContext is ParagraphViewModel pvm)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SubmitLocalCursor(pvm, tb.CaretIndex);
            }
        }
    }
}
