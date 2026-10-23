using Tommy.Core.Chat;
using Tommy.Core.Events;
using Tommy.Core.Export;

namespace Tommy.Tests;

public class TranscriptMarkdownTests
{
    private static ChatRecord Record() => new() { Id = "c", Title = "My Chat", Kind = ChatKind.Chat };

    [Fact]
    public void Renders_turns_tool_calls_and_results()
    {
        var events = new List<ChatEvent>
        {
            new UserEvent { Text = "find the latest version" },
            new AssistantEvent
            {
                Reasoning = "I should search the web.",
                Content = "Let me look that up.",
                ToolCalls = { new ToolCallRecord { Id = "c1", Name = "web_research", ArgumentsJson = "{\"query\":\"latest\"}" } }
            },
            new ToolResultEvent { CallId = "c1", Tool = "web_research", Success = true, Result = "Version 3.15" },
            new MetaEvent { Kind = "Completed" }, // routine — should be dropped
        };

        var md = TranscriptMarkdown.Render(Record(), events);

        Assert.Contains("# My Chat", md);
        Assert.Contains("## User", md);
        Assert.Contains("find the latest version", md);
        Assert.Contains("## Assistant", md);
        Assert.Contains("<details><summary>Reasoning</summary>", md);
        Assert.Contains("I should search the web.", md);
        Assert.Contains("Tool call — `web_research`", md);
        Assert.Contains("\"query\": \"latest\"", md);   // pretty-printed args
        Assert.Contains("Result — `web_research` ✓", md);
        Assert.Contains("Version 3.15", md);
        Assert.DoesNotContain("Completed", md);          // routine run-end dropped
    }

    [Fact]
    public void Failed_result_is_marked_and_errors_render()
    {
        var events = new List<ChatEvent>
        {
            new ToolResultEvent { CallId = "x", Tool = "read_file", Success = false, Result = "not found" },
            new MetaEvent { Kind = "error", Detail = "engine offline" },
        };

        var md = TranscriptMarkdown.Render(Record(), events);

        Assert.Contains("Result — `read_file` ✗ error", md);
        Assert.Contains("error: engine offline", md);
    }

    [Fact]
    public void Fence_outgrows_backticks_in_content()
    {
        var events = new List<ChatEvent>
        {
            new ToolResultEvent { CallId = "x", Tool = "t", Success = true, Result = "```\ncode\n```" },
        };

        var md = TranscriptMarkdown.Render(Record(), events);

        // The surrounding fence must be longer than the ``` inside, so the block is intact.
        Assert.Contains("````", md);
    }

    [Fact]
    public void Large_result_is_truncated_with_a_note()
    {
        var big = new string('x', 60_000);
        var events = new List<ChatEvent>
        {
            new ToolResultEvent { CallId = "x", Tool = "t", Success = true, Result = big },
        };

        var md = TranscriptMarkdown.Render(Record(), events);

        Assert.Contains("result truncated to", md);
        Assert.True(md.Length < big.Length + 2_000);
    }

    [Fact]
    public void Blob_backed_result_is_resolved()
    {
        var events = new List<ChatEvent>
        {
            new ToolResultEvent { CallId = "x", Tool = "t", Success = true, Result = "", BlobRef = "abc" },
        };

        var md = TranscriptMarkdown.Render(Record(), events, hash => hash == "abc" ? "blob body" : null);

        Assert.Contains("blob body", md);
    }
}
