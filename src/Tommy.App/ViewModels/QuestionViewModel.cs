using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tommy.Core.Tools;

namespace Tommy.App.ViewModels;

/// <summary>
/// A pending question the agent put to the user via <c>ask_question</c>: a prompt,
/// the offered answers as buttons, and (when allowed) a free-text box. Choosing an
/// option or submitting the text completes the tool's awaiting call.
/// </summary>
public partial class QuestionViewModel : ObservableObject
{
    private readonly Action<string?> _respond; // null = dismissed without answering

    public QuestionViewModel(UserQuestion question, Action<string?> respond)
    {
        _respond = respond;
        Question = question.Question;
        Options = new ObservableCollection<QuestionOption>(
            question.Options.Select(o => new QuestionOption(o, _respond)));
        HasOptions = Options.Count > 0;
        AllowCustom = question.AllowCustom;
    }

    public string Question { get; }
    public ObservableCollection<QuestionOption> Options { get; }
    public bool HasOptions { get; }
    public bool AllowCustom { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCustomCommand))]
    private string _customAnswer = string.Empty;

    private bool CanSubmitCustom() => !string.IsNullOrWhiteSpace(CustomAnswer);

    [RelayCommand(CanExecute = nameof(CanSubmitCustom))]
    private void SubmitCustom() => _respond(CustomAnswer.Trim());

    [RelayCommand]
    private void Dismiss() => _respond(null);
}

/// <summary>One offered answer; its command returns that answer to the awaiting tool.</summary>
public sealed partial class QuestionOption
{
    private readonly Action<string> _choose;

    public QuestionOption(string text, Action<string> choose)
    {
        Text = text;
        _choose = choose;
    }

    public string Text { get; }

    [RelayCommand]
    private void Choose() => _choose(Text);
}
