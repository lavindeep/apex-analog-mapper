using System.Text.Json;
using System.Text.Json.Serialization;
using ApexMapper.Core.Curves;
using ApexMapper.Core.Keys;
using ApexMapper.Core.Pipeline;
using ApexMapper.Core.Socd;

namespace ApexMapper.Persistence.Json;

/// <summary>
/// JSON shape for a binding's shaping curve: the optional per-binding <c>inner_deadzone</c> and
/// <c>outer_deadzone</c> floats plus a <c>curve</c> that is either <c>null</c> (linear) or an array
/// of up to eight monotonic <c>[x, y]</c> control points. The three fields compose into a single
/// runtime <see cref="ICurve"/> at load time (never per tick), so a bad curve fails loudly during
/// parse and the file is quarantined by the recovery path.
/// </summary>
internal static class BindingCurveJson
{
    internal static IReadOnlyList<(float X, float Y)>? ReadPoints(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("curve must be null or an array of [x, y] control points.");

        var points = new List<(float X, float Y)>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("each control point must be a two-element [x, y] array.");

            reader.Read();
            if (reader.TokenType != JsonTokenType.Number)
                throw new JsonException("control point x must be a number.");
            var x = reader.GetSingle();

            reader.Read();
            if (reader.TokenType != JsonTokenType.Number)
                throw new JsonException("control point y must be a number.");
            var y = reader.GetSingle();

            reader.Read();
            if (reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException("each control point must have exactly two elements.");

            points.Add((x, y));
        }

        return points;
    }

    // Build the runtime curve from its parts. A curve with default deadzones stays a bare
    // LinearCurve; anything else is wrapped in a DeadzoneCurve, whose constructor enforces
    // deadzone validity and boundary continuity.
    internal static ICurve Compose(IReadOnlyList<(float X, float Y)>? points, float inner, float outer)
    {
        ICurve baseCurve = points is null ? LinearCurve.Instance : new PiecewiseCubicCurve(points);
        return baseCurve is LinearCurve && inner == 0f && outer == 1f
            ? LinearCurve.Instance
            : new DeadzoneCurve(baseCurve, inner, outer);
    }

    // Split a runtime curve back into (inner, outer, control points) for serialization.
    internal static (float Inner, float Outer, IReadOnlyList<(float X, float Y)>? Points) Decompose(ICurve curve)
        => curve switch
        {
            LinearCurve => (0f, 1f, null),
            PiecewiseCubicCurve pc => (0f, 1f, pc.Points),
            DeadzoneCurve dz => (dz.InnerDeadzone, dz.OuterDeadzone, InnerPoints(dz.Inner)),
            _ => throw new JsonException($"Cannot serialize curve of type {curve.GetType().Name}."),
        };

    private static IReadOnlyList<(float X, float Y)>? InnerPoints(ICurve inner)
        => inner switch
        {
            LinearCurve => null,
            PiecewiseCubicCurve pc => pc.Points,
            _ => throw new JsonException($"Cannot serialize inner curve of type {inner.GetType().Name}."),
        };

    internal static void WritePoints(Utf8JsonWriter writer, IReadOnlyList<(float X, float Y)>? points)
    {
        if (points is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var (x, y) in points)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(x);
            writer.WriteNumberValue(y);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }
}

/// <summary>
/// Serializes a <see cref="SingleKeyBinding"/> with the composite deadzone/curve JSON shape.
/// </summary>
public sealed class SingleKeyBindingConverter : JsonConverter<SingleKeyBinding>
{
    public override SingleKeyBinding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();

