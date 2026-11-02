using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace CS2ArmsRaceUtility;

public sealed class SpawnPointData
{
  [JsonPropertyName("x")] public float X { get; set; }
  [JsonPropertyName("y")] public float Y { get; set; }
  [JsonPropertyName("z")] public float Z { get; set; }
  [JsonPropertyName("pitch")] public float Pitch { get; set; }
  [JsonPropertyName("yaw")] public float Yaw { get; set; }
  [JsonPropertyName("roll")] public float Roll { get; set; }
}

public sealed class MapSpawnFile
{
  [JsonPropertyName("t")] public List<SpawnPointData> T { get; set; } = new();
  [JsonPropertyName("ct")] public List<SpawnPointData> CT { get; set; } = new();
}

public partial class ArmsRaceUtilityPlugin
{
  private const int TeamTerrorist = (int)CsTeam.Terrorist;
  private const int TeamCounterTerrorist = (int)CsTeam.CounterTerrorist;

  private const float BeamHeight = 128f;
  private const float LabelHeight = BeamHeight + 16f;

  private static readonly Color TerroristColor = Color.FromArgb(255, 238, 158, 47);
  private static readonly Color CounterTerroristColor = Color.FromArgb(255, 115, 201, 227);

  // map spawns use a separate palette so they are easy to tell apart from custom spawns
  private static readonly Color MapTerroristColor = Color.FromArgb(255, 220, 80, 220);
  private static readonly Color MapCounterTerroristColor = Color.FromArgb(255, 100, 220, 140);
  private static readonly Color MapSharedColor = Color.FromArgb(255, 245, 245, 245);

  private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

  private MapSpawnFile _mapSpawns = new();
  private string _currentMap = string.Empty;

  // maps own spawns
  private readonly List<SpawnPointData> _mapSpawnsT = new();
  private readonly List<SpawnPointData> _mapSpawnsCT = new();
  private bool _mapSpawnPointsLoaded;
  private bool _visualsWarmed;

  private int _armsRaceSpawnCountT;
  private int _armsRaceSpawnCountCT;
  private int _deathmatchSpawnCount;
  private int _competitiveSpawnCountT;
  private int _competitiveSpawnCountCT;

  private const int PoolCustom = 0;
  private const int PoolMap = 1;

  private readonly Dictionary<(int Team, int Pool), List<int>> _spawnBags = new();
  private readonly List<CBeam> _beams = new();
  private readonly List<CPointWorldText> _labels = new();
  private bool _visualize;
  private bool _visualizeMapSpawns;

  private bool _workScheduled;
  private bool _saveDirty;
  private bool _needsFullRebuild;
  private (int Team, int Index)? _pendingMarkerAdd;

  private readonly object _saveLock = new();
  private string? _pendingSavePath;
  private string? _pendingSaveJson;
  private bool _saveWorkerRunning;

  private string SpawnDirectory => Path.Combine(ModuleDirectory, "spawns");

  #region Commands

  [ConsoleCommand("css_arspawn", "Arms Race custom spawn editor")]
  [RequiresPermissions("@css/root")]
  [CommandHelper(minArgs: 0, usage: "add <t|ct> | del <t|ct> [index] | clear <t|ct|all> | list | tp <t|ct> <index> | reload | show <custom|map|all|off> | map | (no args shows help)")]
  public void OnArSpawnCommand(CCSPlayerController? player, CommandInfo command)
  {
    var started = Stopwatch.GetTimestamp();
    var subCommand = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : string.Empty;

    switch (subCommand)
    {
      case "":
        ShowSpawnEditorHelp(player);
        break;
      case "add":
        AddSpawn(player, command);
        break;
      case "del":
      case "delete":
      case "remove":
        DeleteSpawn(player, command);
        break;
      case "clear":
        ClearSpawns(player, command);
        break;
      case "list":
        ListSpawns(player);
        break;
      case "tp":
      case "teleport":
        TeleportToSpawn(player, command);
        break;
      case "reload":
        ReloadSpawns(player);
        break;
      case "map":
        ToggleMapSpawnVisualization(player);
        break;
      case "show":
        ShowSpawnVisualization(player, command);
        break;
      default:
        Reply(player, $"Unknown subcommand '{subCommand}'. Usage: !arspawn add <t|ct> | del <t|ct> [index] | clear <t|ct|all> | list | tp <t|ct> <index> | reload | show <custom|map|all|off> | map");
        break;
    }

    var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    if (elapsedMs >= 15)
    {
      Logger.LogWarning("css_arspawn '{SubCommand}' blocked the main thread for {Elapsed:F0} ms", subCommand, elapsedMs);
    }
  }

