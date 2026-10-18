using Tommy.Core.Chat;
using Tommy.Core.Events;
using Tommy.Persistence.Storage;

namespace Tommy.Tests;

public class ChatSearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tommy-search-").FullName;
    private readonly ChatRepository _repo;

    public ChatSearchTests()
    {
        _repo = new ChatRepository(_root);
        var a = _repo.Create(new ChatRecord { Id = "a", Kind = ChatKind.Code, Title = "Refactor parser" });
        a.Events.Append(new UserEvent { Text = "please fix the tokenizer bug" });
        a.Events.Append(new AssistantEvent { Content = "Found the issue in the lexer." });

        var b = _repo.Create(new ChatRecord { Id = "b", Kind = ChatKind.Chat, Title = "Vacation ideas" });
        b.Events.Append(new UserEvent { Text = "where should I travel this summer" });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Finds_text_in_events()
    {
        var hits = new ChatSearch(_repo).Search("tokenizer");
        var hit = Assert.Single(hits);
        Assert.Equal("a", hit.ChatId);
        Assert.Contains("tokenizer", hit.Snippet);
    }

    [Fact]
    public void Matches_title()
    {
        var hits = new ChatSearch(_repo).Search("Vacation");
        Assert.Equal("b", Assert.Single(hits).ChatId);
    }

    [Fact]
    public void Is_case_insensitive()
    {
        Assert.Single(new ChatSearch(_repo).Search("LEXER"));
    }

    [Fact]
    public void Filters_by_kind()
    {
        Assert.Empty(new ChatSearch(_repo).Search("tokenizer", ChatKind.Chat));
        Assert.Single(new ChatSearch(_repo).Search("tokenizer", ChatKind.Code));
    }

    [Fact]
    public void Empty_query_returns_nothing()
    {
        Assert.Empty(new ChatSearch(_repo).Search("   "));
    }
}
