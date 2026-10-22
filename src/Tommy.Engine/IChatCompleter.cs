using Tommy.Engine.Dto;

namespace Tommy.Engine;

/// <summary>
/// The one engine capability the agent loop needs: run a (serialized) completion
/// and get back content + reasoning + tool calls. Implemented by
/// <see cref="EngineService"/>; a fake implements it in tests.
/// </summary>
public interface IChatCompleter
{
    Task<ChatTurnResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Streaming variant: invokes <paramref name="onToken"/> for each reasoning/content
    /// delta as it arrives and returns the fully assembled result. The default is no
    /// streaming — it just calls the non-streaming overload — so a fake completer and any
    /// non-UI caller keep working unchanged.
    /// </summary>
    Task<ChatTurnResult> CompleteAsync(
        ChatCompletionRequest request, Action<ChatStreamEvent>? onToken, CancellationToken ct = default) =>
        CompleteAsync(request, ct);
}
