namespace ApexMapper.Persistence.Profiles;

public sealed record ProfileStoreOptions(string Directory, int BackupCount = 5);