  private void ShowSpawnEditorHelp(CCSPlayerController? player)
  {
    Reply(player, $"{ModuleName} v{ModuleVersion} by {ModuleAuthor}");
    Reply(player, "Options:");
    Reply(player, "  !arspawn show <custom|map|all|off> - choose which spawn markers to display");
    Reply(player, "  !arspawn map - toggle map spawn markers");
    Reply(player, "  !arspawn add <t|ct> - add a custom spawn at your position");
    Reply(player, "  !arspawn del <t|ct> [index] - delete a custom spawn (nearest if omitted)");
    Reply(player, "  !arspawn clear <t|ct|all> - remove custom spawns");
    Reply(player, "  !arspawn list - list custom and map spawns");
    Reply(player, "  !arspawn tp <t|ct> <index> - teleport to a custom spawn");
    Reply(player, "  !arspawn reload - reload custom spawns from disk");
  }

  private void AddSpawn(CCSPlayerController? player, CommandInfo command)
  {
    if (player == null || !player.IsValid)
    {
      Reply(player, "This subcommand must be run by a player.");
      return;
    }

    if (command.ArgCount < 3 || !TryParseTeam(command.GetArg(2), out var team))
    {
      Reply(player, "Usage: !arspawn add <t|ct>");
      return;
    }

    var pawn = player.PlayerPawn.Value;
    if (pawn == null || !pawn.IsValid)
    {
      Reply(player, "You need to be alive to add a spawn point.");
      return;
    }

    var origin = pawn.AbsOrigin;
    if (origin == null)
    {
      Reply(player, "Could not read your current position.");
      return;
    }

    // only store yaw as nothing else is really needed
    var angles = pawn.EyeAngles;
    var list = GetSpawnList(team);
    list.Add(new SpawnPointData
    {
      X = origin.X,
      Y = origin.Y,
      Z = origin.Z,
      Pitch = 0f,
      Yaw = angles.Y,
      Roll = 0f
    });

    OnSpawnsChanged(markerAdded: true, team, list.Count - 1);
    Reply(player, $"Added {TeamLabel(team)} spawn #{list.Count} at {FormatPosition(origin)} ({list.Count} total).");
  }

  private void DeleteSpawn(CCSPlayerController? player, CommandInfo command)
  {
    if (command.ArgCount < 3 || !TryParseTeam(command.GetArg(2), out var team))
    {
      Reply(player, "Usage: !arspawn del <t|ct> [index] (index is 1-based, omit to remove the nearest spawn)");
      return;
    }

    var list = GetSpawnList(team);
    if (list.Count == 0)
    {
      Reply(player, $"There are no custom {TeamLabel(team)} spawns on {_currentMap}.");
      return;
    }

    int index;
    if (command.ArgCount >= 4 && int.TryParse(command.GetArg(3), out var parsed))
    {
      index = parsed - 1;
    }
    else
    {
      if (player == null || !player.IsValid)
      {
        Reply(player, "Run this in game or provide an index.");
        return;
      }

      var origin = player.PlayerPawn.Value?.AbsOrigin;
      if (origin == null)
      {
        Reply(player, "Could not read your current position.");
        return;
      }

      index = FindNearestSpawn(list, origin);
    }

    if (index < 0 || index >= list.Count)
    {
      Reply(player, $"Invalid index. Valid range is 1-{list.Count}.");
      return;
    }

    list.RemoveAt(index);
    OnSpawnsChanged();
    Reply(player, $"Removed {TeamLabel(team)} spawn #{index + 1}. {list.Count} left.");
  }

  private void ClearSpawns(CCSPlayerController? player, CommandInfo command)
  {
    var argument = command.ArgCount > 2 ? command.GetArg(2).ToLowerInvariant() : string.Empty;
    if (argument != "all" && !TryParseTeam(argument, out _))
    {
      Reply(player, "Usage: !arspawn clear <t|ct|all>");
      return;
    }

    if (argument == "all")
    {
      var removed = _mapSpawns.T.Count + _mapSpawns.CT.Count;
      _mapSpawns.T.Clear();
      _mapSpawns.CT.Clear();
      OnSpawnsChanged();
      Reply(player, $"Removed all {removed} custom spawns on {_currentMap}.");
      return;
    }

    TryParseTeam(argument, out var team);
    var list = GetSpawnList(team);
    var count = list.Count;
    list.Clear();
    OnSpawnsChanged();
    Reply(player, $"Removed {count} custom {TeamLabel(team)} spawns on {_currentMap}.");
  }

