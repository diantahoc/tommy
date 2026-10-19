using CommunityToolkit.Mvvm.ComponentModel;

namespace Tommy.App.ViewModels;

/// <summary>
/// One row in the right-sidebar tool list: a registered tool and whether it's
/// enabled for this chat. Flipping the switch calls back into the owning
/// <see cref="ChatViewModel"/>, which persists the chat's enabled set.
/// </summary>
public partial class ToolToggleViewModel : ObservableObject
{
    private readonly Action<string, bool>? _onChanged;
    private bool _suppress;

    public ToolToggleViewModel(string name, bool enabled, Action<string, bool>? onChanged)
    {
        Name = name;
        _enabled = enabled;
        _onChanged = onChanged;
    }

    public string Name { get; }

    [ObservableProperty]
    private bool _enabled;

    /// <summary>Set the state without firing the change callback (used for group/batch updates).</summary>
    public void SetQuietly(bool value)
    {
        if (_enabled == value) return;
        _suppress = true;
        Enabled = value;
        _suppress = false;
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_suppress) return;
        _onChanged?.Invoke(Name, value);
    }
}
