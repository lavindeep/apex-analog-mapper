using System.ComponentModel;
using ApexMapper.Core.Keys;
using ApexMapper.Windows.Input;
using ApexMapper.Windows.Native;
using Xunit;

namespace ApexMapper.Windows.Tests.Input;

/// <summary>The callback body driven with synthetic hook structs; no hook installed.</summary>
public class KeyboardHookTests
{
    private const int W = 0x11;
    private const int F12 = 0x58;
    private const int LeftCtrl = 0x1D;
    private const int LeftAlt = 0x38;

    private static User32.KBDLLHOOKSTRUCT Event(uint scanCode, bool extended = false, bool injected = false, uint vkCode = 0) => new()
    {
        vkCode = vkCode,
        scanCode = scanCode,
        flags = (extended ? User32.LLKHF_EXTENDED : 0) | (injected ? User32.LLKHF_INJECTED : 0),
    };

    private static (KeyboardHook Hook, KeyStateStore Store, HookPolicy Policy, ForegroundFlag Flag) Build(bool gameForeground = true, bool swallowInjected = false)
    {
        var store = new KeyStateStore();
        var policy = new HookPolicy { SwallowInjected = swallowInjected };
        var flag = new ForegroundFlag { IsGameForeground = gameForeground };
        var hook = new KeyboardHook(store, policy, flag, [new ScanCode(W), new ScanCode(0xE048)]);
        return (hook, store, policy, flag);
    }

    [Theory]
    [InlineData(0x11u, false, 0u, 0x11)]
    [InlineData(0x48u, true, 0u, 256 + 0x48)]
    [InlineData(0x45u, true, 0u, 0x45)]
    [InlineData(0x45u, false, 0u, 512 + 0x45)]
    [InlineData(0x58u, false, 0xE7u, -1)]
    [InlineData(0x0158u, false, 0u, -1)]
    [InlineData(0u, false, 0u, -1)]
    [InlineData(0xFFu, false, 0u, -1)]
    [InlineData(0x2Au, true, 0u, -1)]
    [InlineData(0x36u, true, 0u, -1)]
    [InlineData(0x2Au, false, 0u, 0x2A)]
    public void Decode_maps_make_codes_to_slots_and_ignores_what_the_mapper_cannot_use(uint scanCode, bool extended, uint vkCode, int slot)
    {
        Assert.Equal(slot, KeyboardHook.SlotOf(vkCode, scanCode, extended ? User32.LLKHF_EXTENDED : 0));
    }

    [Fact]
    public void A_swallowed_press_is_recorded_down_and_not_gated()
    {
        var (hook, store, _, _) = Build();

        Assert.True(hook.Handle(User32.WM_KEYDOWN, Event(W)));
        Assert.True(store.Read(W).Digital);
        Assert.False(store.IsGated(W));
        Assert.True(hook.Handle(User32.WM_KEYUP, Event(W)));
        Assert.False(store.Read(W).Digital);
    }

    [Fact]
    public void A_mapped_press_that_passes_through_is_recorded_down_and_gated()
    {
        var (hook, store, _, _) = Build(gameForeground: false);

        Assert.False(hook.Handle(User32.WM_KEYDOWN, Event(W)));
        Assert.True(store.Read(W).Digital);
        Assert.True(store.IsGated(W));
        Assert.False(hook.Handle(User32.WM_KEYDOWN, Event(W)));
        Assert.True(store.IsGated(W));
        Assert.False(hook.Handle(User32.WM_KEYUP, Event(W)));
        Assert.False(store.Read(W).Digital);
        Assert.False(store.IsGated(W));
    }

    /// <summary>
    /// Held when the hook went in, with the game in front, left as <c>MarkKeysAlreadyDown</c>
    /// leaves it: passed, gated, not recorded down. Its repeats are swallowed and must
    /// not ungate it, because the game already has the press.
    /// </summary>
    [Fact]
    public void A_key_held_at_install_stays_gated_through_its_swallowed_repeats()
    {
        var (hook, store, policy, _) = Build();
        policy.MarkDown(W);
        store.Gate(W);

        Assert.True(hook.Handle(User32.WM_KEYDOWN, Event(W)));
        Assert.True(store.Read(W).Digital);
        Assert.True(store.IsGated(W));
        Assert.False(hook.Handle(User32.WM_KEYUP, Event(W)));
        Assert.False(store.IsGated(W));
    }

    [Fact]
    public void An_injected_event_updates_the_store_outside_test_mode_but_is_not_swallowed()
    {
        var (hook, store, policy, _) = Build();

        Assert.False(hook.Handle(User32.WM_KEYDOWN, Event(W, injected: true)));
        Assert.True(store.Read(W).Digital);
        Assert.True(store.IsGated(W));
        Assert.False(hook.Handle(User32.WM_KEYUP, Event(W, injected: true)));
        Assert.False(store.Read(W).Digital);
        Assert.Equal(0, policy.SwallowedCount);
    }

