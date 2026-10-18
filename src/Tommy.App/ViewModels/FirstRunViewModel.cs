using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.Core.Configuration;
using Tommy.Core.Engine;
using Tommy.Persistence;
using Tommy.Persistence.Setup;

namespace Tommy.App.ViewModels;

/// <summary>
/// First-run setup: collect a managed or remote engine profile, validate that
/// the dir/script exist (managed) or the URL is well-formed, then save it as the
/// first profile and continue to the chat window.
/// </summary>
public partial class FirstRunViewModel : ObservableObject
{
    private readonly IConfigStore _store;

    public FirstRunViewModel(IConfigStore store)
    {
        _store = store;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemote))]
    private bool _isManaged;

    public bool IsRemote => !IsManaged;

    [ObservableProperty]
    private string _profileName = "default";

    [ObservableProperty]
    private string _baseUrl = "http://127.0.0.1:8080/v1";

    [ObservableProperty]
    private string _installDir = string.Empty;

    [ObservableProperty]
    private string _runScript = string.Empty;

    [ObservableProperty]
    private string? _error;

    /// <summary>Raised with the saved config + active profile when setup completes.</summary>
    public event Action<TommyConfig, EngineProfile>? Completed;

    [RelayCommand]
    private void Save()
    {
        var profile = new EngineProfile
        {
            Name = string.IsNullOrWhiteSpace(ProfileName) ? "default" : ProfileName.Trim(),
            Mode = IsManaged ? EngineMode.Managed : EngineMode.Remote,
            BaseUrl = BaseUrl.Trim(),
            InstallDir = IsManaged ? InstallDir.Trim() : null,
            RunScript = IsManaged ? RunScript.Trim() : null
        };

        var result = FirstRunValidator.Validate(profile);
        if (!result.Ok)
        {
            Error = result.Error;
            return;
        }

        Error = null;
        var config = _store.Load();
        config.EngineProfiles.Add(profile);
        config.ActiveProfileName = profile.Name;
        _store.Save(config);

        Completed?.Invoke(config, profile);
    }
}
