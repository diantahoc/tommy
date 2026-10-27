using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.Core.Chat;
using Tommy.Core.Mcp;

namespace Tommy.App.ViewModels;

/// <summary>One entry in the "add MCP" picker: a configured server or an integration.</summary>
public sealed record McpCatalogEntry(string Key, string DisplayName, bool IsIntegration, IReadOnlyList<McpFieldSpec> Fields);

/// <summary>Outcome of connecting a chat's MCP server.</summary>
public sealed record McpConnectResult(bool Ok, IReadOnlyList<string> ToolNames, string? Error);

/// <summary>
/// The seam a <see cref="ChatViewModel"/> uses to connect its MCP servers. The
/// shell implements it (it owns the manager, the integration registry, and the
/// saved-server config); the chat only decides which servers it wants.
/// </summary>
public interface IMcpConnector
{
    /// <summary>Configured servers + integrations the chat can connect.</summary>
    IReadOnlyList<McpCatalogEntry> McpCatalog { get; }

    Task<McpConnectResult> ConnectChatMcpAsync(ChatMcpRef reference);

    Task DisconnectChatMcpAsync(string id);
}

/// <summary>A row in the sidebar's "Connected MCPs" list for the active chat.</summary>
public partial class ConnectedMcpViewModel : ObservableObject
{
    private readonly Func<ConnectedMcpViewModel, Task> _remove;
    private readonly Func<ConnectedMcpViewModel, Task>? _reconnect;

    public ConnectedMcpViewModel(ChatMcpRef reference, Func<ConnectedMcpViewModel, Task> remove,
        Func<ConnectedMcpViewModel, Task>? reconnect = null)
    {
        Reference = reference;
        DisplayName = string.IsNullOrWhiteSpace(reference.DisplayName) ? reference.Id : reference.DisplayName;
        _remove = remove;
        _reconnect = reconnect;
    }

    public ChatMcpRef Reference { get; }
    public string Id => Reference.Id;
    public string DisplayName { get; }

    [ObservableProperty]
    private string _status = "connecting…";

    /// <summary>True after a failed connect — surfaces the Reconnect affordance.</summary>
    [ObservableProperty]
    private bool _failed;

    [RelayCommand]
    private Task Remove() => _remove(this);

    [RelayCommand]
    private Task Reconnect() => _reconnect?.Invoke(this) ?? Task.CompletedTask;
}
