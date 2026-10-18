using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Tommy.App.Views;

/// <summary>A minimal modal single-line text prompt (used for Rename).</summary>
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _box;
    private bool _confirmed;

    public TextPromptWindow(string title, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _box = new TextBox { Text = initial, Margin = new Thickness(0, 8), Watermark = "Name…" };
        _box.AttachedToVisualTree += (_, _) => { _box.SelectAll(); _box.Focus(); };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _confirmed = true; Close(); }
            else if (e.Key == Key.Escape) { Close(); }
        };

        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 4) };
        ok.Click += (_, _) => { _confirmed = true; Close(); };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4) };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                _box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok }
                }
            }
        };
    }

    /// <summary>Show modally and return the entered text, or null if cancelled.</summary>
    public async Task<string?> PromptAsync(Window owner)
    {
        await ShowDialog(owner);
        return _confirmed ? _box.Text : null;
    }
}
