using System.Drawing;
using Skua.Core.Models.Items;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Players;
using Skua.Core.Models.Skills;
using SkuaSim.Core.Runtime;
using SkuaSim.Core.World;

namespace SkuaSim.Core.Api;

public sealed class PlayerApi : ApiBase
{
    private readonly SkillInfo[] _skills;

    public PlayerApi(SimContext ctx) : base(ctx)
    {
        _skills = Enumerable.Range(0, 6).Select(i => new SkillInfo
        {
            ID = i,
            Name = i == 0 ? "Auto Attack" : $"Skill {i}",
            Auto = i == 0,
            IsOk = true,
            Cooldown = i == 0 ? 2000 : 6000,
            Range = 301,
            Type = "aa",
            Description = "",
            Icon = "",
            _Anim = "Attack1"
        }).ToArray();
    }

    public string Guild => World.Guild;
    public int ID => 1;
    public int XP => World.XP;
    public int RequiredXP => World.RequiredXP;
    public int AccessLevel { get => World.AccessLevel; set => World.AccessLevel = value; }
    public string Cell => World.Cell;
    public string Pad => World.Pad;
    public string ServerIP => World.LoggedIn ? "twig.aq.com" : "";
    public int State => World.State;
    public bool InCombat => World.InCombat;
    public bool Alive => World.Alive;
    public bool LoggedIn => World.LoggedIn;
    public bool Playing => World.LoggedIn && World.MapLoaded && World.Alive;
    public string Username => World.Username;
    public string Password => "";
    public bool Kicked => World.Kicked;
    public bool IsMember => World.IsMember;
    public int Health => World.Health;
    public int MaxHealth => World.MaxHealth;
    public int Mana => 100;
    public int MaxMana => 100;
    public int Level => World.Level;
    public int Gold => (int)Math.Min(int.MaxValue, World.Gold);
    public int Coins => World.Coins;
    public InventoryItem? CurrentClass => World.CurrentClass;
    public int CurrentClassRank => World.ClassRank;
    public Monster? Target => World.Target?.ToModel();
    public bool HasTarget => World.Target is { Alive: true };
    public bool Loaded => World.LoggedIn && World.MapLoaded;
    public SkillInfo[] Skills => World.Skills;
    public PlayerStats Stats => World.Stats;
    public bool AFK => false;
    public int X => World.PositionX;
    public int Y => World.PositionY;
    public Point Position => new(World.PositionX, World.PositionY);
    public int Scale { get; set; } = 3;
    public int WalkSpeed { get; set; } = 8;

    public void Goto(string name) => Ctx.Trace.Info($"Goto('{name}'): other players are not simulated yet (multi-client is a later phase).");

    public void SetSpawnPoint() => World.SetSpawnPoint(null, null);

    public void SetSpawnPoint(string cell, string pad) => World.SetSpawnPoint(cell, pad);

    public void WalkTo(int x, int y, int speed)
    {
        World.PositionX = x;
        World.PositionY = y;
    }

    public void Rest(bool full) => World.Rest();
}

public sealed class MapApi : ApiBase
{
    public MapApi(SimContext ctx) : base(ctx) { }

    public string LastMap { get => World.LastMap; set => World.LastMap = value; }
    public bool Loading => World.LoggedIn && !World.MapLoaded;
    public string FilePath { get; set; } = "";
    public string FileName => World.Map?.FileName ?? "";
    public string Name => World.MapName;
    public string FullName => $"{World.MapName}-{World.RoomNumber}";
    public bool Loaded => World.MapLoaded;
    public int RoomID => World.RoomNumber;
    public int RoomNumber => World.RoomNumber;
    public int PlayerCount => World.LoggedIn ? 1 : 0;
    public List<string> Cells => World.Map?.Cells.ToList() ?? new List<string>();
    public List<string> PlayerNames => World.LoggedIn ? new List<string> { World.Username } : new List<string>();
    public List<PlayerInfo> Players => World.LoggedIn ? new List<PlayerInfo> { World.SelfInfo() } : new List<PlayerInfo>();
    public List<PlayerInfo> CellPlayers => Players;

