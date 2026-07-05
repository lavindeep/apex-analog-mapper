namespace ApexMapper.Core.Curves;

/// <summary>
/// Monotonic cubic Hermite interpolation (Fritsch-Carlson) between control
/// points. The x sequence must be strictly increasing; endpoints must be
/// at x=0 and x=1. Up to 8 control points.
/// </summary>
public sealed class PiecewiseCubicCurve : ICurve
{
    private readonly float[] _xs;
    private readonly float[] _ys;
    private readonly float[] _tangents;

    public PiecewiseCubicCurve(IReadOnlyList<(float X, float Y)> points)
    {
        if (points.Count < 2 || points.Count > 8)
        {
            throw new ArgumentException("Curve requires 2..8 control points.", nameof(points));
        }
        if (points[0].X != 0f || points[^1].X != 1f)
        {
            throw new ArgumentException("Endpoints must be at x=0 and x=1.", nameof(points));
        }
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].X <= points[i - 1].X)
            {
                throw new ArgumentException("X values must be strictly increasing.", nameof(points));
            }
        }
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].Y < 0f || points[i].Y > 1f)
            {
                throw new ArgumentException("Y values must be within [0, 1].", nameof(points));
            }
        }
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].Y < points[i - 1].Y)
            {
                throw new ArgumentException("Y values must be non-decreasing.", nameof(points));
            }
        }

        var n = points.Count;
        _xs = new float[n];
        _ys = new float[n];
        for (var i = 0; i < n; i++)
        {
            _xs[i] = points[i].X;
            _ys[i] = points[i].Y;
        }
        _tangents = ComputeMonotonicTangents(_xs, _ys);
    }

    /// <summary>The control points this curve was built from, for serialization.</summary>
    public IReadOnlyList<(float X, float Y)> Points
    {
        get
        {
            var points = new (float X, float Y)[_xs.Length];
            for (var i = 0; i < _xs.Length; i++) points[i] = (_xs[i], _ys[i]);
            return points;
        }
    }

    public float Map(float input)
    {
        if (input <= 0f) return _ys[0] < 0f ? 0f : _ys[0];
        if (input >= 1f) return _ys[^1] > 1f ? 1f : _ys[^1];

        int i = 0;
        while (i < _xs.Length - 2 && input > _xs[i + 1]) i++;

        var h = _xs[i + 1] - _xs[i];
        var t = (input - _xs[i]) / h;
        var t2 = t * t;
        var t3 = t2 * t;

        var h00 = 2 * t3 - 3 * t2 + 1;
        var h10 = t3 - 2 * t2 + t;
        var h01 = -2 * t3 + 3 * t2;
        var h11 = t3 - t2;

        var y = h00 * _ys[i] + h10 * h * _tangents[i] + h01 * _ys[i + 1] + h11 * h * _tangents[i + 1];
        return y < 0f ? 0f : y > 1f ? 1f : y;
    }

    private static float[] ComputeMonotonicTangents(float[] xs, float[] ys)
    {
        var n = xs.Length;
        var d = new float[n - 1];
        for (var i = 0; i < n - 1; i++) d[i] = (ys[i + 1] - ys[i]) / (xs[i + 1] - xs[i]);

        var m = new float[n];
        m[0] = d[0];
        m[^1] = d[^1];
        for (var i = 1; i < n - 1; i++)
        {
            // Fritsch-Carlson sign rule: at a local extremum or flat spot — adjacent secants
            // of opposite sign, or either secant zero — the tangent must be zero to keep the
            // interpolant monotone and free of overshoot.
            m[i] = d[i - 1] == 0f || d[i] == 0f || MathF.Sign(d[i - 1]) != MathF.Sign(d[i])
                ? 0f
                : (d[i - 1] + d[i]) / 2f;
        }

        for (var i = 0; i < n - 1; i++)
        {
            if (d[i] == 0f) { m[i] = 0f; m[i + 1] = 0f; continue; }
            var a = m[i] / d[i];
            var b = m[i + 1] / d[i];
            var s = a * a + b * b;
            if (s > 9f)
            {
                var tau = 3f / MathF.Sqrt(s);
                m[i] = tau * a * d[i];
                m[i + 1] = tau * b * d[i];
            }
        }
        return m;
    }
}
