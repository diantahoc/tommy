using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Tommy.App.ViewModels;

/// <summary>A reasoning step shown inside an activity.</summary>
public partial class ThinkingItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _text = string.Empty;

    public void Append(string delta) => Text += delta;
}

/// <summary>
/// Groups one run's reasoning + tool calls into a single collapsible "Working on…"
/// block (collapsed by default), so a multi-tool run reads as one step. Shows a
/// live status while running; the final prose answer renders as its own bubble.
/// </summary>
public partial class ActivityViewModel : ObservableObject
{
    public ObservableCollection<object> Items { get; } = new();

    // One working word per activity, so the status reads steady while the run is live
    // (varying it per reasoning chunk would flicker).
    private readonly string _thinkingWord = ThinkingWords.Next();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private bool _isRunning = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private string _title = "Working…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private string? _status;

    /// <summary>Live status while running, summary title when done.</summary>
    public string HeaderText => IsRunning ? (Status ?? Title) : Title;

    public bool HasItems => Items.Count > 0;

    public void AddThinking(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Items.Add(new ThinkingItemViewModel { Text = text.Trim() });
        OnPropertyChanged(nameof(HasItems));
        Status = _thinkingWord;
    }

    /// <summary>
    /// Append a streamed reasoning delta to the current thinking item (or start one), so
    /// reasoning renders live token-by-token rather than arriving whole at step end.
    /// </summary>
    public void AppendThinking(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        if (Items.Count > 0 && Items[^1] is ThinkingItemViewModel last)
        {
            last.Append(delta);
        }
        else
        {
            Items.Add(new ThinkingItemViewModel { Text = delta });
            OnPropertyChanged(nameof(HasItems));
        }
        Status = _thinkingWord;
    }

    public void AddTool(ToolCallViewModel tool)
    {
        Items.Add(tool);
        OnPropertyChanged(nameof(HasItems));
        Status = tool.IntentLabel;
    }

    public void Finish()
    {
        IsRunning = false;
        var tools = Items.OfType<ToolCallViewModel>().Count();
        Title = tools > 0
            ? $"Worked through {tools} tool call{(tools == 1 ? "" : "s")}"
            : "Thought about it";
        Status = null;
    }
}