    public void Reload() => World.Join($"{World.MapName}-{World.RoomNumber}", World.Cell, World.Pad, ignoreCheck: true);

    public void Join(string map, string cell, string pad, bool ignoreCheck, bool autoCorrect) => World.Join(map, cell, pad, ignoreCheck);

    public void JoinIgnore(string map) => World.Join(map, "Enter", "Spawn", ignoreCheck: true);

    public void JoinPacket(string map, string cell, string pad) => World.Join(map, cell, pad, ignoreCheck: true);

    public void Jump(string cell, string pad, bool autoCorrect, bool clientOnly) => World.Jump(cell, pad, autoCorrect);

    public void GetMapItem(int id) => World.GetMapItem(id, 1);

    public void GetMapItem(int id, int quantity) => World.GetMapItem(id, quantity);

    public bool PlayerExists(string name) => World.LoggedIn && SimWorld.Eq(name, World.Username);

    public bool TryGetPlayer(string username, out PlayerInfo? player)
    {
        player = PlayerExists(username) ? World.SelfInfo() : null;
        return player != null;
    }

    public PlayerInfo? GetPlayer(string username) => PlayerExists(username) ? World.SelfInfo() : null;

    public List<MapItem> FindMapItems(bool forceRefresh) =>
        World.Map?.Data?.MapItems.Keys.Select(id => new MapItem(id, 0, FilePath, World.MapName)).ToList() ?? new List<MapItem>();

    public void ClearMapItemsCache(string specificMap) { }
}

public sealed class MonstersApi : ApiBase
{
    public MonstersApi(SimContext ctx) : base(ctx) { }

    public List<Monster> CurrentMonsters => World.MonstersInCell().Select(m => m.ToModel()).ToList();
    public List<Monster> MapMonsters => World.AllMonsters().Select(m => m.ToModel()).ToList();
    public List<Monster> CurrentAvailableMonsters => World.MonstersInCell().Where(m => m.Alive).Select(m => m.ToModel()).ToList();

    public bool Exists(string name) => World.FindMonster(name) != null;

    public bool Exists(int id) => World.MonstersInCell().Any(m => m.Alive && (m.MonID == id || m.MapID == id));

    public Dictionary<string, List<Monster>> GetCellMonsters() =>
        World.AllMonsters().GroupBy(m => m.Cell, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(m => m.ToModel()).ToList(), StringComparer.OrdinalIgnoreCase);

    public List<string> GetLivingMonsterCells(string name) => Cells(m => m.Alive && SimWorld.NameMatches(m.Name, name));
    public List<string> GetLivingMonsterCells(int id) => Cells(m => m.Alive && m.MonID == id);
    public List<string> GetLivingMonsterDataLeafCells(string name) => GetLivingMonsterCells(name);
    public List<string> GetLivingMonsterDataLeafCells(int id) => GetLivingMonsterCells(id);
    public List<string> GetMonsterCells(string name) => Cells(m => SimWorld.NameMatches(m.Name, name));
    public List<string> GetMonsterCells(int id) => Cells(m => m.MonID == id);

    public List<Monster> GetMonstersByCell(string cell) => World.MonstersInCell(cell).Select(m => m.ToModel()).ToList();

    public bool TryGetMonster(string name, out Monster? monster)
    {
        var all = World.AllMonsters().Where(m => SimWorld.NameMatches(m.Name, name)).ToList();
        var pick = all.FirstOrDefault(m => m.Alive && SimWorld.Eq(m.Cell, World.Cell)) ?? all.FirstOrDefault(m => m.Alive) ?? all.FirstOrDefault();
        monster = pick?.ToModel();
        return monster != null;
    }

    public bool TryGetMonster(int id, out Monster? monster)
    {
        var all = World.AllMonsters().Where(m => m.MonID == id || m.MapID == id).ToList();
        var pick = all.FirstOrDefault(m => m.Alive && SimWorld.Eq(m.Cell, World.Cell)) ?? all.FirstOrDefault(m => m.Alive) ?? all.FirstOrDefault();
        monster = pick?.ToModel();
        return monster != null;
    }

    private List<string> Cells(Func<SimMonster, bool> predicate) =>
        World.AllMonsters().Where(predicate).Select(m => m.Cell).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
