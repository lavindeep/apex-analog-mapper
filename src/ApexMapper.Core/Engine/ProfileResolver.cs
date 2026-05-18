using System.Text.RegularExpressions;

namespace ApexMapper.Core.Engine;

public sealed class ProfileResolver
{
    private readonly Profile[] _profiles;

    public ProfileResolver(IReadOnlyList<Profile> profiles)
    {
        _profiles = profiles.ToArray();
    }

    public Profile? Resolve(ForegroundContext ctx, string? manualPinId)
    {
        Profile? best = null;
        var bestPrecedence = (int)ProfilePrecedence.Generic - 1;

        foreach (var p in _profiles)
        {
            var prec = Score(p, ctx, manualPinId);
            if (prec is null) continue;
            if ((int)prec > bestPrecedence)
            {
                bestPrecedence = (int)prec;
                best = p;
            }
        }
        return best;
    }

    private static ProfilePrecedence? Score(Profile p, ForegroundContext ctx, string? manualPinId)
    {
        if (manualPinId != null && p.Id == manualPinId) return ProfilePrecedence.ManualPin;

        var g = p.Game;
        var hasAny = g.ExecutableName is not null || g.WindowTitlePattern is not null || g.SteamAppId is not null;

        var exeMatch = g.ExecutableName is not null
            && ctx.ExecutableName is not null
            && string.Equals(g.ExecutableName, ctx.ExecutableName, StringComparison.OrdinalIgnoreCase);
        var appIdMatch = g.SteamAppId is not null && ctx.SteamAppId is not null && g.SteamAppId == ctx.SteamAppId;
        if (exeMatch || appIdMatch) return ProfilePrecedence.ExactExecutableOrAppId;

        if (g.WindowTitlePattern is not null && ctx.WindowTitle is not null)
        {
            if (Regex.IsMatch(ctx.WindowTitle, g.WindowTitlePattern, RegexOptions.IgnoreCase))
                return ProfilePrecedence.WindowTitle;
        }

        if (!hasAny) return ProfilePrecedence.Generic;

        return null;
    }
}
