using Avalonia.Controls;
using Momo.App.ViewModels;

namespace Momo.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
