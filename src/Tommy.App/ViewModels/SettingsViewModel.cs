using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.Core.Configuration;
using Tommy.Core.Mcp;
using Tommy.Core.Skills;

namespace Tommy.App.ViewModels;

/// <summary>One saved MCP server shown in the settings list.</summary>
public sealed record McpServerRow(string Id, string Name, string Transport, bool Connected, int ToolCount)
{
    public string StatusLabel => Connected ? $"connected · {ToolCount} tools" : "not connected";
}

/// <summary>
/// Backs the Settings window: the cost-meter rate, the installed skills, and the
/// user's MCP servers (view / add / remove). Reads and writes through the shell,
/// which owns persistence and the MCP manager.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _rateText = shell.ElectricityRate.ToString(CultureInfo.InvariantCulture);
        _currency = shell.Currency;
        _engineToken = shell.EngineAuthToken ?? string.Empty;
        _searxngUrl = shell.SearxngUrl ?? string.Empty;
        _managedEnabled = shell.SearxngManagedEnabled;
        _releaseUrl = shell.SearxngReleaseUrl ?? string.Empty;
        LoadSkills();
        LoadMcp();
    }

    // --- Engine -------------------------------------------------------------

    public string EngineBaseUrl => _shell.EngineBaseUrl;

    [ObservableProperty] private string _engineToken;
    [ObservableProperty] private string _engineSaveStatus = string.Empty;

    [RelayCommand]
    private void SaveEngine()
    {
        _shell.SaveEngineAuthToken(EngineToken);
        EngineSaveStatus = string.IsNullOrWhiteSpace(EngineToken)
            ? "Saved — no token; requests are sent unauthenticated."
            : "Saved — token applied to new requests.";
    }

    // --- Web search ---------------------------------------------------------

    /// <summary>Exposed so the UI can bind the live managed-install status/flag on the shell.</summary>
    public ShellViewModel Shell => _shell;

    [ObservableProperty] private string _searxngUrl;
    [ObservableProperty] private bool _managedEnabled;
    [ObservableProperty] private string _releaseUrl;
    [ObservableProperty] private string _webSearchStatus = string.Empty;

    [RelayCommand]
    private void SaveWebSearch()
    {
        _shell.SaveWebSearchSettings(SearxngUrl, ManagedEnabled, ReleaseUrl);
        WebSearchStatus = !string.IsNullOrWhiteSpace(SearxngUrl)
            ? "Saved — using the custom SearXNG URL."
            : ManagedEnabled
                ? "Saved — using the managed SearXNG (when installed)."
                : "Saved — using the bundled search provider.";
    }

    [RelayCommand]
    private Task InstallManaged() => _shell.InstallManagedSearxngAsync();

    // --- General ------------------------------------------------------------

    [ObservableProperty] private string _rateText;
    [ObservableProperty] private string _currency = "$";
    [ObservableProperty] private string _generalStatus = string.Empty;

    [RelayCommand]
    private void SaveGeneral()
    {
        double.TryParse(RateText, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate);
        _shell.SaveGeneralSettings(rate, Currency);
        RateText = _shell.ElectricityRate.ToString(CultureInfo.InvariantCulture);
        GeneralStatus = rate > 0 ? "Saved — cost meter on." : "Saved — cost meter off (rate is 0).";
    }

    // --- Skills -------------------------------------------------------------

    public ObservableCollection<Skill> Skills { get; } = new();

    [RelayCommand] private void ReloadSkills() => LoadSkills();
    [RelayCommand] private void OpenSkillsFolder() => _shell.OpenSkillsFolder();

    private void LoadSkills()
    {
        Skills.Clear();
        foreach (var s in _shell.Skills()) Skills.Add(s);
    }

    // --- MCP ----------------------------------------------------------------

    public ObservableCollection<McpServerRow> McpServers { get; } = new();

    [ObservableProperty] private string _newId = string.Empty;
    [ObservableProperty] private bool _newIsHttp;
    [ObservableProperty] private string _newCommand = string.Empty;
    [ObservableProperty] private string _newArgs = string.Empty;
    [ObservableProperty] private string _newUrl = string.Empty;
    [ObservableProperty] private bool _newReadOnly;
    [ObservableProperty] private bool _newAuthIsBearer;
    [ObservableProperty] private string _newAuthToken = string.Empty;
    [ObservableProperty] private string _mcpStatus = string.Empty;

    private void LoadMcp()
    {
        McpServers.Clear();
        foreach (var e in _shell.SavedMcpServers)
            McpServers.Add(new McpServerRow(e.Id, e.DisplayName, e.Transport.ToString(),
                _shell.IsMcpConnected(e.Id), _shell.McpToolCount(e.Id)));
    }

    [RelayCommand]
    private async Task AddMcp()
    {
        if (string.IsNullOrWhiteSpace(NewId)) { McpStatus = "A short id is required."; return; }
        if (NewIsHttp && string.IsNullOrWhiteSpace(NewUrl)) { McpStatus = "An HTTP server needs a URL."; return; }
        if (!NewIsHttp && string.IsNullOrWhiteSpace(NewCommand)) { McpStatus = "A stdio server needs a command."; return; }

        var entry = new McpServerEntry
        {
            Id = NewId.Trim(),
            DisplayName = NewId.Trim(),
            Transport = NewIsHttp ? McpTransportKind.Http : McpTransportKind.Stdio,
            Command = NewIsHttp ? null : NewCommand.Trim(),
            Args = NewIsHttp ? new List<string>() : SplitArgs(NewArgs),
            Url = NewIsHttp ? NewUrl.Trim() : null,
            ReadOnly = NewReadOnly,
            AuthType = NewIsHttp && NewAuthIsBearer ? McpAuthType.BearerToken : McpAuthType.None,
            AuthToken = NewIsHttp && NewAuthIsBearer ? NewAuthToken.Trim() : null
        };

        McpStatus = "Connecting…";
        var count = await _shell.AddMcpServerAsync(entry);
        McpStatus = count > 0
            ? $"Connected — {count} tools registered."
            : "Saved, but connected with 0 tools (check the command/URL).";

        NewId = NewCommand = NewArgs = NewUrl = NewAuthToken = string.Empty;
        NewReadOnly = false;
        NewAuthIsBearer = false;
        LoadMcp();
    }

    [RelayCommand]
    private async Task RemoveMcp(McpServerRow? row)
    {
        if (row is null) return;
        await _shell.RemoveMcpServerAsync(row.Id);
        LoadMcp();
    }

    private static List<string> SplitArgs(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? new List<string>()
            : s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
