using ApexMapper.Persistence.Atomic;
using FluentAssertions;

namespace ApexMapper.Persistence.Tests.Atomic;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apex-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Writes_bytes_atomically_to_target_path()
    {
        var path = Path.Combine(_dir, "file.bin");
        AtomicFile.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        File.ReadAllBytes(path).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void Replaces_existing_file_atomically()
    {
        var path = Path.Combine(_dir, "file.bin");
        File.WriteAllBytes(path, new byte[] { 9, 9 });
        AtomicFile.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        File.ReadAllBytes(path).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Leaves_no_temp_files_on_success()
    {
        var path = Path.Combine(_dir, "file.bin");
        AtomicFile.WriteAllBytes(path, new byte[] { 1 });
        Directory.GetFiles(_dir).Should().ContainSingle().Which.Should().EndWith("file.bin");
    }

    [Fact]
    public void Writes_text_with_utf8_no_bom()
    {
        var path = Path.Combine(_dir, "file.txt");
        AtomicFile.WriteAllText(path, "hello");
        var bytes = File.ReadAllBytes(path);
        bytes.Should().Equal((byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o');
    }

    [Fact]
    public void Commit_falls_back_to_delete_and_move_when_replace_unsupported()
    {
        var path = Path.Combine(_dir, "file.bin");
        File.WriteAllBytes(path, new byte[] { 9, 9 });
        var tmp = AtomicFile.WriteTemp(path, new byte[] { 1, 2, 3 });

        // Simulate a filesystem where File.Replace is not supported.
        AtomicFile.Commit(tmp, path, (_, _, _) => throw new PlatformNotSupportedException());

        File.ReadAllBytes(path).Should().Equal(1, 2, 3);
        File.Exists(tmp).Should().BeFalse();
    }

    [Fact]
    public void SweepStaleTemps_removes_stale_temps_and_spares_live_files()
    {
        var live = Path.Combine(_dir, "keep.json");
        File.WriteAllText(live, "{}");
        var freshTemp = Path.Combine(_dir, "keep.json.tmp." + Guid.NewGuid().ToString("N"));
        File.WriteAllText(freshTemp, "in-flight");
        var staleTemp = Path.Combine(_dir, "keep.json.tmp." + Guid.NewGuid().ToString("N"));
        File.WriteAllText(staleTemp, "orphan");
        File.SetLastWriteTimeUtc(staleTemp, DateTime.UtcNow - TimeSpan.FromMinutes(10));

        AtomicFile.SweepStaleTemps(_dir);

        File.Exists(staleTemp).Should().BeFalse();
        File.Exists(freshTemp).Should().BeTrue();
        File.Exists(live).Should().BeTrue();
    }
}
