namespace ApexMapper.Core.Storage;

/// <summary>
/// What a parser made of a file's text: the value, or why it cannot be used.
/// <see cref="Newer"/> marks text written by a newer version of the app, which this
/// version must leave exactly as it is.
/// </summary>
public readonly record struct Parsed<T>(T? Value, string? Error, bool Newer = false) where T : class;
