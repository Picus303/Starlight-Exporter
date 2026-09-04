using Microsoft.Extensions.Configuration;
using Starlight.Game.Resources;
using Starlight.Game.Resources.Binary;
using Starlight.Game.Resources.Excel;

namespace StarlightExporter.Tests;

internal static class TestGameData
{
    public static GameData Create()
    {
        var data = new GameData(new ConfigurationBuilder().Build());
        data.MaterialData[1001] = new MaterialData { Id = 1001, StackLimit = 9999 };
        data.WeaponData[11101] = new WeaponData {
            Id = 11101,
            GadgetId = 50011101,
            SkillAffix = [11101]
        };
        data.AvatarData[10000005] = new AvatarData {
            Id = 10000005,
            InitialWeapon = 11101,
            SkillDepotId = 500,
            HpBase = 100,
            AttackBase = 20,
            DefenseBase = 10,
            CritChanceBase = 0.05f,
            CritDamageBase = 0.5f
        };
        data.AvatarSkillDepotData[500] = new AvatarSkillDepotData {
            Id = 500,
            Skills = [501],
            EnergySkill = 502
        };
        data.Avatars[10000005] = new AvatarConfig();
        return data;
    }
}
