using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace CS2ArmsRaceUtility;

public sealed class ArmsRaceUtilityConfig : BasePluginConfig
{
  // When true, the map's own info_player_terrorist / info_player_counterterrorist
  // spawns stay in the rotation alongside the custom ones.
  [JsonPropertyName("use_map_spawns")]
  public bool UseMapSpawns { get; set; } = true;

  // How often a custom spawn is picked when both custom and map spawns exist.
  [JsonPropertyName("custom_spawn_chance")]
  public float CustomSpawnChance { get; set; } = 0.5f;

  // When mp_randomspawn is enabled, let both teams use each other's spawn points
  [JsonPropertyName("respect_randomspawn")]
  public bool RespectRandomSpawn { get; set; } = true;

  // Small upward offset applied when teleporting a player onto a spawn point
  // to prevent being stuck in ground
  [JsonPropertyName("spawn_z_offset")]
  public float SpawnZOffset { get; set; } = 4.0f;

  // Killstreak grenade reward settings.
  [JsonPropertyName("killstreak")]
  public KillStreakRewardConfig KillStreak { get; set; } = new();
}

public sealed class KillStreakRewardConfig
{
  // When false, killstreak rewards are not given (the streak chat message still shows).
  [JsonPropertyName("enabled")]
  public bool Enabled { get; set; } = true;

  // A reward is granted every N kills (3 => 3rd, 6th, 9th kill, ...).
  [JsonPropertyName("kills_required")]
  public int KillsRequired { get; set; } = 3;

  // Random amount of grenades given per reward, inclusive.
  [JsonPropertyName("min_grenades")]
  public int MinGrenades { get; set; } = 2;

  [JsonPropertyName("max_grenades")]
  public int MaxGrenades { get; set; } = 4;

  // Grenade types that can be rolled. Accepts: molotov, incendiary (incgrenade),
  // flashbang (flash), hegrenade (he), smoke (smokegrenade), decoy.
  [JsonPropertyName("grenade_types")]
  public List<string> GrenadeTypes { get; set; } = new()
  {
    "molotov", "incendiary", "flashbang", "hegrenade", "smoke", "decoy"
  };

  // Sound played locally to the rewarded player only.
  // Leave empty to disable the sound.
  [JsonPropertyName("sound")]
  public string Sound { get; set; } = "sounds/ui/beepclear.vsnd";
}
