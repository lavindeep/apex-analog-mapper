using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ApexMapper.App.ViewModels.Profiles;
using ApexMapper.Core.Keys;
using WpfKey = System.Windows.Input.Key;

namespace ApexMapper.App.Views.Profiles;

/// <summary>Captures a key while focused, without registering global keyboard input.</summary>
public sealed class KeyCaptureButton : Button
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key), typeof(KeyId?), typeof(KeyCaptureButton),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (sender, _) => ((KeyCaptureButton)sender).UpdateContent()));

    private bool _capturing;
    private WpfKey _suppressedKey;
    private HwndSource? _source;

    public KeyId? Key
    {
        get => (KeyId?)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public KeyCaptureButton()
    {
        ToolTip = "Click, then press a key. Escape cancels.";
        Unloaded += (_, _) => StopCapture();
        UpdateContent();
    }

    protected override void OnClick()
    {
        base.OnClick();
        Focus();
        if (_capturing) return;
        _source = PresentationSource.FromVisual(this) as HwndSource;
        if (_source is null) return;
        ComponentDispatcher.ThreadFilterMessage += CaptureMessage;
        _capturing = true;
        UpdateContent();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            WpfKey.System => e.SystemKey,
            WpfKey.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        if (key == _suppressedKey && key != WpfKey.None)
        {
            e.Handled = true;
            return;
        }

        if (!_capturing)
        {
            if (key is WpfKey.Enter or WpfKey.Space)
            {
                e.Handled = true;
                _suppressedKey = key;
                OnClick();
            }
            else base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (_capturing || _suppressedKey != WpfKey.None) e.Handled = true;
        _suppressedKey = WpfKey.None;
        base.OnPreviewKeyUp(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        StopCapture();
        _suppressedKey = WpfKey.None;
        base.OnLostKeyboardFocus(e);
    }

    // Window messages retain numpad and navigation scan codes that WPF Key loses.
    private void CaptureMessage(ref MSG message, ref bool handled)
    {
        if (handled || !IsKeyboardFocused || message.hwnd != _source?.Handle) return;
        if (message.message is not (0x0100 or 0x0104)) return; // WM_KEYDOWN / WM_SYSKEYDOWN
        handled = true;
        var flags = message.lParam.ToInt64();
        if ((flags & (1L << 30)) != 0) return;
        var virtualKey = (int)message.wParam;
        _suppressedKey = KeyInterop.KeyFromVirtualKey(virtualKey);
        if (virtualKey != 0x1B)
        {
            var key = DecodeKey(virtualKey, flags);
            if (key is null) return;
            SetCurrentValue(KeyProperty, key);
        }
        StopCapture();
    }

    internal static KeyId? DecodeKey(int virtualKey, long flags)
    {
        // Pause uses the E1 1D lead-in recognized by RawInputMessageDecoder.
        if (virtualKey == 0x13) return new KeyId(0xE11D);
        var scanCode = (ushort)((flags >> 16) & 0xFF);
        if (scanCode == 0) return null;
        if ((flags & (1L << 24)) != 0) scanCode |= 0xE000;
        return new KeyId(scanCode);
    }

    private void StopCapture()
    {
        ComponentDispatcher.ThreadFilterMessage -= CaptureMessage;
        _source = null;
        _capturing = false;
        UpdateContent();
    }

    private void UpdateContent() => Content = _capturing
        ? "Press a key"
        : Key is { } key ? BindingSummaryItem.KeyName(key) : "Choose key";

}
