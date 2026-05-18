using ApexMapper.App.SingleInstance;
using FluentAssertions;
using Xunit;

namespace ApexMapper.App.Tests.SingleInstance;

/// <summary>
/// Verifies <see cref="SingleInstanceGuard"/> mutual-exclusion semantics.
///
/// Each test uses a random per-test mutex name to avoid cross-test or cross-CI
/// interference with the global mutex namespace.
/// </summary>
public sealed class SingleInstanceGuardTests
{
    private static string NewMutexName() =>
        $"Local\\ApexMapper.App.Test.{Guid.NewGuid():N}";

    [Fact]
    public void FirstGuard_IsPrimary()
    {
        var name = NewMutexName();
        using var guard = new SingleInstanceGuard(name);
        guard.IsPrimary.Should().BeTrue("first instance must be primary");
    }

    [Fact]
    public void SecondGuard_SameName_IsNotPrimary()
    {
        var name = NewMutexName();
        using var first  = new SingleInstanceGuard(name);
        using var second = new SingleInstanceGuard(name);

        first.IsPrimary.Should().BeTrue("first instance must be primary");
        second.IsPrimary.Should().BeFalse("second instance with same name must not be primary");
    }

    [Fact]
    public void TwoGuards_DifferentNames_AreBothPrimary()
    {
        using var a = new SingleInstanceGuard(NewMutexName());
        using var b = new SingleInstanceGuard(NewMutexName());

        a.IsPrimary.Should().BeTrue();
        b.IsPrimary.Should().BeTrue();
    }

    [Fact]
    public void SecondGuard_IsPrimaryStatus_DoesNotChange_AfterFirstDisposed()
    {
        // The spec says: "Dispose first; the second's IsPrimary remains as-was".
        // We capture the status before disposal and assert it is unchanged after.
        var name = NewMutexName();

        var first  = new SingleInstanceGuard(name);
        var second = new SingleInstanceGuard(name);

        var statusBeforeDispose = second.IsPrimary; // false
        first.Dispose();

        // IsPrimary is an immutable snapshot set at construction — it does not
        // re-evaluate after the competing guard is released.
        second.IsPrimary.Should().Be(statusBeforeDispose,
            "IsPrimary is determined at construction and must not change after the primary disposes");

        second.Dispose();
    }
}
