using System;
using System.IO;
using ApexMapper.App.Services;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.Profiles;

public sealed class JsonProfileManualPinStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "ApexMapper_PinStore_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonProfileManualPinStore CreateStore() => new(_tempDir);

    [Fact]
    public void Get_returns_null_when_no_file_exists()
    {
        var store = CreateStore();
        store.Get().Should().BeNull();
    }

    [Fact]
    public void Set_then_reconstruct_Get_returns_stored_id()
    {
        var store = CreateStore();
        store.Set("p1");

        var store2 = CreateStore();
        store2.Get().Should().Be("p1");
    }

    [Fact]
    public void Set_null_clears_the_file()
    {
        var store = CreateStore();
        store.Set("p1");
        store.Set(null);

        var store2 = CreateStore();
        store2.Get().Should().BeNull();
    }
}
