using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API;
using System.Runtime.InteropServices;
using System.Text;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2ArmsRaceUtility;

[MinimumApiVersion(80)]
public partial class ArmsRaceUtilityPlugin : BasePlugin, IPluginConfig<ArmsRaceUtilityConfig>
{
  public override string ModuleName => "Arms Race Utility";
  public override string ModuleVersion => "1.5.0";
  public override string ModuleAuthor => "haencyl";
  public override string ModuleDescription => "Various fun additions for Arms Race";

  // aliases are the names accepted in the config
  private static readonly (string Item, string Label, int AmmoIndex, string[] Aliases)[] GrenadeCatalog = new[]
  {
    ("weapon_molotov", "MOLOTOV", 16, new[] { "molotov" }),
    ("weapon_incgrenade", "INCENDIARY", 16, new[] { "incendiary", "incgrenade" }),
    ("weapon_flashbang", "FLASHBANG", 14, new[] { "flashbang", "flash" }),
    ("weapon_hegrenade", "HE GRENADE", 13, new[] { "hegrenade", "he" }),
    ("weapon_smokegrenade", "SMOKE", 15, new[] { "smokegrenade", "smoke" }),
    ("weapon_decoy", "DECOY", 17, new[] { "decoy" })
  };

  private static readonly Dictionary<string, (string Item, string Label, int AmmoIndex)> GrenadeByName =
    GrenadeCatalog
      .SelectMany(grenade => grenade.Aliases
        .Append(grenade.Item)
        .Select(alias => (Alias: alias, Entry: (grenade.Item, grenade.Label, grenade.AmmoIndex))))
      .ToDictionary(x => x.Alias, x => x.Entry, StringComparer.OrdinalIgnoreCase);

  private const int MaxGrenadesPerReward = 10;
  private List<(string Item, string Label, int AmmoIndex)> _grenadePool =
    GrenadeCatalog.Select(grenade => (grenade.Item, grenade.Label, grenade.AmmoIndex)).ToList();

  private Dictionary<IntPtr, int> _killStreaks = new();

  public ArmsRaceUtilityConfig Config { get; set; } = new();

  public void OnConfigParsed(ArmsRaceUtilityConfig config)
  {
    Config = config;
    _grenadePool = BuildGrenadePool(config.KillStreak?.GrenadeTypes);
    _spawnBags.Clear();

    _mapSpawnPointsLoaded = false;
    _mapSpawnsT.Clear();
    _mapSpawnsCT.Clear();
  }

  private static List<(string Item, string Label, int AmmoIndex)> BuildGrenadePool(IEnumerable<string>? configuredTypes)
  {
    var pool = new List<(string Item, string Label, int AmmoIndex)>();

    if (configuredTypes != null)
    {
      foreach (var name in configuredTypes)
      {
        if (string.IsNullOrWhiteSpace(name) || !GrenadeByName.TryGetValue(name.Trim(), out var entry))
        {
          continue;
        }

        if (pool.Any(grenade => grenade.Item == entry.Item))
        {
          continue;
        }

        pool.Add(entry);
      }
    }

    if (pool.Count == 0)
    {
      pool.AddRange(GrenadeCatalog.Select(grenade => (grenade.Item, grenade.Label, grenade.AmmoIndex)));
    }

    return pool;
  }

  public override void Load(bool hotReload)
  {
    Console.WriteLine("Arms Race Utility loaded!");
    RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
    RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
    RegisterEventHandler<EventRoundStart>(OnRoundStart);

    RegisterListener<Listeners.OnMapStart>(OnSpawnMapStart);
    RegisterListener<Listeners.OnMapEnd>(OnSpawnMapEnd);

    if (!string.IsNullOrWhiteSpace(Server.MapName))
    {
      LoadSpawnEditor(Server.MapName, mapReady: hotReload);
    }
  }

  public override void Unload(bool hotReload)
  {
    ClearSpawnVisualization();
  }

  private void OnSpawnMapStart(string mapName)
  {
    LoadSpawnEditor(mapName, mapReady: false);
    Server.NextFrame(RefreshMapSpawnPoints);
  }

