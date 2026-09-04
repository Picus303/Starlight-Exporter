namespace StarlightExporter.Tests;

internal static class TestResourceDirectory
{
    private static readonly string[] EmptyExcelFiles = [
        "AvatarTalentExcelConfigData.json",
        "CoopPointExcelConfigData.json",
        "GadgetExcelConfigData.json",
        "MonsterExcelConfigData.json",
        "MonsterAffixExcelConfigData.json",
        "SceneExcelConfigData.json",
        "ProudSkillExcelConfigData.json",
        "EquipAffixExcelConfigData.json"
    ];

    private static readonly string[] EmptyDirectories = [
        "BinOutput/Ability",
        "BinOutput/AbilityGroup",
        "BinOutput/AbilityPath",
        "BinOutput/GadgetPath",
        "BinOutput/Gadget",
        "BinOutput/Monster",
        "BinOutput/LevelEntity",
        "BinOutput/Talent",
        "BinOutput/Scene/Point"
    ];

    public static void Create(string path)
    {
        string excel = Path.Combine(path, "ExcelBinOutput");
        Directory.CreateDirectory(excel);
        foreach (string fileName in EmptyExcelFiles)
        {
            File.WriteAllText(Path.Combine(excel, fileName), "[]");
        }

        File.WriteAllText(
            Path.Combine(excel, "MaterialExcelConfigData.json"),
            """
            [{"id":1001,"stackLimit":9999,"itemType":"ITEM_MATERIAL","useOnGain":false}]
            """);
        File.WriteAllText(
            Path.Combine(excel, "WeaponExcelConfigData.json"),
            """
            [{"id":11101,"gadgetId":50011101,"skillAffix":[11101]}]
            """);
        File.WriteAllText(
            Path.Combine(excel, "AvatarExcelConfigData.json"),
            """
            [{"id":10000005,"iconName":"UI_AvatarIcon_PlayerBoy","initialWeapon":11101,"skillDepotId":500,"hpBase":100,"attackBase":20,"defenseBase":10,"critical":0.05,"criticalHurt":0.5}]
            """);
        File.WriteAllText(
            Path.Combine(excel, "AvatarSkillDepotExcelConfigData.json"),
            """
            [{"id":500,"skills":[501],"energySkill":502,"talents":[]}]
            """);

        foreach (string directory in EmptyDirectories)
        {
            Directory.CreateDirectory(Path.Combine(path, directory));
        }

        string common = Path.Combine(path, "BinOutput", "Common");
        Directory.CreateDirectory(common);
        File.WriteAllText(Path.Combine(common, "ConfigGlobalCombat.json"), "{}");

        string avatars = Path.Combine(path, "BinOutput", "Avatar");
        Directory.CreateDirectory(avatars);
        File.WriteAllText(Path.Combine(avatars, "ConfigAvatar_PlayerBoy.json"), "{}");
    }
}
