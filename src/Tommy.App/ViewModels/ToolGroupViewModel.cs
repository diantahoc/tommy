using System.ComponentModel;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Tommy.App.ViewModels;

/// <summary>
/// A collapsible group of tools in the right sidebar. The group switch flips
/// every tool in the group at once (a UI convenience — the underlying
/// permission is still per tool). Reflects the aggregate state of its members.
/// </summary>
public partial class ToolGroupViewModel : ObservableObject
{
    private readonly Action<IEnumerable<string>, bool> _setMany;
    private bool _suppress;

    public ToolGroupViewModel(string name, IEnumerable<ToolToggleViewModel> tools, Action<IEnumerable<string>, bool> setMany)
    {
        Name = name;
        _setMany = setMany;
        foreach (var t in tools)
        {
            Tools.Add(t);
            t.PropertyChanged += OnToolChanged;
        }
        Recalc();
    }

    public string Name { get; }
    public ObservableCollection<ToolToggleViewModel> Tools { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _allEnabled;

    public string CountLabel => $"{Tools.Count(t => t.Enabled)}/{Tools.Count}";

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    private void OnToolChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolToggleViewModel.Enabled)) Recalc();
    }

    private void Recalc()
    {
        _suppress = true;
        AllEnabled = Tools.All(t => t.Enabled);
        _suppress = false;
        OnPropertyChanged(nameof(CountLabel));
    }

    partial void OnAllEnabledChanged(bool value)
    {
        if (_suppress) return;
        _setMany(Tools.Select(t => t.Name).ToList(), value);
    }
}
