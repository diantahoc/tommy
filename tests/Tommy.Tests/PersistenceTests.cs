using Tommy.Core.Chat;
using Tommy.Core.Configuration;
using Tommy.Core.Engine;
using Tommy.Persistence;
using Tommy.Persistence.Setup;

namespace Tommy.Tests;

public class ConfigStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"tommy-cfg-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_returns_default_when_missing()
    {
        var store = new ConfigStore(TempFile());
        var config = store.Load();
        Assert.True(config.NeedsFirstRun);
        Assert.Empty(config.EngineProfiles);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var path = TempFile();
        try
        {
            var store = new ConfigStore(path);
            var config = new TommyConfig
            {
                ActiveProfileName = "dev",
                ElectricityRatePerKwh = 0.31,
                EngineProfiles =
                {
                    new EngineProfile
                    {
                        Name = "dev",
                        Mode = EngineMode.Remote,
                        BaseUrl = "http://127.0.0.1:8080/v1"
                    }
                }
            };
            config.Defaults.ThinkingEffort = ThinkingEffort.High;

            store.Save(config);
            var loaded = new ConfigStore(path).Load();

            Assert.False(loaded.NeedsFirstRun);
            Assert.Equal("dev", loaded.ActiveProfile!.Name);
            Assert.Equal(EngineMode.Remote, loaded.ActiveProfile.Mode);
            Assert.Equal(0.31, loaded.ElectricityRatePerKwh, 3);
            Assert.Equal(ThinkingEffort.High, loaded.Defaults.ThinkingEffort);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Enums_persist_as_strings()
    {
        var path = TempFile();
        try
        {
            new ConfigStore(path).Save(new TommyConfig
            {
                EngineProfiles = { new EngineProfile { Name = "m", Mode = EngineMode.Managed, BaseUrl = "http://x/v1" } }
            });
            var text = File.ReadAllText(path);
            Assert.Contains("Managed", text);
            Assert.DoesNotContain("\"Mode\": 1", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Mcp_servers_round_trip()
    {
        var path = TempFile();
        try
        {
            var store = new ConfigStore(path);
            var config = new TommyConfig
            {
                EngineProfiles = { new EngineProfile { Name = "m", Mode = EngineMode.Remote, BaseUrl = "http://x/v1" } },
                McpServers =
                {
                    new McpServerEntry
                    {
                        Id = "productiondb",
                        DisplayName = "productiondb",
                        Transport = Tommy.Core.Mcp.McpTransportKind.Stdio,
                        Command = "npx",
                        Args = { "-y", "@benborla29/mcp-server-mysql" },
                        ReadOnly = true
                    }
                }
            };

            store.Save(config);
            var text = File.ReadAllText(path);
            Assert.Contains("Stdio", text); // enum as string

            var loaded = new ConfigStore(path).Load();
            var entry = Assert.Single(loaded.McpServers);
            Assert.Equal("productiondb", entry.Id);
            Assert.Equal("npx", entry.Command);
            Assert.Equal(new[] { "-y", "@benborla29/mcp-server-mysql" }, entry.Args);
            Assert.True(entry.ReadOnly);

            // Maps to a plain (always-ask) route.
            var mapped = entry.ToConfig();
            Assert.Equal(Tommy.Core.Mcp.McpRoute.Plain, mapped.Route);
            Assert.Equal("npx", mapped.Command);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Active_profile_falls_back_to_first()
    {
        var config = new TommyConfig
        {
            ActiveProfileName = "missing",
            EngineProfiles = { new EngineProfile { Name = "only", Mode = EngineMode.Remote, BaseUrl = "http://x/v1" } }
        };
        Assert.Equal("only", config.ActiveProfile!.Name);
    }
}

public class SecretStoreTests
{
    [Fact]
    public void Round_trips_and_deletes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tommy-sec-{Guid.NewGuid():N}.json");
        try
        {
            var store = new PlaintextSecretStore(path);
            store.Set("db:prod", "p@ss");
            Assert.Equal("p@ss", new PlaintextSecretStore(path).Get("db:prod"));

            store.Delete("db:prod");
            Assert.Null(new PlaintextSecretStore(path).Get("db:prod"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public class FirstRunValidatorTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080/v1", true)]
    [InlineData("https://host:9000/v1", true)]
    [InlineData("127.0.0.1:8080", false)]
    [InlineData("ftp://host/v1", false)]
    [InlineData("", false)]
    public void Remote_url_validation(string url, bool ok)
    {
        Assert.Equal(ok, FirstRunValidator.ValidateRemote(url).Ok);
    }

    [Fact]
    public void Managed_requires_existing_dir_and_script()
    {
        var missing = FirstRunValidator.ValidateManaged("C:/definitely/not/here", "run.bat", "http://x/v1");
        Assert.False(missing.Ok);

        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var script = Path.Combine(dir, "run.bat");
            File.WriteAllText(script, "echo hi");
            Assert.True(FirstRunValidator.ValidateManaged(dir, "run.bat", "http://x/v1").Ok);
            Assert.False(FirstRunValidator.ValidateManaged(dir, "nope.bat", "http://x/v1").Ok);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