  private void ListSpawns(CCSPlayerController? player)
  {
    if (!_mapSpawnPointsLoaded)
    {
      RefreshMapSpawnPoints();
    }

    Reply(player, $"{_currentMap}: {_mapSpawns.T.Count} T / {_mapSpawns.CT.Count} CT custom spawns");
    Reply(player,
      $"Map spawns: {_mapSpawnsT.Count} T / {_mapSpawnsCT.Count} CT in use " +
      $"(arms race {_armsRaceSpawnCountT}/{_armsRaceSpawnCountCT}, deathmatch {_deathmatchSpawnCount}, " +
      $"competitive {_competitiveSpawnCountT}/{_competitiveSpawnCountCT})");
    Reply(player,
      $"use_map_spawns={(Config.UseMapSpawns ? "on" : "off")}, custom chance {Config.CustomSpawnChance:P0}, " +
      $"randomspawn mixing={(Config.RespectRandomSpawn && IsRandomSpawnEnabled() ? "on" : "off")}");

    for (var i = 0; i < _mapSpawns.T.Count; i++)
    {
      Reply(player, $"T #{i + 1} {FormatSpawn(_mapSpawns.T[i])}");
    }

    for (var i = 0; i < _mapSpawns.CT.Count; i++)
    {
      Reply(player, $"CT #{i + 1} {FormatSpawn(_mapSpawns.CT[i])}");
    }
  }

  private void TeleportToSpawn(CCSPlayerController? player, CommandInfo command)
  {
    if (player == null || !player.IsValid)
    {
      Reply(player, "This subcommand must be run by a player.");
      return;
    }

    if (command.ArgCount < 4 || !TryParseTeam(command.GetArg(2), out var team) || !int.TryParse(command.GetArg(3), out var parsed))
    {
      Reply(player, "Usage: !arspawn tp <t|ct> <index>");
      return;
    }

    var list = GetSpawnList(team);
    var index = parsed - 1;
    if (index < 0 || index >= list.Count)
    {
      Reply(player, list.Count == 0
        ? $"There are no custom {TeamLabel(team)} spawns on {_currentMap}."
        : $"Invalid index. Valid range is 1-{list.Count}.");
      return;
    }

    var spawn = list[index];
    var pawn = player.PlayerPawn.Value;
    if (pawn == null || !pawn.IsValid)
    {
      Reply(player, "You need to be alive to teleport.");
      return;
    }

    TeleportToSpawnPoint(pawn, spawn);

    Reply(player, $"Teleported to {TeamLabel(team)} spawn #{index + 1}.");
  }

  private void ReloadSpawns(CCSPlayerController? player)
  {
    Server.NextFrame(() =>
    {
      _mapSpawns = LoadSpawnFile(_currentMap);
      RefreshMapSpawnPoints();
      _spawnBags.Clear();
      _needsFullRebuild = true;
      ScheduleSpawnWork();

      Reply(player,
        $"Reloaded {_currentMap}: {_mapSpawns.T.Count} T / {_mapSpawns.CT.Count} CT custom, " +
        $"{_mapSpawnsT.Count} T / {_mapSpawnsCT.Count} CT map " +
        $"(arms race {_armsRaceSpawnCountT}/{_armsRaceSpawnCountCT}, deathmatch {_deathmatchSpawnCount}, " +
        $"competitive {_competitiveSpawnCountT}/{_competitiveSpawnCountCT}).");
    });
  }

  #endregion

  #region Spawn application

  internal void ApplyCustomSpawn(CCSPlayerController player)
  {
    if (player == null || !player.IsValid)
    {
      return;
    }

    if (player.TeamNum != TeamTerrorist && player.TeamNum != TeamCounterTerrorist)
    {
      return;
    }

    var spawn = PickSpawn(player.TeamNum);
    if (spawn == null)
    {
      return;
    }

    var teleported = TryTeleportTo(player, spawn);
    // correct it on the next frame if the pawn was not ready yet
    Server.NextFrame(() =>
    {
      if (!player.IsValid)
      {
        return;
      }

      if (!teleported)
      {
        TryTeleportTo(player, spawn);
        return;
      }

      var pawn = player.PlayerPawn.Value;
      var origin = pawn?.AbsOrigin;
      if (origin == null)
      {
        return;
      }

      var dx = origin.X - spawn.X;
      var dy = origin.Y - spawn.Y;
      var dz = origin.Z - spawn.Z;
      if (dx * dx + dy * dy + dz * dz > 100f * 100f)
      {
        TryTeleportTo(player, spawn);
      }
    });
  }

