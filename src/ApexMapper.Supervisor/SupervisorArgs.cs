namespace ApexMapper.Supervisor;

/// <summary>
/// Parsed supervisor command line. The supervisor is launched per tray session
/// and must be told which session it serves, so <c>--session &lt;id&gt;</c> is
/// required and anything else is rejected — a malformed launch fails loudly
/// rather than serving the wrong (or no) session.
/// </summary>
internal sealed record SupervisorArgs(string SessionId)
{
    public static bool TryParse(IReadOnlyList<string> args, out SupervisorArgs? parsed, out string? error)
    {
        parsed = null;
        error = null;
        string? sessionId = null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--session":
                    if (i + 1 >= args.Count)
                    {
                        error = "Missing value for --session.";
                        return false;
                    }

                    sessionId = args[++i];
                    break;
                default:
                    error = $"Unknown argument: {args[i]}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            error = "The --session <id> argument is required.";
            return false;
        }

        parsed = new SupervisorArgs(sessionId);
        return true;
    }
}