    [Fact]
    public void An_extended_key_lands_in_the_e0_page_and_an_unmapped_key_is_only_observed()
    {
        var (hook, store, _, _) = Build();

        Assert.True(hook.Handle(User32.WM_SYSKEYDOWN, Event(0x48, extended: true)));
        Assert.True(store.Read(256 + 0x48).Digital);
        Assert.False(store.Read(0x48).Digital);
        Assert.False(hook.Handle(User32.WM_KEYDOWN, Event(0x48)));
        Assert.True(store.Read(0x48).Digital);
        Assert.False(store.IsGated(0x48));
    }

    [Fact]
    public void The_stop_chord_raises_the_handler_and_is_swallowed_and_a_throwing_handler_is_counted()
    {
        var (hook, _, policy, _) = Build();
        var raised = 0;
        hook.StopRequested = () =>
        {
            raised++;
            throw new InvalidOperationException("handler bug");
        };

        Assert.False(hook.Handle(User32.WM_KEYDOWN, Event(LeftCtrl)));
        Assert.False(hook.Handle(User32.WM_SYSKEYDOWN, Event(LeftAlt)));
        Assert.True(hook.Handle(User32.WM_SYSKEYDOWN, Event(F12)));
        Assert.True(hook.Handle(User32.WM_SYSKEYUP, Event(F12)));

        Assert.Equal(1, raised);
        Assert.Equal(1, hook.HandlerFaults);
        Assert.True(policy.CtrlDown);
    }

    [Fact]
    public void Ignored_events_touch_nothing()
    {
        var (hook, store, _, _) = Build();

        Assert.False(hook.Handle(User32.WM_KEYDOWN, Event(0x58, vkCode: 0xE7)));
        Assert.False(hook.Handle(User32.WM_KEYDOWN, Event(0x2A, extended: true)));

        Assert.False(store.Read(F12).Digital);
        Assert.False(store.Read(0x2A).Digital);
        Assert.False(store.Read(256 + 0x2A).Digital);
    }

    [Fact]
    public void The_callback_path_allocates_nothing()
    {
        var (hook, _, _, flag) = Build();
        var down = Event(W);
        var up = Event(W);
        var passedDown = Event(0x48, extended: true, injected: true);
        hook.Handle(User32.WM_KEYDOWN, down);
        hook.Handle(User32.WM_KEYUP, up);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            flag.IsGameForeground = (i & 1) == 0;
            hook.Handle(User32.WM_KEYDOWN, in down);
            hook.Handle(User32.WM_KEYDOWN, in passedDown);
            hook.Handle(User32.WM_KEYUP, in up);
            hook.Handle(User32.WM_KEYUP, in passedDown);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }
}

/// <summary>The real hook thread. Skips where the session refuses a low-level hook.</summary>
[Collection(ProcessSingletons.Name)]
public class KeyboardHookLifecycleTests
{
    private static KeyboardHook Build() =>
        new(new KeyStateStore(), new HookPolicy(), new ForegroundFlag(), [new ScanCode(0x11)]);

    private static void StartOrSkip(KeyboardHook hook)
    {
        try
        {
            hook.Start();
        }
        catch (Win32Exception e)
        {
            Assert.Skip("This session cannot install a low-level keyboard hook: " + e.Message);
        }
    }

    [Fact]
    public void The_timer_ticks_on_the_hook_thread_and_stop_joins()
    {
        using var hook = Build();
        var ticks = 0;
        var hookThreadTicks = 0;
        hook.Timer = () =>
        {
            ticks++;
            if (Thread.CurrentThread.Name == "apex-hook")
            {
                hookThreadTicks++;
            }
        };
        StartOrSkip(hook);

        Assert.True(hook.IsInstalled);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 3, 2000));
        Assert.True(hook.Stop());

        Assert.False(hook.IsInstalled);
        Assert.Equal(ticks, hookThreadTicks);
        Assert.Equal(0, hook.HandlerFaults);
        Assert.Null(hook.Error);
    }

    [Fact]
    public void Stop_from_the_timer_handler_returns_at_once_and_the_thread_exits()
    {
        using var hook = Build();
        var selfStop = -1;
        hook.Timer = () => selfStop = hook.Stop() ? 1 : 0;
        StartOrSkip(hook);

        Assert.True(SpinWait.SpinUntil(() => !hook.IsInstalled, 2000));

        Assert.Equal(0, selfStop);
        Assert.True(hook.Stop());
    }

    [Fact]
    public void A_throwing_timer_handler_is_counted_and_the_hook_survives()
    {
        using var hook = Build();
        hook.Timer = () => throw new InvalidOperationException("handler bug");
        StartOrSkip(hook);

        Assert.True(SpinWait.SpinUntil(() => hook.HandlerFaults >= 2, 2000));

        Assert.True(hook.IsInstalled);
        Assert.True(hook.Stop());
        Assert.Null(hook.Error);
    }

    [Fact]
    public void A_second_hook_in_the_process_is_refused_while_the_first_runs()
    {
        using var first = Build();
        StartOrSkip(first);
        using var second = Build();
        try
        {
            Assert.Throws<InvalidOperationException>(second.Start);
        }
        finally
        {
            first.Stop();
        }

        second.Start();
        Assert.True(second.Stop());
    }
}
