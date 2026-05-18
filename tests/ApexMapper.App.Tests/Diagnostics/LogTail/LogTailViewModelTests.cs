using ApexMapper.App.Diagnostics;
using ApexMapper.App.Diagnostics.LogTail;
using FluentAssertions;

namespace ApexMapper.App.Tests.Diagnostics.LogTail;

public class LogTailViewModelTests
{
    private sealed class FakeTail : ILogTail
    {
        public IReadOnlyList<LogTailEntry> ToReturn { get; set; } = Array.Empty<LogTailEntry>();
        public int MalformedCount { get; set; }
        public int LoadCalls { get; private set; }

        public IReadOnlyList<LogTailEntry> Load(int maxLines)
        {
            LoadCalls++;
            return ToReturn;
        }

        public IReadOnlyList<LogTailEntry> Filter(IReadOnlyList<LogTailEntry> entries, IReadOnlyCollection<string> levels)
            => LogTailFilter.Apply(entries, levels);
    }

    private sealed class FakeClipboard : IClipboard
    {
        public string? LastSet { get; private set; }
        public int SetCalls { get; private set; }
        public void SetText(string text)
        {
            LastSet = text;
            SetCalls++;
        }
    }

    private static LogTailEntry E(string level, string msg, int secOffset = 0) =>
        new(new DateTime(2026, 5, 18, 0, 0, secOffset, DateTimeKind.Utc), level, msg);

    [Fact]
    public void Refresh_loads_all_entries_then_applies_default_filter()
    {
        var tail = new FakeTail
        {
            ToReturn = new[] { E("DEBUG", "d"), E("INFO", "i", 1), E("WARN", "w", 2), E("ERROR", "e", 3) },
            MalformedCount = 2,
        };
        var clip = new FakeClipboard();
        var vm = new LogTailViewModel(tail, clip, () => tail.MalformedCount);

        vm.Refresh();

        // Default: all four toggles on.
        vm.Entries.Should().HaveCount(4);
        vm.MalformedCount.Should().Be(2);
    }

    [Fact]
    public void Toggling_level_off_re_filters_entries()
    {
        var tail = new FakeTail
        {
            ToReturn = new[] { E("DEBUG", "d"), E("INFO", "i", 1), E("WARN", "w", 2), E("ERROR", "e", 3) },
        };
        var clip = new FakeClipboard();
        var vm = new LogTailViewModel(tail, clip, () => tail.MalformedCount);

        vm.Refresh();
        vm.ShowDebug = false;
        vm.ShowInfo = false;

        vm.Entries.Select(e => e.Level).Should().BeEquivalentTo(new[] { "WARN", "ERROR" });
        // Toggling does not re-read the underlying store.
        tail.LoadCalls.Should().Be(1);
    }

    [Fact]
    public void Toggling_all_levels_off_clears_entries()
    {
        var tail = new FakeTail
        {
            ToReturn = new[] { E("INFO", "a") },
        };
        var vm = new LogTailViewModel(tail, new FakeClipboard(), () => 0);
        vm.Refresh();

        vm.ShowDebug = false;
        vm.ShowInfo = false;
        vm.ShowWarn = false;
        vm.ShowError = false;

        vm.Entries.Should().BeEmpty();
    }

    [Fact]
    public void CopyAll_writes_visible_entries_through_IClipboard()
    {
        var tail = new FakeTail
        {
            ToReturn = new[] { E("INFO", "first"), E("WARN", "second", 1) },
        };
        var clip = new FakeClipboard();
        var vm = new LogTailViewModel(tail, clip, () => 0);
        vm.Refresh();

        vm.CopyAll();

        clip.SetCalls.Should().Be(1);
        clip.LastSet.Should().NotBeNull();
        clip.LastSet!.Should().Contain("INFO first");
        clip.LastSet.Should().Contain("WARN second");
    }

    [Fact]
    public void CopyAll_respects_active_filter()
    {
        var tail = new FakeTail
        {
            ToReturn = new[] { E("INFO", "keep"), E("WARN", "drop") },
        };
        var clip = new FakeClipboard();
        var vm = new LogTailViewModel(tail, clip, () => 0);
        vm.Refresh();
        vm.ShowWarn = false;

        vm.CopyAll();

        clip.LastSet.Should().NotBeNull();
        clip.LastSet!.Should().Contain("INFO keep");
        clip.LastSet.Should().NotContain("WARN drop");
    }

    [Fact]
    public void MalformedCount_surfaces_from_underlying_service()
    {
        var tail = new FakeTail { MalformedCount = 7 };
        var vm = new LogTailViewModel(tail, new FakeClipboard(), () => tail.MalformedCount);
        vm.Refresh();
        vm.MalformedCount.Should().Be(7);
    }

    [Fact]
    public void Refresh_command_invokes_Refresh()
    {
        var tail = new FakeTail();
        var vm = new LogTailViewModel(tail, new FakeClipboard(), () => 0);

        vm.RefreshCommand.Execute(null);

        tail.LoadCalls.Should().Be(1);
    }
}
