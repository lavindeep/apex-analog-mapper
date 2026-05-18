using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;

namespace ApexMapper.App.Diagnostics.LogTail;

/// <summary>
/// View-model for <c>LogTailView</c>. Loads the last 200 lines from the
/// underlying <see cref="ILogTail"/>, exposes Debug/Info/Warn/Error toggles
/// that drive a filtered <see cref="Entries"/> collection, and surfaces
/// <see cref="MalformedCount"/> for the status bar.
///
/// <para>
/// Filtering is intentionally driven from the cached unfiltered snapshot
/// (<c>_loaded</c>) rather than re-querying the file system on every toggle
/// flip — toggling four checkboxes mustn't translate into four disk reads.
/// </para>
/// </summary>
public sealed class LogTailViewModel : INotifyPropertyChanged
{
    /// <summary>Default number of lines loaded by <see cref="Refresh"/>.</summary>
    public const int DefaultMaxLines = 200;

    private readonly ILogTail _tail;
    private readonly IClipboard _clipboard;
    private readonly Func<int> _readMalformedCount;
    private readonly int _maxLines;
    private IReadOnlyList<LogTailEntry> _loaded = Array.Empty<LogTailEntry>();
    private bool _showDebug = true;
    private bool _showInfo = true;
    private bool _showWarn = true;
    private bool _showError = true;
    private int _malformedCount;

    /// <summary>
    /// Convenience overload binding the malformed-count accessor to the
    /// concrete <see cref="LogTail"/> property. Production composition uses
    /// this; tests prefer the overload that takes an explicit
    /// <c>readMalformedCount</c> delegate so they can drive the value with a
    /// fake <see cref="ILogTail"/>.
    /// </summary>
    public LogTailViewModel(LogTail tail, IClipboard clipboard, int maxLines = DefaultMaxLines)
        : this(tail, clipboard, () => tail.MalformedCount, maxLines)
    {
    }

    public LogTailViewModel(
        ILogTail tail,
        IClipboard clipboard,
        Func<int> readMalformedCount,
        int maxLines = DefaultMaxLines)
    {
        ArgumentNullException.ThrowIfNull(tail);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(readMalformedCount);
        if (maxLines <= 0) throw new ArgumentOutOfRangeException(nameof(maxLines));
        _tail = tail;
        _clipboard = clipboard;
        _readMalformedCount = readMalformedCount;
        _maxLines = maxLines;
        Entries = new ObservableCollection<LogTailEntry>();
        RefreshCommand = new RelayCommand(_ => Refresh());
        CopyAllCommand = new RelayCommand(_ => CopyAll());
    }

    /// <summary>Filtered entries bound to the view.</summary>
    public ObservableCollection<LogTailEntry> Entries { get; }

    /// <summary>Number of lines that failed to parse on the last refresh.</summary>
    public int MalformedCount
    {
        get => _malformedCount;
        private set => SetProperty(ref _malformedCount, value);
    }

    /// <summary>Show <c>DEBUG</c>-level entries.</summary>
    public bool ShowDebug
    {
        get => _showDebug;
        set { if (SetProperty(ref _showDebug, value)) ReapplyFilter(); }
    }

    /// <summary>Show <c>INFO</c>-level entries.</summary>
    public bool ShowInfo
    {
        get => _showInfo;
        set { if (SetProperty(ref _showInfo, value)) ReapplyFilter(); }
    }

    /// <summary>Show <c>WARN</c>-level entries.</summary>
    public bool ShowWarn
    {
        get => _showWarn;
        set { if (SetProperty(ref _showWarn, value)) ReapplyFilter(); }
    }

    /// <summary>Show <c>ERROR</c>-level entries.</summary>
    public bool ShowError
    {
        get => _showError;
        set { if (SetProperty(ref _showError, value)) ReapplyFilter(); }
    }

    /// <summary>Reloads from disk and re-applies the current level filter.</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>Copies the currently visible (filtered) entries to the clipboard.</summary>
    public ICommand CopyAllCommand { get; }

    /// <summary>Reloads the underlying log and re-applies the active level filter.</summary>
    public void Refresh()
    {
        _loaded = _tail.Load(_maxLines);
        MalformedCount = _readMalformedCount();
        ReapplyFilter();
    }

    /// <summary>Writes the currently visible entries to <see cref="IClipboard"/>.</summary>
    public void CopyAll()
    {
        var sb = new StringBuilder();
        foreach (var entry in Entries)
        {
            // Mirror the on-disk format so users get a self-contained
            // snapshot they can paste into a bug report.
            sb.Append(entry.TimestampUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.Append(entry.Level);
            sb.Append(' ');
            sb.AppendLine(entry.Message);
        }
        _clipboard.SetText(sb.ToString());
    }

    private void ReapplyFilter()
    {
        var allowed = ActiveLevels();
        var filtered = _tail.Filter(_loaded, allowed);

        Entries.Clear();
        foreach (var entry in filtered)
        {
            Entries.Add(entry);
        }
    }

    private IReadOnlyCollection<string> ActiveLevels()
    {
        var list = new List<string>(4);
        if (_showDebug) list.Add("DEBUG");
        if (_showInfo) list.Add("INFO");
        if (_showWarn) list.Add("WARN");
        if (_showError) list.Add("ERROR");
        return list;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;

        public RelayCommand(Action<object?> execute) => _execute = execute;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
    }
}
