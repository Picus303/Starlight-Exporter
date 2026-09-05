using Starlight.Game.Resources;
using Starlight.Game.Resources.Excel;
using StarlightExporter.Mapping;
using StarlightExporter.Snapshot;
using Xunit;

namespace StarlightExporter.Tests;

public sealed class StarlightSnapshotMapperEdgeTests
{
    [Fact]
    public async Task TargetResourcePoliciesAreReportedAndApplied()
    {
        OfficialSnapshot source = await ReadMinimalAsync();
        GameData data = TestGameData.Create();
        data.MaterialData[1001].UseOnGain = true;
        data.MaterialData[1002] = new MaterialData { Id = 1002, StackLimit = 0 };
        data.WeaponData[11101].GadgetId = 42;
        data.WeaponData[11101].SkillAffix = [43, 44];
        OfficialSnapshot snapshot = source with {
            Materials = [source.Materials[0], new SnapshotMaterial(1002, 10002, 10)]
        };

        StarlightMappingResult result = new StarlightSnapshotMapper(data).Map(snapshot);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: 1u, Assert.Single(result.State.Materials).Count);
        Assert.Equal(expected: 42u, Assert.Single(result.State.Weapons).GadgetId);
        Assert.Equal(expected: 43u, Assert.Single(result.State.Weapons).AffixId);
        AssertIssue(result, "UNSUPPORTED_ITEM", MappingIssueSeverity.Warning);
        AssertIssue(result, "STACK_CLAMPED", MappingIssueSeverity.Warning);
        AssertIssue(result, "WEAPON_AFFIX_AMBIGUOUS", MappingIssueSeverity.Warning);
        AssertIssue(result, "WEAPON_METADATA_REPLACED", MappingIssueSeverity.Warning);
    }

    [Fact]
    public async Task MissingWeaponResourceMakesEquippedAvatarUnmappable()
    {
        OfficialSnapshot source = await ReadMinimalAsync();
        OfficialSnapshot snapshot = source with {
            Weapons = [source.Weapons[0] with { ItemId = 99999 }]
        };
        GameData data = TestGameData.Create();

        StarlightMappingResult result = new StarlightSnapshotMapper(data).Map(snapshot);

        Assert.False(result.IsSuccess);
        AssertIssue(result, "UNSUPPORTED_ITEM", MappingIssueSeverity.Warning);
        AssertIssue(result, "AVATAR_WEAPON_UNSUPPORTED", MappingIssueSeverity.Error);
        Assert.Empty(result.State.Avatars);
    }

    [Fact]
    public async Task WeaponWithoutSkillAffixUsesZeroAndReportsCompatibilityWarning()
    {
        OfficialSnapshot snapshot = await ReadMinimalAsync();
        GameData data = TestGameData.Create();
        data.WeaponData[11101].SkillAffix.Clear();

        StarlightMappingResult result = new StarlightSnapshotMapper(data).Map(snapshot);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: 0u, Assert.Single(result.State.Weapons).AffixId);
        AssertIssue(result, "WEAPON_AFFIX_MISSING", MappingIssueSeverity.Warning);
        AssertIssue(result, "WEAPON_METADATA_REPLACED", MappingIssueSeverity.Warning);
    }

    [Fact]
    public async Task MissingAvatarResourceTableIsReported()
    {
        OfficialSnapshot snapshot = await ReadMinimalAsync();
        GameData data = TestGameData.Create();
        data.AvatarSkillDepotData.Clear();

        StarlightMappingResult result = new StarlightSnapshotMapper(data).Map(snapshot);

        Assert.False(result.IsSuccess);
        AssertIssue(result, "UNSUPPORTED_AVATAR", MappingIssueSeverity.Warning);
        AssertIssue(result, "BORN_AVATAR_UNSUPPORTED", MappingIssueSeverity.Error);
    }

    [Fact]
    public async Task CurrentTeamFallsBackToFirstNonEmptyMappedTeam()
    {
        OfficialSnapshot source = await ReadMinimalAsync();
        const uint unsupportedAvatarId = 10000006;
        const ulong unsupportedAvatarGuid = 30002;
        OfficialSnapshot snapshot = source with {
            Avatars = [
                source.Avatars[0],
                new SnapshotAvatar(unsupportedAvatarId, unsupportedAvatarGuid, 20, 0, 1, 20001)
            ],
            Teams = [
                new SnapshotTeam(1, "Unsupported", [unsupportedAvatarGuid], unsupportedAvatarGuid),
                new SnapshotTeam(2, "Fallback", [30001], 30001)
            ]
        };

        StarlightMappingResult result = new StarlightSnapshotMapper(TestGameData.Create()).Map(snapshot);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: 2u, result.State.CurrentAvatarTeamId);
        AssertIssue(result, "TEAM_REPAIRED", MappingIssueSeverity.Warning);
        AssertIssue(result, "CURRENT_TEAM_REPAIRED", MappingIssueSeverity.Warning);
    }

    [Fact]
    public async Task InventoryLimitsMatchPinnedStarlightModule()
    {
        OfficialSnapshot source = await ReadMinimalAsync();
        GameData data = TestGameData.Create();
        var materials = new List<SnapshotMaterial>();
        var weapons = new List<SnapshotWeapon>();

        for (uint index = 0; index < 2001; index++)
        {
            uint materialId = 10000 + index;
            data.MaterialData[materialId] = new MaterialData { Id = materialId, StackLimit = 99 };
            materials.Add(new SnapshotMaterial(materialId, 100000 + index, 1));
            weapons.Add(new SnapshotWeapon(11101, 200000 + index, 1, 1, 0, 11101, 50011101));
        }

        OfficialSnapshot snapshot = source with {
            Materials = materials,
            Weapons = weapons,
            Avatars = [source.Avatars[0] with { WeaponGuid = 200000 }]
        };

        StarlightMappingResult result = new StarlightSnapshotMapper(data).Map(snapshot);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected: 2000, result.State.Materials.Count);
        Assert.Equal(expected: 2000, result.State.Weapons.Count);
        AssertIssue(result, "MATERIAL_LIMIT_REACHED", MappingIssueSeverity.Warning);
        AssertIssue(result, "WEAPON_LIMIT_REACHED", MappingIssueSeverity.Warning);
    }

    private static void AssertIssue(
        StarlightMappingResult result,
        string code,
        MappingIssueSeverity severity) =>
        Assert.Contains(result.Issues, issue => issue.Code == code && issue.Severity == severity);

    private static Task<OfficialSnapshot> ReadMinimalAsync() =>
        OfficialSnapshotSerializer.ReadAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-valid.json"));
}
