using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.Engine.Agent;

namespace Tommy.App.ViewModels;

/// <summary>
/// A pending approval prompt for a gated tool call. A write shows the actual
/// diff, not a bare "allow?". Allow/Deny complete the loop's awaiting handler.
/// </summary>
public partial class ApprovalViewModel : ObservableObject
{
    private readonly Action<bool> _respond;

    public ApprovalViewModel(ApprovalRequest request, Action<bool> respond)
    {
        _respond = respond;
        ToolName = request.ToolName;
        ArgsJson = request.ArgumentsJson;
        TierLabel = request.Tier.ToString().ToLowerInvariant();
        Diff = request.Preview?.Diff;
        Summary = request.Preview?.Summary ?? $"Run {request.ToolName}?";
        HasDiff = !string.IsNullOrEmpty(Diff);
    }

    public string ToolName { get; }
    public string ArgsJson { get; }
    public string TierLabel { get; }
    public string Summary { get; }
    public string? Diff { get; }
    public bool HasDiff { get; }

    [RelayCommand]
    private void Allow() => _respond(true);

    [RelayCommand]
    private void Deny() => _respond(false);
}
