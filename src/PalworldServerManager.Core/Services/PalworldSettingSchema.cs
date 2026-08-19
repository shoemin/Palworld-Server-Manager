using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

public static class PalworldSettingSchema
{
    private static readonly Dictionary<string, SettingDefinition> Definitions = Build()
        .ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

    public static SettingDefinition? Find(string key)
        => Definitions.TryGetValue(key, out var definition) ? definition : null;

    private static IEnumerable<SettingDefinition> Build()
    {
        static SettingDefinition D(string key, string category, string description) => new(key, category, description);

        // Current Pocketpair 1.0.3 server-guide settings. The editor also discovers unknown/new keys dynamically.
        yield return D("BaseCampMaxNum", "Performance", "Total number of bases across the server.");
        yield return D("BaseCampMaxNumInGuild", "Performance", "Maximum number of bases per guild. Higher values increase load.");
        yield return D("BaseCampWorkerMaxNum", "Performance", "Maximum Pals per base. Higher values increase load.");
        yield return D("ItemContainerForceMarkDirtyInterval", "Performance", "Container UI force-resync interval in seconds.");
        yield return D("MaxBuildingLimitNum", "Performance", "Per-player building cap; 0 means unlimited.");
        yield return D("PhysicsActiveDropItemMaxNum", "Performance", "Maximum dropped items using physics behavior.");
        yield return D("ServerReplicatePawnCullDistance", "Performance", "Pal synchronization distance in centimeters.");

        yield return D("AdminPassword", "Server Management", "Administrator password used by server administration interfaces.");
        yield return D("AllowConnectPlatform", "Server Management", "Deprecated/reserved; use CrossplayPlatforms.");
        yield return D("bAllowClientMod", "Server Management", "Allow players with mods enabled to join.");
        yield return D("bEnableBuildingPlayerUIdDisplay", "Server Management", "Display creator player ID on structures.");
        yield return D("bIsShowJoinLeftMessage", "Server Management", "Show join/leave messages.");
        yield return D("bIsUseBackupSaveData", "Server Management", "Enable Palworld's built-in world backups; increases disk load.");
        yield return D("ChatPostLimitPerMinute", "Server Management", "Maximum chat messages per minute.");
        yield return D("CrossplayPlatforms", "Server Management", "Allowed platforms, e.g. (Steam,Xbox,PS5,Mac).");
        yield return D("LogFormatType", "Server Management", "Server log format: Text or Json.");
        yield return D("PublicIP", "Server Management", "Explicit public IP for community-server advertising.");
        yield return D("PublicPort", "Server Management", "Advertised public port; does not change the listening port.");
        yield return D("RCONEnabled", "Server Management", "Enable deprecated RCON interface.");
        yield return D("RCONPort", "Server Management", "RCON listening port.");
        yield return D("RESTAPIEnabled", "Server Management", "Enable Palworld REST administration API.");
        yield return D("RESTAPIPort", "Server Management", "Local REST API listening port.");
        yield return D("ServerDescription", "Server Management", "Server description.");
        yield return D("ServerName", "Server Management", "Server display name.");
        yield return D("ServerPassword", "Server Management", "Password required for players to join.");
        yield return D("ServerPlayerMaxNum", "Server Management", "Maximum players allowed on the server.");

        yield return D("AutoResetGuildTimeNoOnlinePlayers", "Features", "Offline duration before automatic guild cleanup can trigger.");
        yield return D("bAllowEnemyCampSpawnNearBaseCamp", "Features", "Allow enemy camps to spawn near player bases.");
        yield return D("bAllowEnhanceStat_Attack", "Features", "Allow stat points in Attack.");
        yield return D("bAllowEnhanceStat_Health", "Features", "Allow stat points in Health.");
        yield return D("bAllowEnhanceStat_Stamina", "Features", "Allow stat points in Stamina.");
        yield return D("bAllowEnhanceStat_Weight", "Features", "Allow stat points in Carry Weight.");
        yield return D("bAllowEnhanceStat_WorkSpeed", "Features", "Allow stat points in Work Speed.");
        yield return D("bAllowGlobalPalboxExport", "Features", "Allow saving Pals to the Global Palbox.");
        yield return D("bAllowGlobalPalboxImport", "Features", "Allow loading Pals from the Global Palbox.");
        yield return D("bAutoResetGuildNoOnlinePlayers", "Features", "Automatically clean up guild structures/base Pals after prolonged inactivity.");
        yield return D("bBuildAreaLimit", "Features", "Prevent building near protected structures such as fast-travel points.");
        yield return D("bCharacterRecreateInHardcore", "Features", "Allow character recreation after death in Hardcore.");
        yield return D("bDisplayPvPItemNumOnWorldMap_BaseCamp", "Features", "Show PvP-exclusive item count at bases on the map.");
        yield return D("bDisplayPvPItemNumOnWorldMap_Player", "Features", "Show player locations/PvP item counts on the map.");
        yield return D("bEnableFastTravel", "Features", "Enable fast travel.");
        yield return D("bEnableFastTravelOnlyBaseCamp", "Features", "Restrict fast travel to bases.");
        yield return D("bEnableInvaderEnemy", "Features", "Enable invader events.");
        yield return D("bEnableVoiceChat", "Features", "Enable in-game voice chat.");
        yield return D("bExistPlayerAfterLogout", "Features", "Leave logged-out players sleeping at their location.");
        yield return D("bHardcore", "Features", "Enable Hardcore mode.");
        yield return D("bInvisibleOtherGuildBaseCampAreaFX", "Features", "Control visibility of other guild base-area boundaries.");
        yield return D("bIsPvP", "Features", "Enable PvP.");
        yield return D("bIsRandomizerPalLevelRandom", "Features", "Fully randomize wild Pal levels when randomizer is active.");
        yield return D("bIsStartLocationSelectByMap", "Features", "Allow players to choose their starting location.");
        yield return D("bShowPlayerList", "Features", "Enable the ESC-menu player list.");
        yield return D("RandomizerSeed", "Features", "Seed used by Pal spawn randomization.");
        yield return D("RandomizerType", "Features", "Pal spawn randomization mode: None, Region, or All.");
        yield return D("VoiceChatMaxVolumeDistance", "Features", "Distance before voice volume begins attenuating.");
        yield return D("VoiceChatZeroVolumeDistance", "Features", "Distance where voice chat becomes inaudible.");

        yield return D("AdditionalDropItemNumWhenPlayerKillingInPvPMode", "Game Balance", "Quantity of special PvP kill drop.");
        yield return D("AdditionalDropItemWhenPlayerKillingInPvPMode", "Game Balance", "Item ID of special PvP kill drop.");
        yield return D("bAdditionalDropItemWhenPlayerKillingInPvPMode", "Game Balance", "Enable special item drops for PvP player kills.");
        yield return D("BlockRespawnTime", "Game Balance", "Respawn cooldown after death in seconds.");
        yield return D("bPalLost", "Game Balance", "Permanently lose Pals on death.");
        yield return D("BuildObjectDamageRate", "Game Balance", "Damage multiplier applied to buildings.");
        yield return D("BuildObjectDeteriorationDamageRate", "Game Balance", "Building deterioration multiplier.");
        yield return D("CollectionDropRate", "Game Balance", "Gatherable item quantity multiplier.");
        yield return D("CollectionObjectHpRate", "Game Balance", "Gatherable object health multiplier.");
        yield return D("CollectionObjectRespawnSpeedRate", "Game Balance", "Gatherable object respawn interval multiplier.");
        yield return D("DayTimeSpeedRate", "Game Balance", "Daytime progression speed multiplier.");
        yield return D("DeathPenalty", "Game Balance", "Death penalty: None, Item, ItemAndEquipment, or All.");
        yield return D("DenyTechnologyList", "Game Balance", "Technology IDs disabled on the server.");
        yield return D("EnemyDropItemRate", "Game Balance", "Enemy drop quantity multiplier.");
        yield return D("EquipmentDurabilityDamageRate", "Game Balance", "Equipment durability loss multiplier.");
        yield return D("ExpRate", "Game Balance", "Experience gain multiplier.");
        yield return D("GuildPlayerMaxNum", "Game Balance", "Maximum players per guild.");
        yield return D("GuildRejoinCooldownMinutes", "Game Balance", "Guild rejoin cooldown in minutes.");
        yield return D("ItemCorruptionMultiplier", "Game Balance", "Item corruption speed multiplier.");
        yield return D("ItemWeightRate", "Game Balance", "Item weight multiplier.");
        yield return D("MonsterFarmActionSpeedRate", "Game Balance", "Grazing production speed multiplier.");
        yield return D("NightTimeSpeedRate", "Game Balance", "Nighttime progression speed multiplier.");
        yield return D("PalAutoHPRegeneRate", "Game Balance", "Pal natural HP regeneration multiplier.");
        yield return D("PalAutoHpRegeneRateInSleep", "Game Balance", "Pal HP regeneration while sleeping/in Palbox.");
        yield return D("PalCaptureRate", "Game Balance", "Pal capture rate multiplier.");
        yield return D("PalDamageRateAttack", "Game Balance", "Damage dealt by Pals multiplier.");
        yield return D("PalDamageRateDefense", "Game Balance", "Damage taken by Pals multiplier.");
        yield return D("PalEggDefaultHatchingTime", "Game Balance", "Huge Egg base hatching time in hours.");
        yield return D("PalSpawnNumRate", "Game Balance", "Pal spawn-rate multiplier; higher values can affect performance.");
        yield return D("PalStaminaDecreaceRate", "Game Balance", "Pal stamina depletion multiplier.");
        yield return D("PalStomachDecreaceRate", "Game Balance", "Pal hunger depletion multiplier.");
        yield return D("PlayerAutoHPRegeneRate", "Game Balance", "Player natural HP regeneration multiplier.");
        yield return D("PlayerAutoHpRegeneRateInSleep", "Game Balance", "Player HP regeneration while sleeping.");
        yield return D("PlayerDamageRateAttack", "Game Balance", "Damage dealt by players multiplier.");
        yield return D("PlayerDamageRateDefense", "Game Balance", "Damage taken by players multiplier.");
        yield return D("PlayerStaminaDecreaceRate", "Game Balance", "Player stamina depletion multiplier.");
        yield return D("PlayerStomachDecreaceRate", "Game Balance", "Player hunger depletion multiplier.");
        yield return D("RespawnPenaltyDurationThreshold", "Game Balance", "Survival-time threshold for scaled respawn cooldown.");
        yield return D("RespawnPenaltyTimeScale", "Game Balance", "Multiplier applied to the respawn cooldown.");
        yield return D("SupplyDropSpan", "Game Balance", "Meteorite/supply drop interval in minutes.");
    }
}
