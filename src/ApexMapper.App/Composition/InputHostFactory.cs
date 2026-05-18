namespace ApexMapper.App.Composition;

/// <summary>
/// Factory placeholder for wiring <c>InputHost</c> from Phase 2
/// (<c>ApexMapper.Input</c>).
///
/// TODO (Phase 3+ integration): InputHost requires a raw-input adapter (Windows
/// message pump context), a HID provider, and a running poll loop — all of which
/// need a live Win32 HWND that only exists after the WPF Application has started.
/// Until Phase 3 defines the integration contract, constructing InputHost here
/// would couple the composition root to Win32 bootstrap ordering details that are
/// not yet decided.
///
/// For now this factory simply throws <see cref="NotImplementedException"/> so that
/// callers that try to resolve InputHost will fail fast with an actionable message.
/// The composition root does NOT register InputHost in the DI container — this
/// class is purely a documentation anchor for Phase 3.
/// </summary>
public static class InputHostFactory
{
    /// <summary>
    /// Not yet implemented.  Will be wired in Phase 3 integration.
    /// </summary>
    /// <exception cref="NotImplementedException">Always thrown.</exception>
    public static object Create(IServiceProvider _)
        => throw new NotImplementedException(
            "InputHostFactory pending Phase 3+ integration. " +
            "InputHost requires a live Win32 HWND — wire it from App.xaml.cs after the WPF pump starts.");
}
