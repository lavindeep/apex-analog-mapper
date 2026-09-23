using System.Diagnostics;
using System.IO;
using ApexMapper.App.Logging;
using Xunit;

namespace ApexMapper.App.Tests.Logging;

public class FileLogTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 22, 12, 0, 0, TimeSpan.FromHours(-7));
    private const string NoonStamp = "2026-09-22 12:00:00.000 -07:00";

    private static Stream Append(string path) => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Reads while sharing, so a writer that is still closing its handle cannot fail the read.</summary>
    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Lines_from_any_thread_are_stamped_by_the_caller_and_written_in_order_by_the_log_thread_alone()
    {
        using var dir = new TempDirectory();
        var path = dir.File("log.txt");
        var openedOn = new List<(string? Name, bool Background)>();
        var stampedOn = new HashSet<string?>();
        var log = new FileLog(path, FileLog.MaxBytes, p =>
        {
            lock (openedOn)
            {
                openedOn.Add((Thread.CurrentThread.Name, Thread.CurrentThread.IsBackground));
            }
            return Append(p);
        }, () =>
        {
            lock (stampedOn)
            {
                stampedOn.Add(Thread.CurrentThread.Name);
            }
            return Noon;
        });

        var writers = Enumerable.Range(0, 4).Select(w => new Thread(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                log.Write($"writer {w} line {i}");
            }
        }) { Name = $"writer {w}" }).ToArray();
        foreach (var writer in writers)
        {
            writer.Start();
        }
        foreach (var writer in writers)
        {
            writer.Join();
        }
        log.Dispose();
        log.Dispose();
        log.Write("after dispose");

        var lines = ReadLines(path);
        Assert.Equal(400, lines.Length);
        Assert.All(lines, line => Assert.StartsWith($"{NoonStamp} writer ", line));
        for (var w = 0; w < 4; w++)
        {
            Assert.Equal(Enumerable.Range(0, 100).Select(i => $"line {i}"), lines.Where(l => l.Contains($"writer {w} ")).Select(l => l[l.IndexOf("line ", StringComparison.Ordinal)..]));
        }
        Assert.NotEmpty(openedOn);
        Assert.All(openedOn, opened => Assert.Equal(("apex-log", true), opened));
        Assert.DoesNotContain("apex-log", stampedOn);
    }

    [Fact]
    public void The_real_file_is_appended_to_across_runs_and_its_folder_is_created()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "new", "folder", "log.txt");

        using (var first = new FileLog(path))
        {
            first.Write("first run");
        }
        using (var second = new FileLog(path))
        {
            second.Write("second run");
        }

        var lines = ReadLines(path);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("first run", lines[0]);
        Assert.EndsWith("second run", lines[1]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} ", lines[0]);
    }

    [Fact]
    public void The_file_rolls_so_the_two_files_stay_within_the_limit_together_and_every_line_survives_the_roll()
    {
        using var dir = new TempDirectory();
        var path = dir.File("log.txt");
        const int limit = 1000;

        using (var log = new FileLog(path, limit, Append, () => Noon))
        {
            for (var i = 0; i < 100; i++)
            {
                log.Write($"line {i:D3}");
            }
        }

        // 41 bytes a line, 12 to a 500-byte file: the last two files hold the last 16 lines.
        var rolled = FileLog.RolledPath(path);
        Assert.Equal(dir.File("log.1.txt"), rolled);
        Assert.InRange(new FileInfo(path).Length, 1, limit / 2);
        Assert.InRange(new FileInfo(rolled).Length, 1, limit / 2);
        Assert.Equal(Enumerable.Range(84, 16).Select(i => $"{NoonStamp} line {i:D3}"), [.. ReadLines(rolled), .. ReadLines(path)]);
    }

    [Fact]
    public void A_file_left_by_an_earlier_run_counts_toward_the_roll_and_an_oversized_line_still_lands()
    {
        using var dir = new TempDirectory();
        var path = dir.File("log.txt");
        File.WriteAllText(path, new string('x', 480) + Environment.NewLine);

        using (var log = new FileLog(path, 1000, Append, () => Noon))
        {
            log.Write("next run");
        }
        Assert.Equal([new string('x', 480)], ReadLines(FileLog.RolledPath(path)));
        Assert.Equal([$"{NoonStamp} next run"], ReadLines(path));

        File.Delete(path);
        File.Delete(FileLog.RolledPath(path));
        var oversized = new string('y', 200);
        var small = new FileLog(path, 100, Append, () => Noon);
        small.Write(oversized);
        small.Dispose();
        Assert.Equal(0, small.Failures);
        Assert.Equal([$"{NoonStamp} {oversized}"], ReadLines(path));
    }

    [Fact]
    public void While_the_roll_is_blocked_lines_go_on_up_to_the_whole_limit_and_the_rest_are_noted_once_it_rolls()
    {
        using var dir = new TempDirectory();
        var path = dir.File("log.txt");
        var rolled = FileLog.RolledPath(path);
        File.WriteAllText(rolled, "held by a viewer" + Environment.NewLine);
        var log = new FileLog(path, 1000, Append, () => Noon);

        // Held with every sharing mode, as a tail that followed the last roll holds it.
        using (new FileStream(rolled, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            for (var i = 0; i < 40; i++)
            {
                log.Write($"line {i:D3}");
            }
            // 24 lines of 41 bytes fill the 1000-byte limit; the other 16 are lost. Every line
            // must be handled before the hold ends, or a later batch would roll and write them.
            Assert.True(SpinWait.SpinUntil(() => log.UnnotedDrops == 16, 2000), "every line to be written or counted");
            Assert.Equal(24, ReadLines(path).Length);
        }
        log.Write("after");
        log.Dispose();

        Assert.True(log.Failures > 0);
        Assert.Equal(Enumerable.Range(0, 24).Select(i => $"{NoonStamp} line {i:D3}"), ReadLines(rolled));
        Assert.Equal([$"{NoonStamp} after", $"{NoonStamp} (16 lines dropped from {NoonStamp} on, while the log was behind)"], ReadLines(path));
    }

    [Fact]
    public async Task A_stuck_disk_never_holds_up_a_caller_and_the_dropped_lines_are_noted_once()
    {
        using var dir = new TempDirectory();
        var path = dir.File("log.txt");
        using var disk = new ManualResetEventSlim(false);
        using var stuck = new ManualResetEventSlim(false);
        var log = new FileLog(path, FileLog.MaxBytes, p =>
        {
            stuck.Set();
            disk.Wait(TestContext.Current.CancellationToken);
            return Append(p);
        }, () => Noon);

        log.Write("first");
        Assert.True(stuck.Wait(2000, TestContext.Current.CancellationToken));
        // The writer is inside the open: every one of these must queue or drop, never wait.
        var writes = Task.Run(() =>
        {
            for (var i = 0; i < FileLog.QueueCapacity + 50; i++)
            {
                log.Write($"line {i}");
            }
        }, TestContext.Current.CancellationToken);
        Assert.True(writes == await Task.WhenAny(writes, Task.Delay(2000, TestContext.Current.CancellationToken)), "a write waited on the stuck disk");
        disk.Set();
        // First, the note, and the 1024 that queued: only then is there room for another line.
        Assert.True(SpinWait.SpinUntil(() => File.Exists(path) && ReadLines(path).Length == FileLog.QueueCapacity + 2, 2000), "the backlog to drain");
        log.Write("later");
        log.Dispose();

        var lines = ReadLines(path);
        Assert.EndsWith("first", lines[0]);
        Assert.Single(lines, l => l.EndsWith($"(50 lines dropped from {NoonStamp} on, while the log was behind)", StringComparison.Ordinal));
        Assert.EndsWith("later", lines[^1]);
        Assert.Equal(FileLog.QueueCapacity + 3, lines.Length);
        Assert.Equal(0, log.Failures);
    }

    [Fact]
    public void A_file_it_cannot_open_costs_its_batch_and_the_loss_is_noted_later()
    {
        using var dir = new TempDirectory();
        var path = dir.File("log.txt");
        var fail = true;
        using var opened = new SemaphoreSlim(0);
        var log = new FileLog(path, FileLog.MaxBytes, p =>
        {
            var failing = Volatile.Read(ref fail);
            opened.Release();
            return failing ? throw new UnauthorizedAccessException("denied") : Append(p);
        }, () => Noon);

        log.Write("lost");
        Assert.True(opened.Wait(2000, TestContext.Current.CancellationToken));
        Volatile.Write(ref fail, false);
        log.Write("kept");
        log.Dispose();

        Assert.Equal(1, log.Failures);
        Assert.Equal([$"{NoonStamp} kept", $"{NoonStamp} (1 line dropped from {NoonStamp} on, while the log was behind)"], ReadLines(path));
    }

    [Fact]
    public void A_close_that_fails_is_counted_and_the_log_thread_carries_on()
    {
        using var dir = new TempDirectory();
        var path = dir.File("log.txt");
        using var closed = new SemaphoreSlim(0);
        var opens = 0;
        var log = new FileLog(path, FileLog.MaxBytes, p =>
            Interlocked.Increment(ref opens) == 1 ? new FailingClose(Append(p), closed) : Append(p), () => Noon);

        log.Write("one");
        Assert.True(closed.Wait(2000, TestContext.Current.CancellationToken));
        log.Write("two");
        log.Dispose();

        Assert.Equal(1, log.Failures);
        Assert.Equal(2, opens);
        Assert.Equal([$"{NoonStamp} one", $"{NoonStamp} two"], ReadLines(path));
    }

    [Fact]
    public async Task Dispose_gives_up_on_a_stuck_disk_within_its_bound_and_the_writer_finishes_later()
    {
        using var dir = new TempDirectory();
        var path = dir.File("log.txt");
        using var disk = new ManualResetEventSlim(false);
        using var stuck = new ManualResetEventSlim(false);
        var log = new FileLog(path, FileLog.MaxBytes, p =>
        {
            stuck.Set();
            disk.Wait(TestContext.Current.CancellationToken);
            return Append(p);
        }, () => Noon);
        log.Write("slow");
        Assert.True(stuck.Wait(2000, TestContext.Current.CancellationToken));

        var clock = Stopwatch.StartNew();
        var disposing = Task.Run(log.Dispose, TestContext.Current.CancellationToken);
        Assert.True(disposing == await Task.WhenAny(disposing, Task.Delay(FileLog.DisposeWaitMs + 2000, TestContext.Current.CancellationToken)), "Dispose waited on a stuck writer");
        Assert.InRange(clock.ElapsedMilliseconds, FileLog.DisposeWaitMs - 100, FileLog.DisposeWaitMs + 2000);

        disk.Set();
        Assert.True(SpinWait.SpinUntil(() => File.Exists(path) && ReadLines(path).Length == 1, 2000), "the writer to finish once the disk came back");
    }

    /// <summary>A stream whose close throws after closing, as a flush to a full disk would.</summary>
    private sealed class FailingClose(Stream inner, SemaphoreSlim closed) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            inner.Dispose();
            closed.Release();
            throw new IOException("disk full on close");
        }
    }
}