        KeyId source = default;
        BindingTarget target = default;
        float inner = 0f, outer = 1f, press = 0f, release = 0f;
        IReadOnlyList<(float X, float Y)>? points = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "source": source = JsonSerializer.Deserialize<KeyId>(ref reader, options); break;
                case "target": target = JsonSerializer.Deserialize<BindingTarget>(ref reader, options); break;
                case "inner_deadzone": inner = reader.GetSingle(); break;
                case "outer_deadzone": outer = reader.GetSingle(); break;
                case "curve": points = BindingCurveJson.ReadPoints(ref reader); break;
                case "press_ramp_ms": press = reader.GetSingle(); break;
                case "release_ramp_ms": release = reader.GetSingle(); break;
                default: reader.Skip(); break;
            }
        }

        return new SingleKeyBinding(source, target, BindingCurveJson.Compose(points, inner, outer), press, release);
    }

    public override void Write(Utf8JsonWriter writer, SingleKeyBinding value, JsonSerializerOptions options)
    {
        var (inner, outer, points) = BindingCurveJson.Decompose(value.Curve);
        writer.WriteStartObject();
        writer.WritePropertyName("source");
        JsonSerializer.Serialize(writer, value.Source, options);
        writer.WritePropertyName("target");
        JsonSerializer.Serialize(writer, value.Target, options);
        writer.WriteNumber("inner_deadzone", inner);
        writer.WriteNumber("outer_deadzone", outer);
        writer.WritePropertyName("curve");
        BindingCurveJson.WritePoints(writer, points);
        writer.WriteNumber("press_ramp_ms", value.PressRampMs);
        writer.WriteNumber("release_ramp_ms", value.ReleaseRampMs);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Serializes an <see cref="AxisPairBinding"/> with the composite deadzone/curve JSON shape.
/// </summary>
public sealed class AxisPairBindingConverter : JsonConverter<AxisPairBinding>
{
    public override AxisPairBinding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();

        KeyId negative = default, positive = default;
        BindingTarget target = default;
        SocdMode socd = default;
        float inner = 0f, outer = 1f, press = 0f, release = 0f;
        IReadOnlyList<(float X, float Y)>? points = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "negative_key": negative = JsonSerializer.Deserialize<KeyId>(ref reader, options); break;
                case "positive_key": positive = JsonSerializer.Deserialize<KeyId>(ref reader, options); break;
                case "target": target = JsonSerializer.Deserialize<BindingTarget>(ref reader, options); break;
                case "inner_deadzone": inner = reader.GetSingle(); break;
                case "outer_deadzone": outer = reader.GetSingle(); break;
                case "curve": points = BindingCurveJson.ReadPoints(ref reader); break;
                case "press_ramp_ms": press = reader.GetSingle(); break;
                case "release_ramp_ms": release = reader.GetSingle(); break;
                case "socd": socd = JsonSerializer.Deserialize<SocdMode>(ref reader, options); break;
                default: reader.Skip(); break;
            }
        }

        return new AxisPairBinding(
            negative, positive, target, BindingCurveJson.Compose(points, inner, outer), press, release, socd);
    }

    public override void Write(Utf8JsonWriter writer, AxisPairBinding value, JsonSerializerOptions options)
    {
        var (inner, outer, points) = BindingCurveJson.Decompose(value.Curve);
        writer.WriteStartObject();
        writer.WritePropertyName("negative_key");
        JsonSerializer.Serialize(writer, value.NegativeKey, options);
        writer.WritePropertyName("positive_key");
        JsonSerializer.Serialize(writer, value.PositiveKey, options);
        writer.WritePropertyName("target");
        JsonSerializer.Serialize(writer, value.Target, options);
        writer.WriteNumber("inner_deadzone", inner);
        writer.WriteNumber("outer_deadzone", outer);
        writer.WritePropertyName("curve");
        BindingCurveJson.WritePoints(writer, points);
        writer.WriteNumber("press_ramp_ms", value.PressRampMs);
        writer.WriteNumber("release_ramp_ms", value.ReleaseRampMs);
        writer.WritePropertyName("socd");
        JsonSerializer.Serialize(writer, value.Socd, options);
        writer.WriteEndObject();
    }
}
