using System.Diagnostics;
using ApexMapper.Core.Keys;
using FluentAssertions;

namespace ApexMapper.Core.Tests.Keys;

public class KeyStateStoreConcurrentTests
{
    private static KeyId K(ushort s) => KeyId.FromScanCode(s);

    [Fact]
    public void Indexed_ctor_round_trips_value_and_provenance()
    {
        var idx = new KeyIndex(new[] { K(0x11) });
        var store = new KeyStateStore(idx);

        store.Set(K(0x11), 0.42f, KeyProvenance.Analog);

        var state = store.Get(K(0x11));
        state.Value.Should().BeApproximately(0.42f, 1e-6f);
        state.Source.Should().Be(KeyProvenance.Analog);
    }

    [Fact]
    public void Indexed_Get_returns_Rest_for_unknown_key()
    {
        var idx = new KeyIndex(new[] { K(0x11) });
        var store = new KeyStateStore(idx);

        var state = store.Get(K(0xDEAD));
        state.Should().Be(KeyState.Rest);
    }

    [Fact]
    public void Indexed_Set_is_silent_no_op_for_unknown_key()
    {
        var idx = new KeyIndex(new[] { K(0x11) });
        var store = new KeyStateStore(idx);

        var act = () => store.Set(K(0xBEEF), 0.5f, KeyProvenance.Analog);

        act.Should().NotThrow();
        store.Get(K(0xBEEF)).Should().Be(KeyState.Rest);
        store.Get(K(0x11)).Should().Be(KeyState.Rest);
    }

    [Fact]
    public void Indexed_Set_clamps_value_to_unit_interval()
    {
        var idx = new KeyIndex(new[] { K(0x10) });
        var store = new KeyStateStore(idx);

        store.Set(K(0x10), 1.5f, KeyProvenance.Analog);
        store.Get(K(0x10)).Value.Should().Be(1f);

        store.Set(K(0x10), -0.5f, KeyProvenance.Analog);
        store.Get(K(0x10)).Value.Should().Be(0f);
    }

    [Fact]
    public void Indexed_Keys_returns_all_registered_keys_even_when_unset()
    {
        var keys = new[] { K(0x11), K(0x1E), K(0x20) };
        var idx = new KeyIndex(keys);
        var store = new KeyStateStore(idx);

        store.Keys.Should().BeEquivalentTo(keys);
    }

    [Fact]
    public void Indexed_Reset_zeros_all_cells()
    {
        var idx = new KeyIndex(new[] { K(0x11), K(0x1E) });
        var store = new KeyStateStore(idx);

        store.Set(K(0x11), 0.9f, KeyProvenance.Analog);
        store.Set(K(0x1E), 0.3f, KeyProvenance.Analog);

        store.Reset();

        store.Get(K(0x11)).Should().Be(KeyState.Rest);
        store.Get(K(0x1E)).Should().Be(KeyState.Rest);
    }

    [Fact]
    public async Task Indexed_store_survives_concurrent_writers_and_a_reader()
    {
        var a = K(0x11);
        var b = K(0x1E);
        var shared = K(0x20);
        var idx = new KeyIndex(new[] { a, b, shared });
        var store = new KeyStateStore(idx);

        const int iterations = 100_000;
        using var start = new CountdownEvent(1);
        var stop = false;
        var exceptions = new List<Exception>();
        var sync = new object();

        void Record(Action body)
        {
            try
            {
                start.Wait();
                body();
            }
            catch (Exception ex)
            {
                lock (sync) { exceptions.Add(ex); }
            }
        }

        var writerA = Task.Run(() => Record(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var v = (i & 1023) / 1023f;
                store.Set(a, v, KeyProvenance.Analog);
            }
        }));

        var writerB = Task.Run(() => Record(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var v = ((i * 7) & 1023) / 1023f;
                store.Set(b, v, KeyProvenance.Digital);
            }
        }));

        var writerShared = Task.Run(() => Record(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var v = ((i & 1) == 0) ? 0.25f : 0.75f;
                store.Set(shared, v, KeyProvenance.Analog);
            }
        }));

        var reader = Task.Run(() => Record(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var sa = store.Get(a);
                var sb = store.Get(b);
                var ss = store.Get(shared);

                sa.Value.Should().BeInRange(0f, 1f);
                sb.Value.Should().BeInRange(0f, 1f);
                ss.Value.Should().BeInRange(0f, 1f);
            }
        }));

        var sw = Stopwatch.StartNew();
        start.Signal();

        var writersAll = Task.WhenAll(writerA, writerB, writerShared);
        var writersCompleted = await Task.WhenAny(writersAll, Task.Delay(TimeSpan.FromSeconds(5)));
        writersCompleted.Should().BeSameAs(writersAll, "writers should finish within 5 seconds");

        Volatile.Write(ref stop, true);

        var readerCompleted = await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(5)));
        readerCompleted.Should().BeSameAs(reader, "reader should observe stop and exit within 5 seconds");

        sw.Stop();

        await writersAll;
        await reader;

        exceptions.Should().BeEmpty();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Indexed_reader_sees_writer_value_within_short_deadline()
    {
        var key = K(0x21);
        var idx = new KeyIndex(new[] { key });
        var store = new KeyStateStore(idx);

        using var writerReady = new ManualResetEventSlim(false);

        var writer = Task.Run(() =>
        {
            store.Set(key, 0.7f, KeyProvenance.Analog);
            writerReady.Set();
        });

        // Generous bound: this waits on thread-pool SCHEDULING of the writer,
        // not on the visibility property under test - saturated CI runners can
        // take multiple seconds to run a queued task.
        writerReady.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        var deadline = Stopwatch.StartNew();
        KeyState observed = default;
        // Visibility after the release/acquire pair on writerReady is immediate
        // for a correct store; a broken store never converges, so a generous
        // deadline keeps full detection power without CI-preemption flakes.
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            observed = store.Get(key);
            if (observed.Value == 0.7f && observed.Source == KeyProvenance.Analog)
            {
                break;
            }
        }

        observed.Value.Should().BeApproximately(0.7f, 1e-6f);
        observed.Source.Should().Be(KeyProvenance.Analog);

        var writerCompleted = await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(10)));
        writerCompleted.Should().BeSameAs(writer);
        await writer;
    }
}
