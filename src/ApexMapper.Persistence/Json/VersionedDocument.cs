namespace ApexMapper.Persistence.Json;

public sealed record VersionedDocument<T>(int Version, T Payload);
