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
        _value = spec.Default ?? string.Empty;
    }

    public string Key { get; }
    public string Label { get; }
    public bool Secret { get; }

    [ObservableProperty]
    private string _value;
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