  private bool TryTeleportTo(CCSPlayerController player, SpawnPointData spawn)
  {
    if (!player.IsValid)
    {
      return false;
    }

    var pawn = player.PlayerPawn.Value;
    if (pawn == null || !pawn.IsValid)
    {
      return false;
    }

    TeleportToSpawnPoint(pawn, spawn);
    return true;
  }

  private void TeleportToSpawnPoint(CCSPlayerPawn pawn, SpawnPointData spawn)
  {
    var zOffset = Math.Clamp(Config.SpawnZOffset, 0f, 64f);
    pawn.Teleport(
      new Vector(spawn.X, spawn.Y, spawn.Z + zOffset),
      new QAngle(0f, spawn.Yaw, 0f),
      new Vector(0f, 0f, 0f));
  }

  private SpawnPointData? PickSpawn(byte teamNum)
  {
    var team = (int)teamNum;
    var anyTeam = Config.RespectRandomSpawn && IsRandomSpawnEnabled();

    if (!_mapSpawnPointsLoaded)
    {
      RefreshMapSpawnPoints();
    }

    var custom = anyTeam
      ? _mapSpawns.T.Concat(_mapSpawns.CT).ToList()
      : new List<SpawnPointData>(GetSpawnList(team));

    var map = new List<SpawnPointData>();
    if (Config.UseMapSpawns)
    {
      if (anyTeam)
      {
        map.AddRange(_mapSpawnsT);
        map.AddRange(_mapSpawnsCT);
      }
      else
      {
        map.AddRange(team == TeamCounterTerrorist ? _mapSpawnsCT : _mapSpawnsT);
      }
    }

    if (custom.Count == 0 && map.Count == 0)
    {
      return null;
    }

    if (custom.Count == 0)
    {
      return PickFromPool(team, PoolMap, map);
    }

    if (map.Count == 0)
    {
      return PickFromPool(team, PoolCustom, custom);
    }

    var chance = Math.Clamp(Config.CustomSpawnChance, 0f, 1f);
    return Random.Shared.NextDouble() < chance
      ? PickFromPool(team, PoolCustom, custom)
      : PickFromPool(team, PoolMap, map);
  }

  private SpawnPointData? PickFromPool(int team, int pool, List<SpawnPointData> candidates)
  {
    if (candidates.Count == 0)
    {
      return null;
    }

    var key = (team, pool);
    if (!_spawnBags.TryGetValue(key, out var bag) || bag.Count == 0 || bag.Any(i => i >= candidates.Count))
    {
      bag = Enumerable.Range(0, candidates.Count).ToList();
      for (var i = bag.Count - 1; i > 0; i--)
      {
        var j = Random.Shared.Next(i + 1);
        (bag[i], bag[j]) = (bag[j], bag[i]);
      }

      _spawnBags[key] = bag;
    }

    var index = bag[^1];
    bag.RemoveAt(bag.Count - 1);
    return candidates[index];
  }

  private static bool IsRandomSpawnEnabled()
  {
    var conVar = ConVar.Find("mp_randomspawn");
    return conVar != null && conVar.GetPrimitiveValue<int>() > 0;
  }