  private void OnSpawnMapEnd()
  {
    ClearSpawnVisualization();
  }

  private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
  {
    RefreshMapSpawnPoints();

    if (!_visualsWarmed)
    {
      _visualsWarmed = true;
      Server.NextFrame(WarmUpVisualization);
    }

    return HookResult.Continue;
  }

  private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
  {
    var attacker = @event.Attacker;
    var victim = @event.Userid;

    if (attacker == null || attacker.IsBot || attacker == victim)
    {
      return HookResult.Continue;
    }

    var attackerPawn = attacker.PlayerPawn.Value;
    if (attackerPawn == null)
    {
      return HookResult.Continue;
    }

    var pawnHandle = attackerPawn.Handle;
    var currentStreak = _killStreaks.TryGetValue(pawnHandle, out var streak) ? streak : 0;
    var newStreak = currentStreak + 1;
    _killStreaks[pawnHandle] = newStreak;

    var victimName = victim?.PlayerName ?? "player";
    var oldHealth = attackerPawn.Health;
    attackerPawn.Health = 100;
    attackerPawn.ArmorValue = 100;
    var healAmount = 100 - oldHealth;
    if (healAmount < 0) healAmount = 0;

    if (healAmount > 0)
    {
      attacker.PrintToChat($" \x04+\x0C{healAmount}HP\x04 for killing \x0B{victimName}");
    }

    var reward = Config.KillStreak ?? new KillStreakRewardConfig();
    var killsRequired = Math.Max(1, reward.KillsRequired);
    var isRewardKill = newStreak >= killsRequired && newStreak % killsRequired == 0;
    var rewarded = false;

    if (isRewardKill && reward.Enabled && _grenadePool.Count > 0)
    {
      var grenade = _grenadePool[Random.Shared.Next(_grenadePool.Count)];
      var minGrenades = Math.Clamp(reward.MinGrenades, 1, MaxGrenadesPerReward);
      var maxGrenades = Math.Clamp(reward.MaxGrenades, minGrenades, MaxGrenadesPerReward);
      var count = Random.Shared.Next(minGrenades, maxGrenades + 1);

      var totalGrenades = GiveGrenades(attacker, attackerPawn, grenade.Item, grenade.AmmoIndex, count);
      if (totalGrenades > 0)
      {
        rewarded = true;
        attacker.PrintToChat($" \x0B{newStreak} \x10Kill Streak! \x04You got \x0B{totalGrenades}x {grenade.Label}");

        if (!string.IsNullOrWhiteSpace(reward.Sound))
        {
          attacker.ExecuteClientCommand($"play {reward.Sound.Trim()}");
        }
      }
    }

    if (newStreak >= killsRequired && !rewarded)
    {
      attacker.PrintToChat($" \x0B{newStreak} \x10Kill Streak!");
    }

    return HookResult.Continue;
  }

  private static int GiveGrenades(CCSPlayerController player, CCSPlayerPawn pawn, string item, int ammoIndex, int count)
  {
    var weaponServices = pawn.WeaponServices;
    if (weaponServices == null)
    {
      return -1;
    }

    var ammo = weaponServices.Ammo;
    if (ammoIndex < 0 || ammoIndex >= ammo.Length)
    {
      return -1;
    }

    if (ammo[ammoIndex] == 0 && player.GiveNamedItem(item) == IntPtr.Zero)
    {
      return -1;
    }

    ammo[ammoIndex] = (ushort)Math.Max(ammo[ammoIndex], count);

    Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pWeaponServices");
    return ammo[ammoIndex];
  }

  private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
  {
    var player = @event.Userid;
    if (player == null)
    {
      return HookResult.Continue;
    }

    ApplyCustomSpawn(player);

    if (player.IsBot)
    {
      return HookResult.Continue;
    }

    var pawn = player.PlayerPawn.Value;
    if (pawn == null)
    {
      return HookResult.Continue;
    }

    _killStreaks[pawn.Handle] = 0;
    return HookResult.Continue;
  }
}
