using CommunityToolkit.Mvvm.ComponentModel;
using Tommy.Core.Tools;

namespace Tommy.App.ViewModels;

/// <summary>
/// A tool call shown in the transcript: the proposed call (name, tier, args),
/// an optional diff preview while it runs, and its result when it finishes.
/// </summary>
public partial class ToolCallViewModel : ObservableObject
{
    public ToolCallViewModel(string callId, string toolName, string argsJson, PermissionTier tier, string? previewDiff)
    {
        CallId = callId;
        ToolName = toolName;
        ArgsJson = argsJson;
        Tier = tier;
        Detail = previewDiff;
        HasDetail = !string.IsNullOrEmpty(previewDiff);
        IsDiff = HasDetail;
    }

    public string CallId { get; }
    public string ToolName { get; }
    public string ArgsJson { get; }
    public PermissionTier Tier { get; }

    public string TierLabel => Tier.ToString().ToLowerInvariant();

    [ObservableProperty]
    private string _status = "running…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string? _detail;

    [ObservableProperty]
    private bool _hasDetail;

    [ObservableProperty]
    private bool _isDiff;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private bool _done;

    public void Complete(ToolResult result)
    {
        Done = true;
        IsError = !result.Success;
        Status = result.Success ? "done" : "failed";
        if (!string.IsNullOrEmpty(result.Content))
        {
            Detail = result.Content;
            HasDetail = true;
            IsDiff = result.Kind == ToolResultKind.Diff;
        }
    }
}