  private void RefreshMapSpawnPoints()
  {
    _mapSpawnsT.Clear();
    _mapSpawnsCT.Clear();
    _armsRaceSpawnCountT = 0;
    _armsRaceSpawnCountCT = 0;
    _deathmatchSpawnCount = 0;
    _competitiveSpawnCountT = 0;
    _competitiveSpawnCountCT = 0;
    _mapSpawnPointsLoaded = true;

    if (!Config.UseMapSpawns)
    {
      ScheduleMapSpawnVisualizationRefresh();
      return;
    }

    var competitiveT = new List<SpawnPointData>();
    var competitiveCT = new List<SpawnPointData>();
    var armsRaceT = new List<SpawnPointData>();
    var armsRaceCT = new List<SpawnPointData>();
    var deathmatch = new List<SpawnPointData>();

    foreach (var entity in Utilities.GetAllEntities())
    {
      if (!entity.IsValid)
      {
        continue;
      }

      var designerName = entity.DesignerName;
      var isCompetitiveT = designerName == "info_player_terrorist";
      var isCompetitiveCT = designerName == "info_player_counterterrorist";
      var isArmsRaceT = designerName == "info_armsrace_terrorist";
      var isArmsRaceCT = designerName == "info_armsrace_counterterrorist";
      var isDeathmatch = designerName == "info_deathmatch_spawn";
      if (!isCompetitiveT && !isCompetitiveCT && !isArmsRaceT && !isArmsRaceCT && !isDeathmatch)
      {
        continue;
      }

      var spawn = entity.As<SpawnPoint>();

      if ((isCompetitiveT || isCompetitiveCT) && (!spawn.IsValid || !spawn.Enabled))
      {
        continue;
      }

      var origin = spawn.AbsOrigin;
      if (origin == null)
      {
        continue;
      }

      var angles = spawn.AbsRotation;
      var data = new SpawnPointData
      {
        X = origin.X,
        Y = origin.Y,
        Z = origin.Z,
        Pitch = angles?.X ?? 0f,
        Yaw = angles?.Y ?? 0f,
        Roll = angles?.Z ?? 0f
      };

      if (isCompetitiveCT)
      {
        competitiveCT.Add(data);
      }
      else if (isCompetitiveT)
      {
        competitiveT.Add(data);
      }
      else if (isArmsRaceCT)
      {
        armsRaceCT.Add(data);
      }
      else if (isArmsRaceT)
      {
        armsRaceT.Add(data);
      }
      else
      {
        deathmatch.Add(data);
      }
    }

    _armsRaceSpawnCountT = armsRaceT.Count;
    _armsRaceSpawnCountCT = armsRaceCT.Count;
    _deathmatchSpawnCount = deathmatch.Count;
    _competitiveSpawnCountT = competitiveT.Count;
    _competitiveSpawnCountCT = competitiveCT.Count;

    // use info_armsrace_* and ignore the competitive spawns
    _mapSpawnsT.AddRange(armsRaceT.Count > 0 ? armsRaceT
      : deathmatch.Count > 0 ? deathmatch
      : competitiveT);
    _mapSpawnsCT.AddRange(armsRaceCT.Count > 0 ? armsRaceCT
      : deathmatch.Count > 0 ? deathmatch
      : competitiveCT);

    ScheduleMapSpawnVisualizationRefresh();
  }

  private void ScheduleMapSpawnVisualizationRefresh()
  {
    if (!_visualizeMapSpawns)
    {
      return;
    }

    _needsFullRebuild = true;
    ScheduleSpawnWork();
  }

  private List<SpawnPointData> GetSpawnList(int team)
  {
    return team == TeamCounterTerrorist ? _mapSpawns.CT : _mapSpawns.T;
  }

  private static int FindNearestSpawn(List<SpawnPointData> spawns, Vector origin)
  {
    var nearest = 0;
    var nearestDistance = float.MaxValue;

    for (var i = 0; i < spawns.Count; i++)
    {
      var dx = spawns[i].X - origin.X;
      var dy = spawns[i].Y - origin.Y;
      var dz = spawns[i].Z - origin.Z;
      var distance = dx * dx + dy * dy + dz * dz;

      if (distance < nearestDistance)
      {
        nearestDistance = distance;
        nearest = i;
      }
    }

    return nearest;
  }

  #endregion

  #region Visualization

  private void ToggleMapSpawnVisualization(CCSPlayerController? player)
  {
    if (!_mapSpawnPointsLoaded)
    {
      RefreshMapSpawnPoints();
    }

    _visualizeMapSpawns = !_visualizeMapSpawns;
    _needsFullRebuild = true;
    ScheduleSpawnWork();

    if (_visualizeMapSpawns)
    {
      Reply(player, $"Map spawn visualization enabled ({_mapSpawnsT.Count} T / {_mapSpawnsCT.Count} CT on {_currentMap}).");
      return;
    }

    Reply(player, "Map spawn visualization disabled.");
  }

