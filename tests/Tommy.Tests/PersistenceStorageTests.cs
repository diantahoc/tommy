using Tommy.Core.Chat;
using Tommy.Core.Configuration;
using Tommy.Core.Events;
using Tommy.Core.Projects;
using Tommy.Core.Tools;
using Tommy.Persistence.Storage;

namespace Tommy.Tests;

public class BlobStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tommy-blob-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Put_is_content_addressed_and_dedupes()
    {
        var store = new BlobStore(_dir);
        var h1 = store.PutText("hello");
        var h2 = store.PutText("hello");
        var h3 = store.PutText("world");

        Assert.Equal(h1, h2);
        Assert.NotEqual(h1, h3);
        Assert.Equal("hello", store.GetText(h1));
        Assert.Single(Directory.GetFiles(_dir), f => Path.GetFileName(f) == h1);
    }
}

public class EventStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tommy-events-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Round_trips_polymorphic_events()
    {
        var store = new EventStore(Path.Combine(_dir, "events.jsonl"));
        store.Append(new UserEvent { Text = "hi" });
        store.Append(new AssistantEvent
        {
            Content = "answer",
            Reasoning = "thinking",
            ToolCalls = { new ToolCallRecord { Id = "c1", Name = "line_count", ArgumentsJson = "{}" } }
        });
        store.Append(new ToolResultEvent { CallId = "c1", Tool = "line_count", Success = true, Result = "3" });
        store.Append(new MetaEvent { Kind = "run_end" });

        var events = store.Read().ToList();
        Assert.Equal(4, events.Count);
        Assert.IsType<UserEvent>(events[0]);
        var asst = Assert.IsType<AssistantEvent>(events[1]);
        Assert.Equal("thinking", asst.Reasoning);
        Assert.Single(asst.ToolCalls);
        Assert.Equal("3", Assert.IsType<ToolResultEvent>(events[2]).Result);
        Assert.Equal("run_end", Assert.IsType<MetaEvent>(events[3]).Kind);
    }
}

public class ChatRepositoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tommy-chats-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Create_persist_list_and_delete()
    {
        var repo = new ChatRepository(_root);
        var handle = repo.Create(new ChatRecord { Id = "chat1", Kind = ChatKind.Code, Title = "First" });
        handle.Events.Append(new UserEvent { Text = "hello" });

        var reloaded = repo.Open("chat1");
        Assert.NotNull(reloaded);
        Assert.Equal("First", reloaded!.Record.Title);
        Assert.Equal(ChatKind.Code, reloaded.Record.Kind);
        Assert.Single(reloaded.Events.Read());

        Assert.Single(repo.List());
        repo.Delete("chat1");
        Assert.Empty(repo.List());
    }

    [Fact]
    public void Energy_kwh_round_trips()
    {
        var repo = new ChatRepository(_root);
        repo.Create(new ChatRecord { Id = "e1", Title = "Energy", EnergyKwh = 0.0123 });

        var reloaded = repo.Open("e1");
        Assert.NotNull(reloaded);
        Assert.Equal(0.0123, reloaded!.Record.EnergyKwh, precision: 6);
    }

    [Fact]
    public void List_is_newest_first()
    {
        var repo = new ChatRepository(_root);
        repo.Create(new ChatRecord { Id = "a", Title = "A", Updated = DateTimeOffset.UtcNow.AddMinutes(-5) });
        repo.Create(new ChatRecord { Id = "b", Title = "B", Updated = DateTimeOffset.UtcNow });
        Assert.Equal("b", repo.List()[0].Id);
    }
}

public class WorkspaceRepositoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tommy-ws-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Hash_is_stable_and_case_insensitive()
    {
        var a = WorkspaceRepository.HashFolder(@"C:\Projects\App");
        var b = WorkspaceRepository.HashFolder(@"C:\Projects\App\");
        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
    }

    [Fact]
    public void GetOrCreate_is_idempotent_and_tracks_chats()
    {
        var repo = new WorkspaceRepository(_root);
        var folder = Directory.CreateTempSubdirectory("ws-folder-").FullName;
        var w1 = repo.GetOrCreate(folder);
        var w2 = repo.GetOrCreate(folder);
        Assert.Equal(w1.Id, w2.Id);

        repo.AddChat(w1.Id, "chatX");
        repo.AddChat(w1.Id, "chatX"); // no dup
        Assert.Single(repo.Load(w1.Id)!.MemberChatIds);

        Directory.Delete(folder, recursive: true);
    }
}

public class ParameterResolverTests
{
    [Fact]
    public void Chat_overrides_project_overrides_global()
    {
        var global = new GlobalDefaults { MaxTokens = 1000, Temperature = 0.5, ThinkingEffort = Core.Chat.ThinkingEffort.Low };
        var project = new ChatParameters { MaxTokens = 2000, Autonomy = AutonomyLevel.Auto };
        var chat = new ChatParameters { MaxTokens = 3000 };

        var resolved = ParameterResolver.Resolve(chat, project, global);

        Assert.Equal(3000, resolved.MaxTokens);                 // chat wins
        Assert.Equal(AutonomyLevel.Auto, resolved.Autonomy);    // project supplies
        Assert.Equal(0.5, resolved.Temperature);                // global supplies
        Assert.Equal(ChatDefaults.SystemPrompt, resolved.SystemPrompt);
    }

    [Fact]
    public void Falls_back_to_global_when_no_overrides()
    {
        var resolved = ParameterResolver.Resolve(null, null, new GlobalDefaults());
        Assert.Equal(AutonomyLevel.ReviewEach, resolved.Autonomy);
        Assert.Equal(4096, resolved.MaxTokens);
    }
}

public class ProjectRepositoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tommy-proj-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Round_trips_with_default_params()
    {
        var repo = new ProjectRepository(_root);
        repo.Save(new ProjectRecord { Id = "p1", Name = "Proj", Defaults = new ChatParameters { Autonomy = AutonomyLevel.ReviewDestructive } });

        var loaded = repo.Load("p1");
        Assert.NotNull(loaded);
        Assert.Equal("Proj", loaded!.Name);
        Assert.Equal(AutonomyLevel.ReviewDestructive, loaded.Defaults.Autonomy);
        Assert.True(Directory.Exists(repo.MemoryDir("p1")));
    }
}
