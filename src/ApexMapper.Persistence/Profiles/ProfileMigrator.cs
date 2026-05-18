namespace ApexMapper.Persistence.Profiles;

public static class ProfileMigrator
{
    public static bool CanMigrate(int version) => version >= 1 && version <= ProfileStore.CurrentSchemaVersion;
}
