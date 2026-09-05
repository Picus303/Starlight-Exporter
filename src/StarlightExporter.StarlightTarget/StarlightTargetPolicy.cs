using Starlight.Game.Player;
using Starlight.Game.Resources;

namespace StarlightExporter.StarlightTarget;

// Compatibility rules mirrored from the pinned InventoryModule, AvatarModule and TeamModule.
// Keep this as the single exporter-owned location for Starlight rules that are not public upstream APIs.
public static class StarlightTargetPolicy
{
    public const int MaterialCountLimit = 2000;
    public const int WeaponCountLimit = 2000;
    public const uint MaxTeamCount = 4;

    public static bool CanCreateAvatar(GameData gameData, uint avatarId)
    {
        ArgumentNullException.ThrowIfNull(gameData);

        return gameData.AvatarData.TryGetValue(avatarId, out var avatar)
            && gameData.AvatarSkillDepotData.ContainsKey(avatar.SkillDepotId)
            && gameData.WeaponData.ContainsKey(avatar.InitialWeapon)
            && gameData.Avatars.ContainsKey(avatarId);
    }

    public static (uint Minimum, uint Maximum) PromotionRangeFor(uint level)
    {
        uint minimum = WeaponItem.PromoteLevelFor(level);
        uint maximum = level is 20 or 40 or 50 or 60 or 70 or 80
            ? minimum + 1
            : minimum;
        return (minimum, maximum);
    }
}
