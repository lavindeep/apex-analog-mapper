using System.IO;
using ApexMapper.App.Logging;
using Xunit;

namespace ApexMapper.App.Tests.Logging;

public class FileLogTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 22, 12, 0, 0, TimeSpan.FromHours(-7));

    private static Stream Append(string path) => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    [Fact]
    public void Lines_from_any_thread_are_stamped_and_written_in_order_by_the_log_thread_alone()
    {
        using var dir = new TempDirectory();
        var path = dir.File("log.txt");
        var openedOn = new List<string?>();
        var log = new FileLog(path, FileLog.MaxBytes, p =>
        {
            lock (openedOn)
            {
                openedOn.Add(Thread.CurrentThread.Name);
            }
            return Append(p);
        }, () => Noon);

        var writers = Enumerable.Range(0, 4).Select(w => new Thread(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                log.Write($"writer {w} line {i}");
            }
        })).ToArray();
        foreach (var writer in writers)
        {
            writer.Start();
        }
        foreach (var writer in writers)
        {
            writer.Join();
        }
        log.Dispose();
        log.Write("after dispose");

        var lines = File.ReadAllLines(path);
        Assert.Equal(400, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("2026-09-22 12:00:00.000 -07:00 writer ", line));
        for (var w = 0; w < 4; w++)
        {
            Assert.Equal(Enumerable.Range(0, 100).Select(i => $"line {i}"), lines.Where(l => l.Contains($"writer {w} ")).Select(l => l[(l.IndexOf("line ", StringComparison.Ordinal))..]));
        }
        Assert.NotEmpty(openedOn);
        Assert.All(openedOn, name => Assert.Equal("apex-log", name));
    }

    [Fact]
    public void The_file_rolls_so_the_two_files_stay_within_the_limit_together()
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

        var rolled = FileLog.RolledPath(path);
        Assert.Equal(dir.File("log.1.txt"), rolled);
        Assert.InRange(new FileInfo(path).Length, 1, limit / 2);
        Assert.InRange(new FileInfo(rolled).Length, 1, limit / 2);
        Assert.EndsWith("line 099", File.ReadAllLines(path)[^1]);
    }

    [Fact]
    public void A_stuck_disk_never_holds_up_a_caller_and_the_dropped_lines_are_noted()
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
        for (var i = 0; i < FileLog.QueueCapacity + 50; i++)
        {
            log.Write($"line {i}");
        }
        disk.Set();
        log.Dispose();

        var lines = File.ReadAllLines(path);
        Assert.EndsWith("first", lines[0]);
        Assert.Contains(lines, l => l.EndsWith("(50 lines dropped while the log was behind)", StringComparison.Ordinal));
        Assert.Equal(FileLog.QueueCapacity + 2, lines.Length);
        Assert.Equal(0, log.Failures);
    }

    [Fact]
    public void A_file_it_cannot_open_costs_the_batch_and_nothing_else()
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
        Assert.EndsWith("kept", Assert.Single(File.ReadAllLines(path)));
    }
}
