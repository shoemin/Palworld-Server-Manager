namespace PalworldServerManager.Core.Settings;

public static partial class PalworldSettingCatalog
{
    // Explicit enumeration of the frozen project's 95 names. Resolve remains the
    // single source of semantics; inclusion here does not infer a kind or sensitivity.
    private static readonly string[] KnownKeys =
    [
        "AdditionalDropItemNumWhenPlayerKillingInPvPMode", "AdditionalDropItemWhenPlayerKillingInPvPMode", "AdminPassword", "AllowConnectPlatform",
        "AutoResetGuildTimeNoOnlinePlayers", "BaseCampMaxNum", "BaseCampMaxNumInGuild", "BaseCampWorkerMaxNum",
        "BlockRespawnTime", "BuildObjectDamageRate", "BuildObjectDeteriorationDamageRate", "ChatPostLimitPerMinute",
        "CollectionDropRate", "CollectionObjectHpRate", "CollectionObjectRespawnSpeedRate", "CrossplayPlatforms",
        "DayTimeSpeedRate", "DeathPenalty", "DenyTechnologyList", "EnemyDropItemRate",
        "EquipmentDurabilityDamageRate", "ExpRate", "GuildPlayerMaxNum", "GuildRejoinCooldownMinutes",
        "ItemContainerForceMarkDirtyInterval", "ItemCorruptionMultiplier", "ItemWeightRate", "LogFormatType",
        "MaxBuildingLimitNum", "MonsterFarmActionSpeedRate", "NightTimeSpeedRate", "PalAutoHPRegeneRate",
        "PalAutoHpRegeneRateInSleep", "PalCaptureRate", "PalDamageRateAttack", "PalDamageRateDefense",
        "PalEggDefaultHatchingTime", "PalSpawnNumRate", "PalStaminaDecreaceRate", "PalStomachDecreaceRate",
        "PhysicsActiveDropItemMaxNum", "PlayerAutoHPRegeneRate", "PlayerAutoHpRegeneRateInSleep", "PlayerDamageRateAttack",
        "PlayerDamageRateDefense", "PlayerStaminaDecreaceRate", "PlayerStomachDecreaceRate", "PublicIP",
        "PublicPort", "RCONEnabled", "RCONPort", "RESTAPIEnabled",
        "RESTAPIPort", "RandomizerSeed", "RandomizerType", "RespawnPenaltyDurationThreshold",
        "RespawnPenaltyTimeScale", "ServerDescription", "ServerName", "ServerPassword",
        "ServerPlayerMaxNum", "ServerReplicatePawnCullDistance", "SupplyDropSpan", "VoiceChatMaxVolumeDistance",
        "VoiceChatZeroVolumeDistance", "bAdditionalDropItemWhenPlayerKillingInPvPMode", "bAllowClientMod", "bAllowEnemyCampSpawnNearBaseCamp",
        "bAllowEnhanceStat_Attack", "bAllowEnhanceStat_Health", "bAllowEnhanceStat_Stamina", "bAllowEnhanceStat_Weight",
        "bAllowEnhanceStat_WorkSpeed", "bAllowGlobalPalboxExport", "bAllowGlobalPalboxImport", "bAutoResetGuildNoOnlinePlayers",
        "bBuildAreaLimit", "bCharacterRecreateInHardcore", "bDisplayPvPItemNumOnWorldMap_BaseCamp", "bDisplayPvPItemNumOnWorldMap_Player",
        "bEnableBuildingPlayerUIdDisplay", "bEnableFastTravel", "bEnableFastTravelOnlyBaseCamp", "bEnableInvaderEnemy",
        "bEnableVoiceChat", "bExistPlayerAfterLogout", "bHardcore", "bInvisibleOtherGuildBaseCampAreaFX",
        "bIsPvP", "bIsRandomizerPalLevelRandom", "bIsShowJoinLeftMessage", "bIsStartLocationSelectByMap",
        "bIsUseBackupSaveData", "bPalLost", "bShowPlayerList",
    ];
    public static IReadOnlyList<PalworldSettingDefinition> KnownDefinitions()
        => Array.AsReadOnly(KnownKeys.Select(Resolve).ToArray());
}
