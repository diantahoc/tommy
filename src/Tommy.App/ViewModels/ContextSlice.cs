using Avalonia.Media;

namespace Tommy.App.ViewModels;

/// <summary>
/// One row in the context-usage breakdown: a labelled slice of the model's
/// context window, with an estimated token count and its share of the window.
/// Counts are estimates (≈ chars/4), not the model's exact tokenizer.
/// </summary>
public sealed record ContextSlice(string Label, int Tokens, double Percent, string Color)
{
    public string TokensLabel => Tokens >= 1000 ? $"{Tokens / 1000.0:0.0}k" : Tokens.ToString();
    public string PercentLabel => $"{Percent:0.0}%";
    public IBrush Brush => Avalonia.Media.Brush.Parse(Color);
}
