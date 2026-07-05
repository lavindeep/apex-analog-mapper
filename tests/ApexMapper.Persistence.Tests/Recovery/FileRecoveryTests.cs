using ApexMapper.Persistence.Recovery;
using FluentAssertions;

namespace ApexMapper.Persistence.Tests.Recovery;

public class FileRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apex-filerec-" + Guid.NewGuid().ToString("N"));

    public FileRecoveryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Path_ => System.IO.Path.Combine(_dir, "doc.json");

    private static ParseResult<string> Parse(string text)
        => text.StartsWith("good", StringComparison.Ordinal)
            ? new ParseResult<string>(ParseStatus.Ok, text)
            : new ParseResult<string>(ParseStatus.Corrupt, null);

    [Fact]
    public void Quarantine_failure_recovers_in_memory_without_overwriting_the_primary()
    {
        File.WriteAllText(Path_, "corrupt bytes");
        File.WriteAllText(Path_ + ".bak.1", "good backup");

        var (loaded, value, report) = FileRecovery.Load(Path_, backupCount: 5, Parse,
            move: (_, _) => throw new IOException("quarantine rename blocked"));

        loaded.Should().BeTrue("the value must still be recovered from the backup");
        value.Should().Be("good backup");
        report!.Outcome.Should().Be(RecoveryOutcome.RecoveredFromBackup);
        File.ReadAllText(Path_).Should().Be(
            "corrupt bytes", "the un-quarantined evidence must not be overwritten by the restore");
        File.Exists(Path_ + ".corrupt").Should().BeFalse("the quarantine rename failed");
    }

    [Fact]
    public void Unmigratable_old_schema_is_left_in_place_and_reported()
    {
        File.WriteAllText(Path_, "old schema doc");
        File.WriteAllText(Path_ + ".bak.1", "good backup");

        var (loaded, _, report) = FileRecovery.Load(Path_, backupCount: 5,
            _ => new ParseResult<string>(ParseStatus.UnmigratableSchema, null));

        loaded.Should().BeFalse();
        report!.Outcome.Should().Be(RecoveryOutcome.UnmigratableSchema);
        File.ReadAllText(Path_).Should().Be(
            "old schema doc", "an old but readable document must not be quarantined or overwritten");
        File.Exists(Path_ + ".corrupt").Should().BeFalse(
            "the shared quarantine slot must stay free for genuine corruption");
    }

    [Fact]
    public void Successful_quarantine_still_restores_the_backup_as_the_new_primary()
    {
        File.WriteAllText(Path_, "corrupt bytes");
        File.WriteAllText(Path_ + ".bak.1", "good backup");

        var (loaded, value, report) = FileRecovery.Load(Path_, backupCount: 5, Parse, File.Move);

        loaded.Should().BeTrue();
        value.Should().Be("good backup");
        report!.Outcome.Should().Be(RecoveryOutcome.RecoveredFromBackup);
        File.ReadAllText(Path_).Should().Be("good backup", "the backup must be restored as the primary");
        File.ReadAllText(Path_ + ".corrupt").Should().Be("corrupt bytes");
    }
}
