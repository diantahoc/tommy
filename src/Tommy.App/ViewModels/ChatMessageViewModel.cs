using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Tommy.App.Rendering;

namespace Tommy.App.ViewModels;

public enum ChatRole
{
    User,
    Assistant
}

/// <summary>
/// One message in the transcript. The assistant message grows while streaming:
/// content deltas append to the raw markdown and the block list is re-diffed
/// (completed blocks stay stable, only the trailing block changes); reasoning
/// deltas feed the collapsed thinking bubble.
/// </summary>
public partial class ChatMessageViewModel : ObservableObject
{
    private readonly StringBuilder _rawContent = new();

    public ChatMessageViewModel(ChatRole role, string? initialText = null, bool includeInHistory = true)
    {
        Role = role;
        IncludeInHistory = includeInHistory;
        if (!string.IsNullOrEmpty(initialText))
        {
            _rawContent.Append(initialText);
            RebuildBlocks();
        }
    }

    public ChatRole Role { get; }
    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;

    /// <summary>Whether this message is sent back as conversation history.</summary>
    public bool IncludeInHistory { get; }

    /// <summary>Raw accumulated markdown (also used to build request history).</summary>
    public string RawText => _rawContent.ToString();

    /// <summary>Rendered blocks, one purpose-built control per block.</summary>
    public ObservableCollection<MdBlock> Blocks { get; } = new();

    [ObservableProperty]
    private string _reasoning = string.Empty;

    [ObservableProperty]
    private bool _hasReasoning;

    [ObservableProperty]
    private bool _isReasoningExpanded;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStats))]
    private string? _stats;

    public bool HasStats => !string.IsNullOrEmpty(Stats);

    public void AppendContent(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        _rawContent.Append(delta);
        RebuildBlocks();
    }

    public void AppendReasoning(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        Reasoning += delta;
        if (!HasReasoning) HasReasoning = true;
    }

    private void RebuildBlocks()
    {
        var updated = MarkdownBlockBuilder.Build(_rawContent.ToString());

        // Keep stable (earlier) blocks; replace only the trailing ones that changed.
        for (var i = 0; i < updated.Count; i++)
        {
            if (i < Blocks.Count)
            {
                if (!Blocks[i].Equals(updated[i]))
                    Blocks[i] = updated[i];
            }
            else
            {
                Blocks.Add(updated[i]);
            }
        }

        for (var i = Blocks.Count - 1; i >= updated.Count; i--)
            Blocks.RemoveAt(i);
    }
}
