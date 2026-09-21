using ApexMapper.Core.Storage;
using Xunit;

namespace ApexMapper.Core.Tests.Storage;

public class JsonFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apex-jsonfile-" + Guid.NewGuid().ToString("N"));
    private string PathOf(string name) => Path.Combine(_dir, name);

    private static (string? Value, string? Error) Parse(string text) =>
        text.StartsWith('{') ? (text, null) : (null, "not an object");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Save_creates_the_file_without_a_bom_and_the_second_save_keeps_a_backup()
    {
        var path = PathOf("a.json");
        JsonFile.Save(path, "{\"v\":1}");
        Assert.Equal("{\"v\":1}", File.ReadAllText(path));
        Assert.Equal((byte)'{', File.ReadAllBytes(path)[0]);
        Assert.False(File.Exists(JsonFile.BackupPath(path)));

        JsonFile.Save(path, "{\"v\":2}");
        Assert.Equal("{\"v\":2}", File.ReadAllText(path));
        Assert.Equal("{\"v\":1}", File.ReadAllText(JsonFile.BackupPath(path)));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_stale_temp_file_from_a_crash_is_overwritten_by_the_next_save()
    {
        var path = PathOf("b.json");
        JsonFile.Save(path, "{\"v\":1}");
        File.WriteAllText(path + ".tmp", "{\"partial");
        JsonFile.Save(path, "{\"v\":2}");
        Assert.Equal("{\"v\":2}", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_save_that_cannot_write_its_temp_file_leaves_the_primary_untouched()
    {
        var path = PathOf("b2.json");
        JsonFile.Save(path, "{\"v\":1}");
        var before = File.ReadAllBytes(path);
        Directory.CreateDirectory(path + ".tmp");
        Assert.ThrowsAny<Exception>(() => JsonFile.Save(path, "{\"v\":2}"));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(JsonFile.BackupPath(path)));
    }

    [Fact]
    public void Corrupt_primary_recovers_from_the_backup_and_is_kept_aside()
    {
        var path = PathOf("c.json");
        JsonFile.Save(path, "{\"v\":1}");
        JsonFile.Save(path, "{\"v\":2}");
        File.WriteAllText(path, "garbage");

        var result = JsonFile.Load(path, Parse);
        Assert.Equal(LoadStatus.Recovered, result.Status);
        Assert.Equal("{\"v\":1}", result.Value);
        Assert.Equal("{\"v\":1}", File.ReadAllText(path));
        Assert.Equal("garbage", File.ReadAllText(JsonFile.CorruptPath(path)));
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Missing_primary_recovers_from_the_backup()
    {
        var path = PathOf("c2.json");
        JsonFile.Save(path, "{\"v\":1}");
        JsonFile.Save(path, "{\"v\":2}");
        File.Delete(path);

        var result = JsonFile.Load(path, Parse);
        Assert.Equal(LoadStatus.Recovered, result.Status);
        Assert.Equal("{\"v\":1}", result.Value);
        Assert.Equal("{\"v\":1}", File.ReadAllText(path));
        Assert.Null(result.Error);
    }

    [Fact]
    public void Missing_file_and_unrecoverable_file_are_reported()
    {
        var missing = JsonFile.Load(PathOf("none.json"), Parse);
        Assert.Equal(LoadStatus.NotFound, missing.Status);

        var path = PathOf("d.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "garbage");
        var corrupt = JsonFile.Load(path, Parse);
        Assert.Equal(LoadStatus.Corrupt, corrupt.Status);
        Assert.Null(corrupt.Value);
        Assert.True(File.Exists(JsonFile.CorruptPath(path)));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Both_primary_and_backup_corrupt_say_so()
    {
        var path = PathOf("e.json");
        JsonFile.Save(path, "{\"v\":1}");
        JsonFile.Save(path, "{\"v\":2}");
        File.WriteAllText(path, "garbage");
        File.WriteAllText(JsonFile.BackupPath(path), "more garbage");
        var result = JsonFile.Load(path, Parse);
        Assert.Equal(LoadStatus.Corrupt, result.Status);
        Assert.Contains("backup is unreadable", result.Error);
        Assert.True(File.Exists(JsonFile.CorruptPath(path)));
    }

    [Fact]
    public void A_parser_that_throws_counts_as_corrupt_input()
    {
        var path = PathOf("f.json");
        JsonFile.Save(path, "{\"v\":1}");
        JsonFile.Save(path, "{\"v\":2}");
        var result = JsonFile.Load<string>(path, text => text.Contains("2") ? throw new NullReferenceException("bad") : (text, null));
        Assert.Equal(LoadStatus.Recovered, result.Status);
        Assert.Equal("{\"v\":1}", result.Value);
        Assert.Contains("bad", result.Error);
    }

    [Fact]
    public void A_locked_primary_is_unavailable_and_left_alone()
    {
        var path = PathOf("g.json");
        JsonFile.Save(path, "{\"v\":1}");
        JsonFile.Save(path, "{\"v\":2}");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = JsonFile.Load(path, Parse);
            Assert.Equal(LoadStatus.Unavailable, result.Status);
            Assert.Null(result.Value);
            Assert.NotNull(result.Error);
        }
        Assert.Equal("{\"v\":2}", File.ReadAllText(path));
        Assert.False(File.Exists(JsonFile.CorruptPath(path)));
    }
}
