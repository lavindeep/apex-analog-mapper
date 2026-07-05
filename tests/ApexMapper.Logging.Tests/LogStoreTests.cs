using ApexMapper.Logging;
using FluentAssertions;

namespace ApexMapper.Logging.Tests;

public class LogStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apex-log-" + Guid.NewGuid().ToString("N"));

    public LogStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Writes_lines_to_active_file()
    {
        using var log = new LogStore(_dir, "app.log", maxBytes: 1_000_000, maxFiles: 5);
        log.Write(LogLevel.Info, "hello");
        log.Flush();
        using var stream = new FileStream(Path.Combine(_dir, "app.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        content.Should().Contain("hello").And.Contain("INFO");
    }

    [Fact]
    public void Rotates_when_active_file_exceeds_max_bytes()
    {
        using var log = new LogStore(_dir, "app.log", maxBytes: 128, maxFiles: 3);
        for (var i = 0; i < 50; i++) log.Write(LogLevel.Info, "abcdefghij " + i);
        log.Flush();

        File.Exists(Path.Combine(_dir, "app.log")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "app.log.1")).Should().BeTrue();
    }

    [Fact]
    public void Keeps_at_most_max_files()
    {
        using (var log = new LogStore(_dir, "app.log", maxBytes: 64, maxFiles: 2))
        {
            for (var i = 0; i < 200; i++) log.Write(LogLevel.Info, "line " + i);
            log.Flush();
        }
        var files = Directory.GetFiles(_dir, "app.log*");
        files.Length.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public void Skips_rotation_and_counts_it_when_active_file_cannot_be_moved()
    {
        // Simulate a reader holding the active file: moving it aside throws IOException.
        using var log = new LogStore(_dir, "app.log", maxBytes: 64, maxFiles: 3,
            move: (_, _) => throw new IOException("active file is locked by a reader"));

        Action write = () =>
        {
            for (var i = 0; i < 100; i++) log.Write(LogLevel.Info, "abcdefghij " + i);
            log.Flush();
        };

        write.Should().NotThrow();
        log.RotationSkips.Should().BeGreaterThan(0);
        // Content keeps accumulating in the active file rather than being lost or archived.
        File.Exists(Path.Combine(_dir, "app.log")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "app.log.1")).Should().BeFalse();
    }

    [Fact]
    public void Stranded_staged_file_is_archived_not_deleted_at_the_next_rotation()
    {
        // A crash mid-rotation strands app.log.rotating; its bytes are log content and must
        // survive as a normal archive generation instead of being deleted.
        File.WriteAllText(Path.Combine(_dir, "app.log.rotating"), "stranded generation\n");
        using var log = new LogStore(_dir, "app.log", maxBytes: 64, maxFiles: 3);
        log.Write(LogLevel.Info, "first line");
        log.Write(LogLevel.Info, "second line"); // pushes past maxBytes: exactly one rotation
        log.Flush();

        var archives = Directory.GetFiles(_dir, "app.log.*")
            .Where(f => !f.EndsWith(".rotating", StringComparison.Ordinal))
            .Select(File.ReadAllText);
        archives.Should().Contain(
            c => c.Contains("stranded generation"),
            "the stranded staged bytes must survive as an archive generation");
    }

    [Fact]
    public void Write_does_not_throw_when_the_final_archive_move_fails()
    {
        // Only the staged->newest-archive move fails (e.g. a reader holds app.log.1).
        using var log = new LogStore(_dir, "app.log", maxBytes: 64, maxFiles: 3,
            move: (src, dst) =>
            {
                if (dst.EndsWith(".1", StringComparison.Ordinal)) throw new IOException("archive slot is locked");
                File.Move(src, dst);
            });

        Action write = () =>
        {
            for (var i = 0; i < 100; i++) log.Write(LogLevel.Info, "abcdefghij " + i);
            log.Flush();
        };

        write.Should().NotThrow();
        log.RotationSkips.Should().BeGreaterThan(0);
        File.Exists(Path.Combine(_dir, "app.log")).Should().BeTrue();
    }

    [Fact]
    public void Write_does_not_throw_when_an_archive_shift_fails_mid_chain()
    {
        // A shift inside the archive chain fails (e.g. a reader holds app.log.3).
        File.WriteAllText(Path.Combine(_dir, "app.log.2"), "generation two\n");
        using var log = new LogStore(_dir, "app.log", maxBytes: 64, maxFiles: 4,
            move: (src, dst) =>
            {
                if (dst.EndsWith(".3", StringComparison.Ordinal)) throw new IOException("archive slot is locked");
                File.Move(src, dst);
            });

        Action write = () =>
        {
            for (var i = 0; i < 100; i++) log.Write(LogLevel.Info, "abcdefghij " + i);
            log.Flush();
        };

        write.Should().NotThrow();
        log.RotationSkips.Should().BeGreaterThan(0);
        File.Exists(Path.Combine(_dir, "app.log")).Should().BeTrue();
    }
}
