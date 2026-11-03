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
    public void Missing_file_loads_default()
    {
        var cfg = RecallConfig.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        Assert.NotEmpty(cfg.Roots);
    }
}
