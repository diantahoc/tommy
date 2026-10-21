using System.Text.Json;
using System.Text.Json.Nodes;
using Tommy.Core.Tools;
using Tommy.Tools.Internal;

namespace Tommy.Tools;

/// <summary>
/// Opens a file the agent produced (a report, a chart, an image, etc.) in the app's
/// file pane so the user can see it. Always available and hidden from the sidebar.
/// </summary>
[Tool]
public sealed class PresentFileTool : ITool
{
    public string Name => "present_file";

    public string Description =>
        "Open a file in the app's file pane for the user to view (e.g. a generated " +
        "report, markdown, or image). Give the path to a file inside the workspace. " +
        "Use after creating or updating a file the user should see.";

    public PermissionTier Tier => PermissionTier.Read;

    public JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.Str("Path to the file to open, relative to the workspace."), true),
        ("title", ToolSchema.Str("Optional title shown above the file (defaults to the file name)."), false));

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!PathSandbox.TryResolve(ctx, Args.Str(args, "path") ?? "", out var full, out var error))
            return Task.FromResult(ToolResult.Fail(error!));
        if (!File.Exists(full))
            return Task.FromResult(ToolResult.Fail("file not found."));
        if (ctx.PresentFile is null)
            return Task.FromResult(ToolResult.Fail("the file pane isn't available in this context."));

        var title = Args.Str(args, "title");
        ctx.PresentFile(new PresentedFile(full, string.IsNullOrWhiteSpace(title) ? null : title!.Trim()));

        var rel = PathSandbox.Relative(ctx, full);
        return Task.FromResult(ToolResult.Ok($"Opened {rel} in the file pane.", ToolResultKind.Text, "present_file"));
    }
}

/// <summary>
/// Lets the model pause and ask the user a question with a set of offered answers
/// (and, by default, a free-text box). The chosen or typed answer is returned as
/// the tool result. This tool is always available and hidden from the tool sidebar.
/// </summary>
[Tool]
public sealed class AskQuestionTool : ITool
{
    public string Name => "ask_question";

    public string Description =>
        "Ask the user a question and wait for their answer. Provide 'options' as the " +
        "answers to choose from; the user can also type a different answer unless " +
        "allow_custom is false. Use this when you need a decision or missing detail " +
        "instead of guessing. Returns the user's answer.";

    // Read tier: it changes nothing and must not itself prompt for approval — the
    // question UI is the interaction.
    public PermissionTier Tier => PermissionTier.Read;

    public JsonObject ParameterSchema => ToolSchema.Object(
        ("question", ToolSchema.Str("The question to put to the user."), true),
        ("options", ToolSchema.StrArray("Answers to offer as choices (optional)."), false),
        ("allow_custom", ToolSchema.Bool("Whether the user may type their own answer (default true)."), false));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var question = Args.Str(args, "question");
        if (string.IsNullOrWhiteSpace(question))
            return ToolResult.Fail("question is required.");

        if (ctx.AskUser is null)
            return ToolResult.Fail("Interactive questions aren't available in this context.");

        var options = Args.StrList(args, "options");
        var allowCustom = Args.Bool(args, "allow_custom", fallback: true);
        // Guard against a dead end: if there are no options, the user must be able to type.
        if (options.Count == 0) allowCustom = true;

        try
        {
            var answer = await ctx.AskUser(new UserQuestion(question, options, allowCustom), ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(answer)
                ? ToolResult.Fail("The user dismissed the question without answering.")
                : ToolResult.Ok(answer, ToolResultKind.Text, "answer");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }
}
