using Recall.Core.Config;

namespace Recall.Core.Tests;

public class ConfigTests
{
    [Fact]
    public void Default_config_uses_existing_known_folders()
    {
        var cfg = RecallConfig.CreateDefault();
        Assert.NotEmpty(cfg.Roots);
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

    [Fact]
    public void Desktop_resolves_through_known_folder_api()
    {
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
