using ReactiveUI;

namespace Momo.App.ViewModels;

public class DiffRowViewModel : ViewModelBase
{
    private string _oldText = string.Empty;
    private string _newText = string.Empty;
    private string _oldBackground = "Transparent";
    private string _newBackground = "Transparent";
    private string _oldForeground = "White";
    private string _newForeground = "White";

    public string OldText
    {
        get => _oldText;
        set => this.RaiseAndSetIfChanged(ref _oldText, value);
    }

    public string NewText
    {
        get => _newText;
        set => this.RaiseAndSetIfChanged(ref _newText, value);
    }

    public string OldBackground
    {
        get => _oldBackground;
        set => this.RaiseAndSetIfChanged(ref _oldBackground, value);
    }

    public string NewBackground
    {
        get => _newBackground;
        set => this.RaiseAndSetIfChanged(ref _newBackground, value);
    }

    public string OldForeground
    {
        get => _oldForeground;
        set => this.RaiseAndSetIfChanged(ref _oldForeground, value);
    }

    public string NewForeground
    {
        get => _newForeground;
        set => this.RaiseAndSetIfChanged(ref _newForeground, value);
    }
}
