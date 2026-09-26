using Starlight.Protobuf.Core;
using Starlight.Protocol;

namespace StarlightExporter.Official;

public sealed class OfficialSnapshotCache
{
    private const StoreType StorePack = (StoreType)1;
    private readonly Dictionary<StoreType, Dictionary<ulong, Item>> _stores = [];
    private readonly Dictionary<ulong, AvatarInfo> _avatars = [];
    private readonly Dictionary<uint, AvatarTeam> _teams = [];
    private readonly Dictionary<ulong, ulong> _pendingWeaponEquips = [];
    private bool _avatarBulkObserved;
    private bool _packObserved;

    public PlayerDataNotify? PlayerData { get; private set; }
    public IReadOnlyDictionary<ulong, Item> PackItems =>
        _stores.TryGetValue(StorePack, out Dictionary<ulong, Item>? items) ? items : EmptyItems;
    public IReadOnlyDictionary<ulong, AvatarInfo> Avatars => _avatars;
    public IReadOnlyDictionary<uint, AvatarTeam> Teams => _teams;
    public uint CurrentTeamId { get; private set; }
    public ulong ChosenAvatarGuid { get; private set; }
    public bool HasCoverage => PlayerData is not null && _avatarBulkObserved && _packObserved;
    public bool HasValidInvariants => ValidateInvariants();

    private static IReadOnlyDictionary<ulong, Item> EmptyItems { get; } =
        new Dictionary<ulong, Item>();

