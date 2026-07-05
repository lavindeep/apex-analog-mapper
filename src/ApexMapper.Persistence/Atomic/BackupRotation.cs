namespace ApexMapper.Persistence.Atomic;

/// <summary>
/// Rolling backup rotation shared by the profile and device stores. Given a primary file at
/// <c>path</c>, keeps <c>path.bak.1</c> (newest) … <c>path.bak.N</c> (oldest) where N is the
/// keep-count, and copies the <em>current</em> primary into <c>.bak.1</c> atomically.
/// </summary>
internal static class BackupRotation
{
    /// <summary>
    /// Rotates existing backups up one slot (dropping any beyond <paramref name="keepCount"/>)
    /// and copies the current primary at <paramref name="primaryPath"/> into <c>.bak.1</c>.
    /// Call this while the primary still holds the content being superseded.
    /// </summary>
    public static void Rotate(string primaryPath, int keepCount)
    {
        if (keepCount < 1) return;

        // Shift .bak.{i-1} -> .bak.{i} from the top down so nothing is overwritten prematurely.
        for (var i = keepCount; i >= 2; i--)
        {
            var src = primaryPath + ".bak." + (i - 1);
            var dst = primaryPath + ".bak." + i;
            if (File.Exists(src))
            {
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
            }
        }

        // Enforce the keep-count: drop any backups beyond it (e.g. after the count was lowered).
        for (var i = keepCount + 1; ; i++)
        {
            var extra = primaryPath + ".bak." + i;
            if (!File.Exists(extra)) break;
            File.Delete(extra);
        }

        // Copy the current primary into the newest slot atomically (temp + fsync + replace).
        AtomicFile.WriteAllBytes(primaryPath + ".bak.1", File.ReadAllBytes(primaryPath));
    }
}
