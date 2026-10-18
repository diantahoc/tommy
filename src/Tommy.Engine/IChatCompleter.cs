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
}
