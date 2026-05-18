using ApexMapper.Core.Curves;
using ApexMapper.Core.Engine;
using ApexMapper.Core.Pipeline;
using ApexMapper.Persistence.Profiles;
using FluentAssertions;

namespace ApexMapper.Persistence.Tests.Profiles;

public class ProfileStoreBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apex-store-bk-" + Guid.NewGuid().ToString("N"));

    public ProfileStoreBackupTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Profile Make(string id) => new(
        Id: id,
        Name: id,
        Device: new DeviceMatcher(1, 1, null, null),
        Game: new GameMatcher(null, null, null),
        Activation: ActivationPolicy.Default,
        SingleBindings: Array.Empty<SingleKeyBinding>(),
        AxisBindings: Array.Empty<AxisPairBinding>(),
        Notes: null);

    [Fact]
    public void Second_save_creates_backup_1()
    {
        var store = new ProfileStore(new ProfileStoreOptions(_dir, BackupCount: 3));
        store.Save(Make("racing"));
        store.Save(Make("racing"));
        File.Exists(Path.Combine(_dir, "racing.json.bak.1")).Should().BeTrue();
    }

    [Fact]
    public void Backups_rotate_oldest_off()
    {
        var store = new ProfileStore(new ProfileStoreOptions(_dir, BackupCount: 2));
        store.Save(Make("racing"));
        store.Save(Make("racing"));
        store.Save(Make("racing"));
        store.Save(Make("racing"));
        File.Exists(Path.Combine(_dir, "racing.json.bak.1")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "racing.json.bak.2")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "racing.json.bak.3")).Should().BeFalse();
    }

    [Fact]
    public void First_save_creates_no_backup()
    {
        var store = new ProfileStore(new ProfileStoreOptions(_dir, BackupCount: 3));
        store.Save(Make("racing"));
        Directory.GetFiles(_dir, "*.bak.*").Should().BeEmpty();
    }
}
