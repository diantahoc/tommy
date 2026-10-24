using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tommy.Core.Mcp;

namespace Tommy.App.ViewModels;

/// <summary>One editable field in the MCP integration config dialog.</summary>
public partial class McpFieldInput : ObservableObject
{
    public McpFieldInput(McpFieldSpec spec)
    {
        Key = spec.Key;
        Label = spec.Required ? spec.Label + " *" : spec.Label;
        Secret = spec.Secret;
        Boolean = spec.Boolean;
        _value = spec.Default ?? string.Empty;
    }

    public string Key { get; }
    public string Label { get; }
    public bool Secret { get; }

    /// <summary>A yes/no field — rendered as an on/off switch, not a text box.</summary>
    public bool Boolean { get; }

    /// <summary>Plain text box (neither secret nor boolean).</summary>
    public bool IsText => !Secret && !Boolean;

    [ObservableProperty]
    private string _value;

    /// <summary>Bool view over <see cref="Value"/> for the toggle switch; the
    /// canonical value stays the "true"/"false" string the integration maps.</summary>
    public bool BoolValue
    {
        get => Value is "true" or "1" or "yes" or "on";
        set => Value = value ? "true" : "false";
    }

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(BoolValue));
}

/// <summary>
/// Backs the dialog that collects an instanceable MCP's configuration (e.g. the
/// MySQL host/user/password). The values are saved into the chat's settings.
/// </summary>
public partial class McpConfigViewModel : ObservableObject
{
    public McpConfigViewModel(McpCatalogEntry entry)
    {
        Title = $"Connect {entry.DisplayName}";
        foreach (var f in entry.Fields) Fields.Add(new McpFieldInput(f));
    }

    public string Title { get; }
    public ObservableCollection<McpFieldInput> Fields { get; } = new();

    [ObservableProperty]
    private bool _readOnly = true;

    public Dictionary<string, string> Values() =>
        Fields.ToDictionary(f => f.Key, f => f.Value ?? string.Empty);
}
