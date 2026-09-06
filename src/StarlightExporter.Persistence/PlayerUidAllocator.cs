namespace StarlightExporter.Persistence;

public enum PlayerUidMode
{
    Preserve,
    Allocate,
}

public static class PlayerUidAllocator
{
    public const uint FirstAllocatedUid = 100_000_000;

    public static uint Resolve(
        PlayerUidMode mode,
        uint officialUid,
        IEnumerable<uint>? existingUids = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(officialUid);

        if (mode == PlayerUidMode.Preserve)
        {
            if (existingUids?.Contains(officialUid) == true)
            {
                throw new InvalidOperationException("The preserved player UID already exists.");
            }

            return officialUid;
        }

        if (mode != PlayerUidMode.Allocate)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        uint highest = FirstAllocatedUid - 1;
        if (existingUids is not null)
        {
            foreach (uint uid in existingUids)
            {
                if (uid >= highest)
                {
                    highest = uid;
                }
            }
        }

        if (highest == uint.MaxValue)
        {
            throw new InvalidOperationException("No private player UID remains available.");
        }

        return highest + 1;
    }
}
