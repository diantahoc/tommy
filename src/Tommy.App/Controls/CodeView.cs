using System;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Tommy.App.Controls;

/// <summary>
/// A read-only, syntax-highlighted code view (AvaloniaEdit + TextMate). Bound with a
/// <see cref="Code"/> string and a <see cref="Language"/> hint (a fenced-code language
/// like "bash"/"python" or a bare file extension like "sh"/"cs"). Used for code/shell/
/// HTML blocks in both the chat transcript and the file pane. The TextMate grammar
/// registry is shared per theme so many blocks stay cheap.
/// </summary>
public class CodeView : TextEditor
{
    public static readonly StyledProperty<string?> CodeProperty =
        AvaloniaProperty.Register<CodeView, string?>(nameof(Code));

    public static readonly StyledProperty<string?> LanguageProperty =
        AvaloniaProperty.Register<CodeView, string?>(nameof(Language));

    public string? Code { get => GetValue(CodeProperty); set => SetValue(CodeProperty, value); }
    public string? Language { get => GetValue(LanguageProperty); set => SetValue(LanguageProperty, value); }

    private static RegistryOptions? _darkRegistry;
    private static RegistryOptions? _lightRegistry;

    private readonly RegistryOptions _registry;
    private readonly TextMate.Installation _textMate;

    public CodeView()
    {
        IsReadOnly = true;
        ShowLineNumbers = false;
        WordWrap = true;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(10, 4, 10, 10);
        FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace");
        FontSize = 13;
        Options.AllowScrollBelowDocument = false;

        // Grow to fit content (the outer ScrollViewer scrolls); never scroll internally,
        // so inline code blocks in chat and the file pane expand like plain text would.
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        TextArea.TextView.VisualLinesChanged += (_, _) => FitHeight();

        var dark = (Application.Current?.ActualThemeVariant ?? ThemeVariant.Dark) == ThemeVariant.Dark;
        _registry = dark
            ? _darkRegistry ??= new RegistryOptions(ThemeName.DarkPlus)
            : _lightRegistry ??= new RegistryOptions(ThemeName.LightPlus);
        _textMate = this.InstallTextMate(_registry);
    }

    private void FitHeight()
    {
        var h = TextArea.TextView.DocumentHeight;
        if (h > 0 && double.IsFinite(h) && Math.Abs(Height - h) > 0.5)
            Height = h;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CodeProperty)
        {
            Document ??= new TextDocument();
            Document.Text = Code ?? string.Empty;
        }
        else if (change.Property == LanguageProperty)
        {
            ApplyGrammar();
        }
    }

    private void ApplyGrammar()
    {
        try { _textMate.SetGrammar(ResolveScope(Language)); }
        catch { /* unknown language -> leave unhighlighted */ }
    }

    /// <summary>Map a language hint or file extension to a TextMate scope name, or null if unknown.</summary>
    private string? ResolveScope(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        lang = lang.Trim().ToLowerInvariant().TrimStart('.');
        lang = lang switch
        {
            "sh" or "bash" or "zsh" or "shell" => "shellscript",
            "py" => "python",
            "js" or "mjs" or "cjs" => "javascript",
            "ts" => "typescript",
            "yml" => "yaml",
            "cs" => "csharp",
            "htm" => "html",
            "md" or "markdown" => "markdown",
            "ps1" => "powershell",
            "rb" => "ruby",
            "rs" => "rust",
            "kt" => "kotlin",
            "yaml" or "json" or "html" or "xml" or "css" or "sql" or "go" or "java"
                or "c" or "cpp" or "php" or "python" or "javascript" or "typescript" or "powershell" => lang,
            _ => lang
        };

        var byId = _registry.GetScopeByLanguageId(lang);
        if (!string.IsNullOrEmpty(byId)) return byId;

        var byExt = _registry.GetLanguageByExtension("." + lang);
        return byExt is not null ? _registry.GetScopeByLanguageId(byExt.Id) : null;
    }
}