    public void Observe(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message)
        {
            case PlayerDataNotify player:
                PlayerData = player;
                break;

            case PlayerPropNotify properties when PlayerData is not null:
                foreach ((uint key, PropValue value) in properties.PropMap)
                {
                    PlayerData.PropMap[key] = value;
                }
                break;

            case AvatarDataNotify data:
                _avatarBulkObserved = true;
                UpsertAvatars(data.AvatarList);
                foreach ((uint id, AvatarTeam team) in data.AvatarTeamMap)
                {
                    _teams[id] = team;
                }
                if (data.CurAvatarTeamId != 0)
                {
                    CurrentTeamId = data.CurAvatarTeamId;
                }
                if (data.ChooseAvatarGuid != 0)
                {
                    ChosenAvatarGuid = data.ChooseAvatarGuid;
                }
                break;

            case AvatarAddNotify added when added.Avatar is { Guid: not 0 } avatar:
                _pendingWeaponEquips.Remove(avatar.Guid);
                _avatars[avatar.Guid] = avatar;
                ApplyAddedAvatarTeamState(avatar.Guid, added.IsInTeam);
                break;

            case AvatarDelNotify deleted:
                foreach (ulong guid in deleted.AvatarGuidList)
                {
                    _avatars.Remove(guid);
                    _pendingWeaponEquips.Remove(guid);
                    RemoveAvatarFromTeams(guid);
                    if (ChosenAvatarGuid == guid)
                    {
                        ChosenAvatarGuid = 0;
                    }
                }
                break;

            case AvatarTeamAllDataNotify allTeams:
                _teams.Clear();
                foreach ((uint id, AvatarTeam team) in allTeams.AvatarTeamMap)
                {
                    _teams[id] = team;
                }
                if (allTeams.Unknown01 != 0)
                {
                    CurrentTeamId = allTeams.Unknown01;
                }
                break;

            case AvatarTeamUpdateNotify updatedTeams:
                foreach ((uint id, AvatarTeam team) in updatedTeams.AvatarTeamMap)
                {
                    _teams[id] = team;
                }
                break;

            case PlayerStoreNotify store:
                ReplaceStore(store.StoreType, store.ItemList);
                if (store.StoreType == StorePack)
                {
                    _packObserved = true;
                    ReconcilePendingWeaponEquips();
                }
                break;

            case StoreItemChangeNotify changed:
                Dictionary<ulong, Item> changedStore = GetOrCreateStore(changed.StoreType);
                UpsertItems(changedStore, changed.ItemList);
                if (changed.StoreType == StorePack)
                {
                    ReconcilePendingWeaponEquips();
                }
                break;

            case StoreItemDelNotify deletedItems:
                Dictionary<ulong, Item> deletedStore = GetOrCreateStore(deletedItems.StoreType);
                foreach (ulong guid in deletedItems.GuidList)
                {
                    deletedStore.Remove(guid);
                }
                break;

            case AvatarEquipChangeNotify equip:
                ApplyEquipChange(equip);
                break;
        }
    }

    private void ReplaceStore(StoreType type, IEnumerable<Item> items)
    {
        Dictionary<ulong, Item> store = GetOrCreateStore(type);
        store.Clear();
        UpsertItems(store, items);
    }

    private static void UpsertItems(Dictionary<ulong, Item> store, IEnumerable<Item> items)
    {
        foreach (Item item in items)
        {
            if (item.Guid != 0)
            {
                store[item.Guid] = item;
            }
        }
    }

    private Dictionary<ulong, Item> GetOrCreateStore(StoreType type)
    {
        if (!_stores.TryGetValue(type, out Dictionary<ulong, Item>? store))
        {
            store = [];
            _stores[type] = store;
        }
        return store;
    }

    private void UpsertAvatars(IEnumerable<AvatarInfo> avatars)
    {
        foreach (AvatarInfo avatar in avatars)
        {
            if (avatar.Guid != 0)
            {
                _pendingWeaponEquips.Remove(avatar.Guid);
                _avatars[avatar.Guid] = avatar;
            }
        }
    }

    private void ApplyAddedAvatarTeamState(ulong guid, bool isInTeam)
    {
        if (!isInTeam)
        {
            RemoveAvatarFromTeams(guid);
            return;
        }
        if (CurrentTeamId != 0 && _teams.TryGetValue(CurrentTeamId, out AvatarTeam? team)
            && !team.AvatarGuidList.Contains(guid))
        {
            team.AvatarGuidList.Add(guid);
        }
    }

    private void RemoveAvatarFromTeams(ulong guid)
    {
        foreach (AvatarTeam team in _teams.Values)
        {
            while (team.AvatarGuidList.Remove(guid))
            {
            }
        }
    }

    private void ApplyEquipChange(AvatarEquipChangeNotify change)
    {
        if (!_avatars.TryGetValue(change.AvatarGuid, out AvatarInfo? avatar))
        {
            return;
        }

        if (change.EquipGuid != 0)
        {
            bool newIsWeapon = PackItems.TryGetValue(change.EquipGuid, out Item? newItem)
                && newItem.Equip?.Weapon is not null;
            if (change.Weapon is not null || newIsWeapon)
            {
                _pendingWeaponEquips[change.AvatarGuid] = change.EquipGuid;
            }
            if (newIsWeapon)
            {
                ReconcileWeaponEquip(avatar, change.EquipGuid);
            }
            if (!avatar.EquipGuidList.Contains(change.EquipGuid))
            {
                avatar.EquipGuidList.Add(change.EquipGuid);
            }
        }
    }

    private void ReconcilePendingWeaponEquips()
    {
        foreach ((ulong avatarGuid, ulong weaponGuid) in _pendingWeaponEquips.ToArray())
        {
            if (_avatars.TryGetValue(avatarGuid, out AvatarInfo? avatar)
                && PackItems.TryGetValue(weaponGuid, out Item? item)
                && item.Equip?.Weapon is not null)
            {
                ReconcileWeaponEquip(avatar, weaponGuid);
                _pendingWeaponEquips.Remove(avatarGuid);
            }
        }
    }

    private void ReconcileWeaponEquip(AvatarInfo avatar, ulong weaponGuid)
    {
        for (int index = avatar.EquipGuidList.Count - 1; index >= 0; index--)
        {
            ulong existingGuid = avatar.EquipGuidList[index];
            if (existingGuid != weaponGuid
                && PackItems.TryGetValue(existingGuid, out Item? existing)
                && existing.Equip?.Weapon is not null)
            {
                avatar.EquipGuidList.RemoveAt(index);
            }
        }
        if (!avatar.EquipGuidList.Contains(weaponGuid))
        {
            avatar.EquipGuidList.Add(weaponGuid);
        }
    }

    private bool ValidateInvariants()
    {
        if (!HasCoverage || string.IsNullOrWhiteSpace(PlayerData?.NickName))
        {
            return false;
        }
        if (_teams.Values.SelectMany(team => team.AvatarGuidList).Any(guid => !_avatars.ContainsKey(guid)))
        {
            return false;
        }
        if (_avatars.Values.SelectMany(avatar => avatar.EquipGuidList).Any(guid =>
                !PackItems.TryGetValue(guid, out Item? item) || item.Equip is null))
        {
            return false;
        }
        return _avatars.Values.Any(avatar => avatar.AvatarId is 10000005 or 10000007)
            && CurrentTeamId != 0
            && _teams.TryGetValue(CurrentTeamId, out AvatarTeam? current)
            && current.AvatarGuidList.Count != 0;
    }
}