  private void ShowSpawnVisualization(CCSPlayerController? player, CommandInfo command)
  {
    if (command.ArgCount < 3)
    {
      Reply(player,
        $"Spawn visualization: custom={(_visualize ? "on" : "off")}, map={(_visualizeMapSpawns ? "on" : "off")}. " +
        "Usage: !arspawn show <custom|map|all|off>");
      return;
    }

    switch (command.GetArg(2).ToLowerInvariant())
    {
      case "custom":
      case "spawns":
        SetSpawnVisualization(player, custom: true, map: false);
        break;
      case "map":
      case "maps":
      case "official":
        SetSpawnVisualization(player, custom: false, map: true);
        break;
      case "all":
      case "both":
        SetSpawnVisualization(player, custom: true, map: true);
        break;
      case "off":
      case "none":
      case "hide":
        SetSpawnVisualization(player, custom: false, map: false);
        break;
      default:
        Reply(player, "Usage: !arspawn show <custom|map|all|off>");
        break;
    }
  }

  private void SetSpawnVisualization(CCSPlayerController? player, bool custom, bool map)
  {
    if (map && !_mapSpawnPointsLoaded)
    {
      RefreshMapSpawnPoints();
    }

    _visualize = custom;
    _visualizeMapSpawns = map;
    _needsFullRebuild = true;
    ScheduleSpawnWork();

    Reply(player,
      $"Spawn visualization: custom={(custom ? "on" : "off")} ({_mapSpawns.T.Count} T / {_mapSpawns.CT.Count} CT), " +
      $"map={(map ? "on" : "off")} ({_mapSpawnsT.Count} T / {_mapSpawnsCT.Count} CT).");
  }

  private void RebuildSpawnVisualization()
  {
    ClearSpawnVisualization();

    if (_visualize)
    {
      CreateMarkers(_mapSpawns.T, TeamTerrorist, isMapSpawn: false);
      CreateMarkers(_mapSpawns.CT, TeamCounterTerrorist, isMapSpawn: false);
    }

    if (_visualizeMapSpawns)
    {
      // deathmatch maps use one shared spawn pool for both teams, so avoid drawing each
      // marker twice at the same position
      if (SpawnListsMatch(_mapSpawnsT, _mapSpawnsCT))
      {
        CreateMarkers(_mapSpawnsT, TeamTerrorist, isMapSpawn: true, sharedMapSpawn: true);
      }
      else
      {
        CreateMarkers(_mapSpawnsT, TeamTerrorist, isMapSpawn: true);
        CreateMarkers(_mapSpawnsCT, TeamCounterTerrorist, isMapSpawn: true);
      }
    }
  }

  private static bool SpawnListsMatch(List<SpawnPointData> left, List<SpawnPointData> right)
  {
    if (left.Count == 0 || left.Count != right.Count)
    {
      return false;
    }

    for (var i = 0; i < left.Count; i++)
    {
      if (Math.Abs(left[i].X - right[i].X) > 0.1f ||
          Math.Abs(left[i].Y - right[i].Y) > 0.1f ||
          Math.Abs(left[i].Z - right[i].Z) > 0.1f)
      {
        return false;
      }
    }

    return true;
  }

  private void CreateMarkers(List<SpawnPointData> spawns, int team, bool isMapSpawn, bool sharedMapSpawn = false)
  {
    for (var i = 0; i < spawns.Count; i++)
    {
      CreateMarker(spawns[i], team, i + 1, isMapSpawn, sharedMapSpawn);
    }
  }

  private void CreateMarker(SpawnPointData spawn, int team, int displayIndex, bool isMapSpawn = false, bool sharedMapSpawn = false)
  {
    var color = sharedMapSpawn
      ? MapSharedColor
      : isMapSpawn
        ? team == TeamTerrorist ? MapTerroristColor : MapCounterTerroristColor
        : team == TeamTerrorist ? TerroristColor : CounterTerroristColor;
    var label = sharedMapSpawn
      ? "MAP"
      : isMapSpawn ? $"MAP {TeamLabel(team)}" : TeamLabel(team);

    var beam = Utilities.CreateEntityByName<CBeam>("beam");
    if (beam != null && beam.IsValid)
    {
      beam.Render = color;
      beam.Width = 2.0f;
      beam.EndWidth = 2.0f;
      beam.Amplitude = 0f;
      beam.Speed = 0f;
      beam.Teleport(new Vector(spawn.X, spawn.Y, spawn.Z), new QAngle(0f, 0f, 0f), new Vector(0f, 0f, 0f));
      beam.EndPos.X = spawn.X;
      beam.EndPos.Y = spawn.Y;
      beam.EndPos.Z = spawn.Z + BeamHeight;
      beam.DispatchSpawn();
      _beams.Add(beam);
    }

    var text = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
    if (text != null && text.IsValid)
    {
      text.MessageText = $"{label} #{displayIndex}";
      text.Enabled = true;
      text.FontSize = 64f;
      text.Color = color;
      text.WorldUnitsPerPx = 0.25f;
      text.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
      text.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;
      text.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
      text.Teleport(new Vector(spawn.X, spawn.Y, spawn.Z + LabelHeight), new QAngle(0f, 0f, 0f), new Vector(0f, 0f, 0f));
      text.DispatchSpawn();
      _labels.Add(text);
    }
  }

