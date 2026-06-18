using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace VoiceAgend.App.Views;

/// <summary>
/// ViewModel für einen Eintrag in der Pro-Sprache-Prompt-Liste der ProfileView.
/// de/en sind nicht entfernbar (eigene Server-Spalten), Zusatzsprachen schon.
/// </summary>
public sealed class PromptLangVm : INotifyPropertyChanged
{
    public string Code { get; }
    public string DisplayName { get; }
    public bool Removable { get; }

    private string _prompt;
    public string Prompt
    {
        get => _prompt;
        set { if (_prompt != value) { _prompt = value; OnChanged(); } }
    }

    public Visibility RemovableVisibility => Removable ? Visibility.Visible : Visibility.Collapsed;

    public PromptLangVm(string code, string displayName, bool removable, string prompt)
    {
        Code = code;
        DisplayName = displayName;
        Removable = removable;
        _prompt = prompt ?? "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
