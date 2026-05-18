namespace ApexMapper.App.Diagnostics;

/// <summary>
/// A single latency observation drained from the binding pipeline.
/// </summary>
public readonly record struct LatencySample(long TimestampMicros, long LatencyMicros);