  internal void ClearSpawnVisualization()
  {
    foreach (var beam in _beams)
    {
      if (beam.IsValid)
      {
        beam.Remove();
      }
    }

    foreach (var label in _labels)
    {
      if (label.IsValid)
      {
        label.Remove();
      }
    }

    _beams.Clear();
    _labels.Clear();
  }

  #endregion

  #region Persistence

  private void OnSpawnsChanged(bool markerAdded = false, int team = 0, int index = -1)
  {
    _spawnBags.Clear();
    _saveDirty = true;

    if (markerAdded)
    {
      if (_pendingMarkerAdd != null)
      {
        _needsFullRebuild = true;
      }
      else
      {
        _pendingMarkerAdd = (team, index);
      }
    }
    else
    {
      _needsFullRebuild = true;
    }

    ScheduleSpawnWork();
  }

  private void ScheduleSpawnWork()
  {
    if (_workScheduled)
    {
      return;
    }

    _workScheduled = true;
    Server.NextFrame(RunSpawnWork);
  }

  private void RunSpawnWork()
  {
    _workScheduled = false;

    if (_saveDirty)
    {
      _saveDirty = false;
      var saveStarted = Stopwatch.GetTimestamp();
      SaveSpawnFile();
      LogIfSlow("save", saveStarted);
    }

    var visualizationStarted = Stopwatch.GetTimestamp();
    if (!_visualize && !_visualizeMapSpawns)
    {
      ClearSpawnVisualization();
    }
    else if (_needsFullRebuild || _pendingMarkerAdd == null || !_visualize)
    {
      RebuildSpawnVisualization();
    }
    else
    {
      var (team, index) = _pendingMarkerAdd.Value;
      var list = GetSpawnList(team);
      if (index >= 0 && index < list.Count)
      {
        CreateMarker(list[index], team, index + 1);
      }
      else
      {
        RebuildSpawnVisualization();
      }
    }

    _needsFullRebuild = false;
    _pendingMarkerAdd = null;
    LogIfSlow("visualization", visualizationStarted);
  }

  private void LogIfSlow(string phase, long startTimestamp)
  {
    var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
    if (elapsedMs >= 15)
    {
      Logger.LogWarning("Spawn editor {Phase} work took {Elapsed:F0} ms", phase, elapsedMs);
    }
  }

  private void LoadSpawnEditor(string mapName, bool mapReady)
  {
    ClearSpawnVisualization();
    _visualize = false;
    _visualizeMapSpawns = false;
    _visualsWarmed = false;
    _needsFullRebuild = false;
    _pendingMarkerAdd = null;
    _spawnBags.Clear();
    _mapSpawnPointsLoaded = false;
    _mapSpawnsT.Clear();
    _mapSpawnsCT.Clear();
    _currentMap = string.IsNullOrWhiteSpace(mapName) ? "unknown" : mapName;
    _mapSpawns = LoadSpawnFile(_currentMap);

    WarmUpSerialization();

    // on a cold start the world does not exist yet, so wait with entity access
    if (mapReady)
    {
      RefreshMapSpawnPoints();
    }
  }

  private MapSpawnFile LoadSpawnFile(string mapName)
  {
    var path = GetSpawnFilePath(mapName);

    try
    {
      if (!File.Exists(path))
      {
        return new MapSpawnFile();
      }

      var json = File.ReadAllText(path);
      var file = JsonSerializer.Deserialize<MapSpawnFile>(json, JsonOptions) ?? new MapSpawnFile();

      file.T ??= new List<SpawnPointData>();
      file.CT ??= new List<SpawnPointData>();
      return file;
    }
    catch (Exception ex)
    {
      Logger.LogError(ex, "Failed to load spawn file {Path}", path);
      return new MapSpawnFile();
    }
  }

