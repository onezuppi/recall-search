using Recall.Core.Config;

namespace Recall.Core.Tests;

public class ConfigTests
{
    [SkippableFact]
    public void Default_config_uses_existing_known_folders()
    {
        var cfg = RecallConfig.CreateDefault();
        Skip.If(cfg.Roots.Count == 0, "this machine has none of the known folders (a clean CI runner, for one)");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        foreach (var r in cfg.Roots)
        {
            Assert.True(Directory.Exists(r.Path), r.Path);
            Assert.True(
                r.Path.StartsWith(profile, StringComparison.OrdinalIgnoreCase) ||
                (oneDrive != null && r.Path.StartsWith(oneDrive, StringComparison.OrdinalIgnoreCase)),
                r.Path);
        }
    }

    [SkippableFact]
    public void Desktop_resolves_through_known_folder_api()
    {
        Skip.If(KnownFolders.Get(KnownFolderIds.Desktop) == null, "the Desktop known folder does not exist on this machine");
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            KnownFolders.Get(KnownFolderIds.Desktop),
            ignoreCase: true);
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var cfg = RecallConfig.CreateDefault();
            cfg.Save(path);
            var back = RecallConfig.Load(path);
            Assert.Equal(cfg.Roots.ToList(), back.Roots.ToList());
            Assert.Equal(cfg.EnabledGroups.Order().ToList(), back.EnabledGroups.Order().ToList());
            Assert.Equal(cfg.GlobalIgnorePatterns.ToList(), back.GlobalIgnorePatterns.ToList());
            Assert.Equal(cfg.MaxTextBytes, back.MaxTextBytes);
            Assert.Equal(cfg.OcrLanguages.ToList(), back.OcrLanguages.ToList());
        }
        finally { File.Delete(path); }
    }

    private static string TempConfigPath() => Path.Combine(Path.GetTempPath(), "rs-cfg-" + Guid.NewGuid().ToString("N"), "config.json");

    private static void Cleanup(string path) { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }

    [Fact]
    public void A_broken_file_gives_the_defaults_and_is_kept_for_repair()
    {
        var path = TempConfigPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ \"roots\": [ { \"path\": \"C:\\\\x\", \"watc"); // cut off in the middle, as a crash during a save would
            string? problem = null;

            var cfg = RecallConfig.Load(path, p => problem = p);

            Assert.NotNull(cfg.Roots);
            Assert.Contains("config.json", problem);
            Assert.StartsWith("{ \"roots\"", File.ReadAllText(path + ".bad")); // the user's text is not lost
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void A_hand_edited_file_without_lists_does_not_leave_nulls_behind()
    {
        var path = TempConfigPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ \"maxTextBytes\": 1000 }");

            var cfg = RecallConfig.Load(path);

            Assert.Equal(1000, cfg.MaxTextBytes);
            Assert.Empty(cfg.Roots);
            Assert.Empty(cfg.GlobalIgnorePatterns);
            Assert.NotEmpty(cfg.EnabledGroups); // no groups would silently index nothing
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Save_replaces_the_file_without_leaving_a_temporary_one()
    {
        var path = TempConfigPath();
        try
        {
            new RecallConfig([new RootFolder(@"C:\one", false)], new HashSet<string>(["text"]), []).Save(path);
            new RecallConfig([new RootFolder(@"C:\two", true)], new HashSet<string>(["text"]), []).Save(path);

            Assert.Equal(@"C:\two", RecallConfig.Load(path).Roots.Single().Path);
            Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Embedding_fields_round_trip_and_default_off()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var cfg = new RecallConfig([], new HashSet<string>(), []);
            Assert.Null(cfg.EmbeddingModel);
            Assert.Equal(EmbeddingDevice.Cpu, cfg.EmbeddingDevice);
            (cfg with { EmbeddingModel = "multilingual-e5-small", EmbeddingDevice = EmbeddingDevice.Npu }).Save(path);
            Assert.Contains("\"Npu\"", File.ReadAllText(path));
            var back = RecallConfig.Load(path);
            Assert.Equal("multilingual-e5-small", back.EmbeddingModel);
            Assert.Equal(EmbeddingDevice.Npu, back.EmbeddingDevice);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_file_loads_default()
    {
        var cfg = RecallConfig.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        Assert.NotEmpty(cfg.Roots);
    }

    [Fact]
    public void Pause_settings_default_to_on_and_survive_a_save_load_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), "rs-cfg-" + Guid.NewGuid().ToString("N"), "config.json");
        try
        {
            var cfg = new RecallConfig([], new HashSet<string>(), []) { PauseProcesses = ["game.exe"], PauseWhenBusy = false };
            Assert.True(new RecallConfig([], new HashSet<string>(), []).PauseWhenBusy);
            cfg.Save(path);
            var loaded = RecallConfig.Load(path);
            Assert.False(loaded.PauseWhenBusy);
            Assert.Equal(["game.exe"], loaded.PauseProcesses);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }
}
