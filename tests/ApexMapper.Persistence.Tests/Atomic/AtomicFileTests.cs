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
}