  private void SaveSpawnFile()
  {
    if (string.IsNullOrWhiteSpace(_currentMap))
    {
      return;
    }

    string json;
    try
    {
      json = JsonSerializer.Serialize(_mapSpawns, JsonOptions);
    }
    catch (Exception ex)
    {
      Logger.LogError(ex, "Failed to serialize spawns for {Map}", _currentMap);
      return;
    }

    lock (_saveLock)
    {
      _pendingSavePath = GetSpawnFilePath(_currentMap);
      _pendingSaveJson = json;

      if (_saveWorkerRunning)
      {
        return;
      }

      _saveWorkerRunning = true;
    }

    _ = Task.Run(WritePendingSaves);
  }

  private void WritePendingSaves()
  {
    while (true)
    {
      string? path;
      string? json;

      lock (_saveLock)
      {
        if (_pendingSaveJson == null)
        {
          _saveWorkerRunning = false;
          return;
        }

        path = _pendingSavePath;
        json = _pendingSaveJson;
        _pendingSavePath = null;
        _pendingSaveJson = null;
      }

      try
      {
        if (path == null || json == null)
        {
          continue;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
      }
      catch (Exception ex)
      {
        Logger.LogError(ex, "Failed to save spawn file {Path}", path);
      }
    }
  }

  private void WarmUpSerialization()
  {
    // the first System.Text.Json call in the process is expensive
    // so pay it at map start to cache
    try
    {
      _ = JsonSerializer.Serialize(new MapSpawnFile(), JsonOptions);
    }
    catch (Exception ex)
    {
      Logger.LogWarning(ex, "Spawn JSON warm-up failed");
    }
  }

  private void WarmUpVisualization()
  {
    var beam = Utilities.CreateEntityByName<CBeam>("beam");
    if (beam != null && beam.IsValid)
    {
      beam.Render = TerroristColor;
      beam.Width = 1f;
      beam.EndWidth = 1f;
      beam.Amplitude = 0f;
      beam.Speed = 0f;
      beam.Teleport(new Vector(0f, 0f, -16384f), new QAngle(0f, 0f, 0f), new Vector(0f, 0f, 0f));
      beam.EndPos.X = 0f;
      beam.EndPos.Y = 0f;
      beam.EndPos.Z = -16256f;
      beam.DispatchSpawn();
      Server.NextFrame(() =>
      {
        if (beam.IsValid)
        {
          beam.Remove();
        }
      });
    }

    var text = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
    if (text != null && text.IsValid)
    {
      text.MessageText = string.Empty;
      text.Enabled = true;
      text.FontSize = 1f;
      text.Color = TerroristColor;
      text.WorldUnitsPerPx = 0.25f;
      text.Teleport(new Vector(0f, 0f, -16384f), new QAngle(0f, 0f, 0f), new Vector(0f, 0f, 0f));
      text.DispatchSpawn();
      Server.NextFrame(() =>
      {
        if (text.IsValid)
        {
          text.Remove();
        }
      });
    }
  }

  private string GetSpawnFilePath(string mapName)
  {
    var safeName = string.Concat(mapName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
    return Path.Combine(SpawnDirectory, $"{safeName}.json");
  }

  #endregion

  #region Helpers

  private static bool TryParseTeam(string value, out int team)
  {
    switch (value.ToLowerInvariant())
    {
      case "t":
      case "terrorist":
      case "terror":
        team = TeamTerrorist;
        return true;
      case "ct":
      case "counterterrorist":
      case "counter-terrorist":
        team = TeamCounterTerrorist;
        return true;
      default:
        team = TeamTerrorist;
        return false;
    }
  }

  private static string TeamLabel(int team) => team == TeamCounterTerrorist ? "CT" : "T";

  private static string FormatPosition(Vector position) =>
    $"({position.X:F0}, {position.Y:F0}, {position.Z:F0})";

  private static string FormatSpawn(SpawnPointData spawn) =>
    $"({spawn.X:F0}, {spawn.Y:F0}, {spawn.Z:F0}) yaw {spawn.Yaw:F0}";

  private static void Reply(CCSPlayerController? player, string message)
  {
    if (player != null && player.IsValid)
    {
      player.PrintToChat($" \x04[ArmsRace]\x01 {message}");
      return;
    }

    Console.WriteLine($"[ArmsRace] {message}");
  }

  #endregion
}
