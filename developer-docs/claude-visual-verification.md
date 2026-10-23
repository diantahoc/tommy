# Visual verification — look at what the UI renders

This is how to **see** what Tommy's Avalonia UI renders, so UI work is verified
against a real image instead of edited blind. Tommy renders its real windows on
Avalonia's **headless platform with the Skia renderer**, captures the frame, and
saves a PNG you then open and inspect.

No display, X server, or GUI is required — it runs in a terminal / CI / agent
environment. See also [`conventions.md`](conventions.md#verifying-ui-work--look-at-the-pixels).

> **Rule:** do not assume a layout is correct from the AXAML alone. Run the
> snapshot, open the PNG, and confirm. Interactive behavior (clicks, live
> streaming) still needs the user running the real app — snapshots cover static
> layout/appearance.

---

## The harness already exists

It lives in **`tests/Tommy.Snapshots`** (referenced by the solution). You normally
don't set anything up — you add or adjust a test and run it.

- `Tommy.Snapshots.csproj` pins **Avalonia.Headless.XUnit 11.3.0** +
  **Avalonia.Skia 11.3.0** (must match the app's Avalonia version) and references
  `src/Tommy.App`.
- `TestAppBuilder.cs` builds the **real `Tommy.App.App`** (so its themes/styles
  load) with `.UseSkia()` + `UseHeadlessDrawing = false` — the two settings that,
  if missing, make captures come back blank. App startup is guarded by the
  classic-desktop lifetime, so it won't try to open a window headlessly.
- `Snapshots.cs` holds the renders. A `[ModuleInitializer]` points `TOMMY_HOME` at
  a throwaway temp dir so snapshots never touch the real `~/.tommy`.

## Running it

```bash
dotnet test tests/Tommy.Snapshots
```

PNGs land in **`artifacts/snapshots/`** at the repo root — one per
`[AvaloniaFact]` (the folder is gitignored). Open them and inspect layout,
spacing, colors, truncation. To iterate on one view:

```bash
dotnet test tests/Tommy.Snapshots --filter "FullyQualifiedName~MainWindow_right_sidebar"
```

> While the app is running, this project is copy-locked like any Tommy.App
> consumer — see [`conventions.md`](conventions.md#building-while-the-app-is-running-copy-lock).

## Adding a snapshot

The offline shell gives you a `ShellViewModel` with one chat open that **never
connects to an engine** (remote profile pointed at a dead port). Build the view,
set whatever state you want visible, and capture:

```csharp
[AvaloniaFact]
public void MainWindow_right_sidebar()
{
    var shell = OfflineShell();
    shell.RightSidebarOpen = true;

    var chat = shell.ActiveChat!;
    foreach (var g in chat.ToolGroups.Take(2)) g.IsExpanded = true;  // show nested rows
    chat.ActivityHint = "Read notes.txt";                            // show the hint

    var window = new MainWindow { DataContext = shell, Width = 1200, Height = 900 };
    Capture(window, "MainWindow-right-sidebar");
}
```

The shared `Capture(window, name)` helper does the load-bearing steps:

```csharp
private static void Capture(Window window, string name)
{
    window.Show();
    Dispatcher.UIThread.RunJobs();          // flush layout + data binding first
    var frame = window.CaptureRenderedFrame();
    var dir = SnapshotDir();                 // walks up to the repo's .git, then artifacts/snapshots
    Directory.CreateDirectory(dir);
    frame!.Save(Path.Combine(dir, $"{name}.png"));
}
```

To render a dialog or a specific tab, construct its window with the right
DataContext (e.g. `new McpConfigWindow { DataContext = new McpConfigViewModel(entry) }`),
and if you need a non-default tab, `Show()` → `RunJobs()` → set the `TabControl`'s
`SelectedIndex` (found via `GetVisualDescendants().OfType<TabControl>()`) →
`RunJobs()` again before capturing. (`Settings_mcp_auth` does exactly this.)

## Gotchas

- **Blank / null frame?** Almost always a missing `.UseSkia()` /
  `UseHeadlessDrawing = false`, or `Avalonia.Skia` not referenced — all already
  set here, so a blank frame usually means the content had zero size.
- **Pump the dispatcher.** After `window.Show()`, call
  `Dispatcher.UIThread.RunJobs()` so layout and binding complete before capture.
- **Size.** A `Window` sizes to content unless you set `Width`/`Height`; set an
  explicit size for a predictable frame. A `UserControl` must be wrapped in a sized
  `Window`.
- **DataContext.** Set the view model before capturing — `OfflineShell()` for the
  main window, or construct the dialog's VM directly.
- This is the same `CaptureRenderedFrame` mechanism Avalonia uses for its own
  visual regression tests, so it's a supported, stable path. For true regression
  testing later, load a baseline PNG and compare pixel-by-pixel with a tolerance.
