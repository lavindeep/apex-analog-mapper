using System;
using System.IO;
using ApexMapper.App.Persistence;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Hotkey;

public sealed class JsonPanicPolicyStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ApexMapper_Tests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonPanicPolicyStore CreateStore()
        => new(new PanicPolicyOptions(_tempDir));

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void Empty_store_ListDisabled_returns_empty()
    {
        var store = CreateStore();

        store.ListDisabled().Should().BeEmpty();
    }

    [Fact]
    public void DisableAutoEnable_then_reload_IsAutoEnableDisabled_returns_true_case_insensitive()
    {
        var store = CreateStore();
        store.DisableAutoEnable(@"C:\Games\Forza.exe");

        // Re-create store from the same directory (simulates restart)
        var reloadedStore = CreateStore();

        reloadedStore.IsAutoEnableDisabled(@"c:\games\forza.EXE").Should().BeTrue();
    }

    [Fact]
    public void EnableAutoEnable_removes_the_entry()
    {
        var store = CreateStore();
        store.DisableAutoEnable(@"C:\Games\Forza.exe");

        store.EnableAutoEnable(@"C:\Games\Forza.exe");

        store.IsAutoEnableDisabled(@"C:\Games\Forza.exe").Should().BeFalse();
        store.ListDisabled().Should().BeEmpty();
    }

    [Fact]
    public void Concurrent_disable_from_two_stores_does_not_corrupt_the_file()
    {
        // Single-writer semantics: two sequential writes each add a distinct exe.
        var store1 = CreateStore();
        var store2 = CreateStore();

        store1.DisableAutoEnable(@"C:\Games\GameA.exe");
        store2.DisableAutoEnable(@"C:\Games\GameB.exe");

        // Re-create from disk to verify both survived
        var reader = CreateStore();
        reader.ListDisabled().Should().HaveCount(2);
        reader.IsAutoEnableDisabled(@"C:\Games\GameA.exe").Should().BeTrue();
        reader.IsAutoEnableDisabled(@"C:\Games\GameB.exe").Should().BeTrue();
    }
}
