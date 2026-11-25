using System.Collections.Immutable;
using System.Dynamic;
using CommunityToolkit.Mvvm.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Auras;
using Skua.Core.Models.Servers;
using SkuaSim.Core.Proxies;
using SkuaSim.Core.Runtime;
using SkuaSim.Core.Scenarios;
using SkuaSim.Core.World;

namespace SkuaSim.Core.Api;

public sealed class OptionsApi : ApiBase, IPropertyDefaults, IProxyAware
{
    private static readonly Dictionary<string, object?> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ReloginTryDelay"] = 3000,
        ["LoginTimeout"] = 30,
        ["AcceptACDrops"] = true,
        ["AcceptAllDrops"] = false,
        ["RejectAllDrops"] = false,
        ["AggroAllMonsters"] = false,
        ["AggroMonsters"] = false,
        ["AttackWithoutTarget"] = false,
        ["AutoRelogin"] = true,
        ["AutoReloginAny"] = false,
        ["RetryRelogin"] = true,
        ["CustomGuild"] = "",
        ["CustomName"] = "",
        ["DisableCollisions"] = true,
        ["DisableDeathAds"] = true,
        ["DisableFX"] = true,
        ["MaximumTries"] = 20,
        ["ActionDelay"] = 700,
        ["GuildColor"] = 0,
        ["HidePlayers"] = false,
        ["HuntBuffer"] = 1,
        ["HuntDelay"] = 1000,
        ["InfiniteRange"] = true,
        ["LagKiller"] = true,
        ["HeadlessMode"] = false,
        ["StreamerMode"] = false,
        ["OptimizePlayers"] = false,
        ["UseFunctionBasedSkills"] = false,
        ["LoadTimeout"] = 15,
        ["ReloginServer"] = "Twilly",
        ["Magnetise"] = true,
        ["NameColor"] = 0,
        ["PrivateRooms"] = true,
        ["PrivateNumber"] = 100000,
        ["RestPackets"] = true,
        ["SafeRelogin"] = true,
        ["SafeTimings"] = true,
        ["SetFPS"] = 30,
        ["ShowFPS"] = false,
        ["SkipCutscenes"] = true,
        ["WalkSpeed"] = 8,
        ["JoinMapTries"] = 3,
        ["QuestAcceptAndCompleteTries"] = 20,
        ["ReloginTries"] = 3,
        ["HuntPriority"] = HuntPriorities.None,
        ["EnableScriptWatchdog"] = false,
        ["ScriptWatchdogTimeout"] = 120,
        ["WatchdogAutoRestart"] = false,
        ["WatchdogDiscordAlerts"] = false
    };

    private ProxyState? _state;

    public OptionsApi(SimContext ctx) : base(ctx) { }

    public void Attach(object proxy, ProxyState state)
    {
        _state = state;
        Ctx.OptionsState = state;
        foreach (var (k, v) in Ctx.Scenario.BotOptions)
        {
            var targetType = Defaults.TryGetValue(k, out var d) && d != null ? d.GetType() : typeof(string);
            state.Values[k] = SimContext.ConvertJson(v, targetType);
        }
    }

    public ImmutableDictionary<string, Func<object>> OptionDictionary => ImmutableDictionary<string, Func<object>>.Empty;

    public bool TryGetDefault(string propertyName, out object? value) => Defaults.TryGetValue(propertyName, out value);

    public void Reset() => _state?.Values.Clear();
    public void ResetToDefault() => _state?.Values.Clear();
    public void Save() { }
}

public sealed class ConfigApi : ApiBase
{
    public ConfigApi(SimContext ctx) : base(ctx) { }

    public Dictionary<string, List<IOption>> MultipleOptions { get; } = new();
    public List<IOption> Options { get; } = new();
    public string OptionsFile { get; set; } = "skuasim_options.json";
    public string Storage { get; set; } = "skuasim_options";
    public Dictionary<IOption, string> OptionValues { get; } = new();
    public IReadOnlyList<string> ResetNotices { get; } = Array.Empty<string>();

    public T Get<T>(IOption option) => option == null ? default! : Get<T>(option.Name)!;

    public T Get<T>(string name)
    {
        if (string.Equals(name, "SkipOptions", StringComparison.OrdinalIgnoreCase))
        {
            if (Ctx.Scenario.Options.TryGetValue(name, out var explicitVal))
                return (T)SimContext.ConvertJson(explicitVal, typeof(T))!;
            return (T)(object)true;
        }

        if (Ctx.Scenario.Options.TryGetValue(name, out var jsonVal))
            return (T)SimContext.ConvertJson(jsonVal, typeof(T))!;

        var opt = Options.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                  ?? MultipleOptions.Values.SelectMany(x => x).FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
        if (opt != null)
        {
            if (OptionValues.TryGetValue(opt, out var raw))
                return ValueConverter.Convert<T>(raw)!;
            return ValueConverter.Convert<T>(opt.DefaultValue)!;
        }

        return default!;
    }

    public T Get<T>(string category, string name)
    {
        if (!string.IsNullOrWhiteSpace(category) &&
            Ctx.Scenario.Options.TryGetValue($"{category}:{name}", out var scopedVal))
        {
            return (T)SimContext.ConvertJson(scopedVal, typeof(T))!;
        }
        return Get<T>(name)!;
    }

    public string GetDirect(IOption option) =>
        option != null && OptionValues.TryGetValue(option, out var raw) ? raw : option?.DefaultValue?.ToString() ?? "";

    public void Load() { }
    public void Save() { }

    public void Set(string name, object value)
    {
        var opt = Options.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
        if (opt != null)
            OptionValues[opt] = value?.ToString() ?? "";
    }

    public void Set(string category, string name, object value) => Set(name, value);
    public void Set<T>(IOption option, T value)
    {
        if (option != null)
            OptionValues[option] = value?.ToString() ?? "";
    }

    public void SetDefaults()
    {
        foreach (var opt in Options)
            OptionValues[opt] = opt.DefaultValue?.ToString() ?? "";
    }

    public void Configure() => Ctx.Trace.Info("Config.Configure() suppressed in headless simulation.");
}

public sealed class ManagerApi : ApiBase
{
    public ManagerApi(SimContext ctx) : base(ctx) { }

    public bool ShouldExit => Ctx.StopRequested;
    public bool ScriptRunning => !Ctx.StopRequested;
    public string LoadedScript => Ctx.LoadedScriptPath ?? "SimScript.cs";
    public string CompiledScript => LoadedScript;
    public IScriptOptionContainer Config { get => Bot.Config!; set { } }
    public IScriptOptionContainer RunningConfig => Bot.Config!;

    public Task RestartScriptAsync()
    {
        Ctx.RequestStop("script requested RestartScriptAsync()");
        return Task.CompletedTask;
    }

    public Task<Exception?> StartScript() => Task.FromResult<Exception?>(null);
    public void SetLoadedScript(string path) => Ctx.LoadedScriptPath = path;

    public ValueTask StopScript(bool runScriptStoppingEvent)
    {
        Ctx.RequestStop("script called Manager.StopScript()");
        return ValueTask.CompletedTask;
    }
}

public sealed class EventsApi : ApiBase, IProxyAware
{
    private ProxyState? _state;

    public EventsApi(SimContext ctx) : base(ctx) { }

    public void Attach(object proxy, ProxyState state)
    {
        _state = state;
        Ctx.EventsState = state;
    }

    public void ClearHandlers() => _state?.Events.Clear();
}

/// <summary>
/// Bot.Servers over the run's AQW server list (<see cref="SimContext.Servers"/>: scenario "servers", else the local
/// servers.json from <c>skuasim servers-update</c>, else a stand-in reported as synthesized). Logins that name a server
/// are checked against it: unknown, offline, member-only (for non-members) and full (for non-members) servers refuse
/// the login and leave the player logged out. Logins without a server go back to the current one.
/// </summary>
public sealed class ServersApi : ApiBase
{
    private List<Server>? _servers;

    public ServersApi(SimContext ctx) : base(ctx)
    {
        var first = ctx.Servers.FirstOrDefault(s => s.Online) ?? ctx.Servers.FirstOrDefault();
        LastName = first?.Name ?? "Twilly";
        LastIP = first?.Ip ?? "";
    }

    public string LastIP { get; set; }
    public string CachedUsername => World.Username;
    public string LastName { get; set; }
    public bool IsIntentionalLogout { get; set; }
    public bool IsReloginInProgress => false;
    public bool IsLogoutInProgress => false;
    public List<Server> ServerList => Servers();
    public List<Server> CachedServers => Servers();
    public bool IsConnected => World.LoggedIn;

    private List<Server> Servers()
    {
        Ctx.NoteServersUsed();
        return _servers ??= Ctx.Servers.Select(s => new Server
        {
            Name = s.Name,
            IP = s.Ip,
            Port = s.Port,
            Online = s.Online,
            PlayerCount = s.PlayerCount,
            MaxPlayers = s.MaxPlayers,
            Upgrade = s.MemberOnly,
            ChatLevel = s.ChatLevel,
            Lang = s.Lang,
            Level = s.Level
        }).ToList();
    }

    private ServerSetup? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Ctx.Servers.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private ServerSetup? FindIp(string? ip, int? port = null) =>
        string.IsNullOrWhiteSpace(ip) ? null
            : Ctx.Servers.FirstOrDefault(s => string.Equals(s.Ip, ip.Trim(), StringComparison.OrdinalIgnoreCase) && (port == null || s.Port == port));

    /// <summary>Logs in to a named server, or refuses (player logged out) when the list says the login would fail.</summary>
    private bool LoginTo(ServerSetup? server, string requested)
    {
        Ctx.NoteServersUsed();
        var refusal = server == null ? $"no server '{requested}' in the server list" : server.RefusalFor(World.IsMember);
        if (refusal != null)
        {
            Ctx.Trace.World($"Login to {server?.Name ?? requested} refused: {refusal}.");
            if (World.LoggedIn)
                World.Logout(autoRelogin: false, reloginDelayMs: 0);
            return false;
        }
        LastName = server!.Name;
        LastIP = server.Ip;
        return World.Relogin();
    }

    public void SetLoginInfo(string username, string password) { }
    public ValueTask<List<Server>> GetServers(bool forceUpdate) => new(Servers());
    public bool Connect(Server server) => LoginTo(Find(server?.Name), server?.Name ?? "");
    public bool Connect(string serverName) => LoginTo(Find(serverName), serverName);
    public bool ConnectIP(string serverIp) => LoginTo(FindIp(serverIp), serverIp);
    public bool ConnectIP(string serverIp, int port) => LoginTo(FindIp(serverIp, port), $"{serverIp}:{port}");
    public void Login(string username, string password) => World.Relogin();
    public void Login() => World.Relogin();
    public void Logout() => World.Logout(Ctx.GetBotOption("AutoRelogin", true), Ctx.GetBotOption("ReloginTryDelay", 3000));
    public bool Reconnect(string serverName, int loginDelay) => LoginTo(Find(serverName), serverName);
    public bool Reconnect(Server server, int loginDelay) => LoginTo(Find(server?.Name), server?.Name ?? "");
    public bool Relogin(Server? server) => server is null ? World.Relogin() : LoginTo(Find(server.Name), server.Name);
    public bool Relogin(string serverName) => LoginTo(Find(serverName), serverName);
    public bool ReloginIP(string ip) => LoginTo(FindIp(ip), ip);
    public bool EnsureRelogin(string serverName) => LoginTo(Find(serverName), serverName);
    public Task<bool> EnsureRelogin(CancellationToken token) => Task.FromResult(World.Relogin());
    public Task<bool> EnsureRelogin(string serverName, CancellationToken token) => Task.FromResult(LoginTo(Find(serverName), serverName));
}

public sealed class SendApi : ApiBase
{
    public SendApi(SimContext ctx) : base(ctx) { }

    public void Packet(string packet, string type)
    {
        if (string.IsNullOrWhiteSpace(packet))
            return;
        var parts = packet.Split('%', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return;

        var cmd = parts[2];
        switch (cmd)
        {
            case "getMapItem":
                if (parts.Length >= 5 && int.TryParse(parts[4], out var mapItemId))
                    World.GetMapItem(mapItemId, 1);
                break;

            case "moveToCell":
                if (parts.Length >= 6)
                    World.Jump(parts[4], parts[5], autoCorrect: true);
                break;

            case "sellItem":
                // %xt%zm%sellItem%<room>%<ItemID>%<qty>%<CharItemID>% — CoreBots.SellItem.
                if (parts.Length >= 6 && int.TryParse(parts[4], out var sellItemId) && int.TryParse(parts[5], out var sellQty))
                    World.Sell(null, sellItemId, Math.Max(1, sellQty));
                break;

            case "mtcid":
                // %xt%zm%mtcid%<room>%<cell number>% — PvP door move (CoreBots.PvPMove).
                if (parts.Length >= 5 && int.TryParse(parts[4], out var cellNumber))
                    World.MoveToCellNumber(cellNumber);
                break;

            case "acceptQuest":
                if (parts.Length >= 5 && int.TryParse(parts[4], out var acceptQuestId))
                    World.AcceptQuest(acceptQuestId);
                break;

            case "tryQuestComplete":
                if (parts.Length >= 5 && int.TryParse(parts[4], out var questId))
                {
                    var rewardId = parts.Length >= 6 && int.TryParse(parts[5], out var rId) ? rId : -1;
                    var turnIns = parts.Length >= 8 && int.TryParse(parts[7], out var qty) ? Math.Max(1, qty) : 1;
                    for (var i = 0; i < turnIns; i++)
                    {
                        if (!World.CompleteQuest(questId, rewardId, log: i == 0))
                            break;
                    }
                }
                break;

            case "enhanceItemShop":
                if (parts.Length >= 7 &&
                    int.TryParse(parts[4], out var targetItemId) &&
                    int.TryParse(parts[5], out var enhShopItemId) &&
                    int.TryParse(parts[6], out var enhShopId))
                {
                    World.EnhanceItem(targetItemId, enhShopItemId, enhShopId);
                }
                break;
        }
    }

    public void ClientPacket(string packet, string type) { }
    public void ClientEvent(string message, string sentBy) => Ctx.Trace.Info($"[ClientEvent] {message}");
    public void ClientGuild(string message, string sentBy) { }
    public void ClientModerator(string message, string sentBy) => Ctx.Trace.Info($"[ClientModerator] {message}");
    public void ClientServer(string message, string sentBy) => Ctx.Trace.Info($"[ClientServer] {message}");
    public void ClientWarning(string message, string sentBy) => Ctx.Trace.Info($"[ClientWarning] {message}");
    public void ClientWhisper(string message, string sentBy) { }
    public void ClientZone(string message, string sentBy) { }
    public void Whisper(string name, string message) { }
    public Task ClientPacketSpam(string packet, string type, int delay, CancellationToken token) => Task.CompletedTask;
    public Task PacketSpam(string packet, string type, int delay, CancellationToken token) => Task.CompletedTask;
}

public sealed class HandlersApi : ApiBase
{
    private readonly List<SimHandler> _handlers = new();

    public HandlersApi(SimContext ctx) : base(ctx) { }

    public IEnumerable<IHandler> CurrentHandlers => _handlers.ToList();

    public IHandler RegisterHandler(int ticks, Func<IScriptInterface, bool> function, string? name)
    {
        var h = new SimHandler { Name = name ?? $"Handler_{_handlers.Count + 1}", Ticks = Math.Max(1, ticks), Function = function };
        _handlers.RemoveAll(x => x.Name == h.Name);
        _handlers.Add(h);
        var intervalMs = Math.Max(200, ticks * 500L);
        Ctx.Clock.Scheduler.ScheduleRecurring(Ctx.Clock.NowMs + intervalMs, intervalMs, "handler:" + h.Name, () =>
        {
            if (!_handlers.Contains(h) || Ctx.StopRequested)
                return false;
            var keep = h.Function(Bot);
            if (!keep)
                _handlers.Remove(h);
            return keep;
        });
        return h;
    }

    public bool Remove(string name) => _handlers.RemoveAll(h => h.Name == name) > 0;
    public bool Remove(IHandler handler) => handler is SimHandler sh && _handlers.Remove(sh);
    public bool Remove(List<IHandler> handlers)
    {
        if (handlers == null) return false;
        var removed = false;
        foreach (var h in handlers)
            removed |= Remove(h);
        return removed;
    }
    public void Clear() => _handlers.Clear();

    private sealed class SimHandler : IHandler
    {
        public required Func<IScriptInterface, bool> Function { get; init; }
        public required string Name { get; init; }
        public required int Ticks { get; init; }
    }
}

public sealed class FlashApi : ApiBase
{
    public FlashApi(SimContext ctx) : base(ctx) { }

    public bool IsWorldLoaded => World.LoggedIn && World.MapLoaded;

    public void InitializeFlash() { }
    public string Call(string function, object[] args) => "";
    public T Call<T>(string function, object[] args) => DefaultValues.For(typeof(T)) is T v ? v : default!;
    public object? Call(string function, Type type, object[] args) => DefaultValues.For(type);
    public bool IsNull(string path) => false;

    /// <summary>
    /// Numeric avatar fields read through Flash (CoreBots.CanBuy checks ACs with
    /// <c>GetGameObject&lt;int&gt;("world.myAvatar.objData.intCoins")</c>).
    /// </summary>
    private long? AvatarNumber(string path) => path.ToLowerInvariant() switch
    {
        "world.myavatar.objdata.intcoins" => World.Coins,
        "world.myavatar.objdata.intgold" => World.Gold,
        "world.myavatar.objdata.intlevel" => World.Level,
        _ => null
    };

    public string GetGameObject(string path)
    {
        if (AvatarNumber(path) is { } number)
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (string.Equals(path, "sfc.myUserName", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "world.myAvatar.objData.strUsername", StringComparison.OrdinalIgnoreCase))
            return JsonConvert.SerializeObject(World.Username ?? "SimPlayer");
        if (string.Equals(path, "world.myAvatar.objData.guild.Name", StringComparison.OrdinalIgnoreCase))
            return "null";
        if (string.Equals(path, "ui.monsterIcon.redX.visible", StringComparison.OrdinalIgnoreCase))
            return "false";
        if (string.Equals(path, "world.shopinfo.items", StringComparison.OrdinalIgnoreCase))
            return BuildShopItemsJArray().ToString(Formatting.None);
        if (string.Equals(path, "world.myAvatar.items", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "world.bankinfo.items", StringComparison.OrdinalIgnoreCase))
            return BuildInventoryJArray().ToString(Formatting.None);
        return "true";
    }

    public T GetGameObject<T>(string path, T def)
    {
        try
        {
            if (AvatarNumber(path) is { } number)
                return (T)Convert.ChangeType(number, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            if (string.Equals(path, "sfc.myUserName", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "world.myAvatar.objData.strUsername", StringComparison.OrdinalIgnoreCase))
                return typeof(T) == typeof(string) ? (T)(object)(World.Username ?? "SimPlayer") : def;
            if (string.Equals(path, "world.myAvatar.objData.guild.Name", StringComparison.OrdinalIgnoreCase))
                return def;
            if (string.Equals(path, "ui.monsterIcon.redX.visible", StringComparison.OrdinalIgnoreCase))
                return typeof(T) == typeof(bool) ? (T)(object)false : def;
            if (string.Equals(path, "world.shopinfo.items", StringComparison.OrdinalIgnoreCase))
            {
                var arr = BuildShopItemsJArray();
                if (typeof(T) == typeof(JArray))
                    return (T)(object)arr;
                return JsonConvert.DeserializeObject<T>(arr.ToString(Formatting.None)) ?? def;
            }
            if (string.Equals(path, "world.myAvatar.items", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "world.bankinfo.items", StringComparison.OrdinalIgnoreCase))
            {
                var arr = BuildInventoryJArray();
                if (typeof(T) == typeof(JArray))
                    return (T)(object)arr;
                return JsonConvert.DeserializeObject<T>(arr.ToString(Formatting.None)) ?? def;
            }
        }
        catch { }
        return def;
    }

    public string GetGameObjectStatic(string path) => "true";
    public T GetGameObjectStatic<T>(string path, T def) => def;
    public void SetGameObject(string path, object value) { }

    public string CallGameFunction(string path, object[] args)
    {
        if (string.Equals(path, "loaderInfo.applicationDomain.hasDefinition", StringComparison.OrdinalIgnoreCase) &&
            args is { Length: > 0 } && string.Equals(args[0]?.ToString(), "InvCat", StringComparison.OrdinalIgnoreCase))
        {
            return "false";
        }

        if (string.Equals(path, "loaderInfo.applicationDomain.getDefinition", StringComparison.OrdinalIgnoreCase) &&
            args is { Length: > 0 } && string.Equals(args[0]?.ToString(), "InvCat", StringComparison.OrdinalIgnoreCase))
        {
            return """{"MISC_SLOTS":300,"MISC_TYPES":["Resource","Item","Quest Item","Note","Misc","ServerUse"]}""";
        }

        if (string.Equals(path, "world.sendBuyItemRequestWithQuantity", StringComparison.OrdinalIgnoreCase) &&
            args is { Length: > 0 } && args[0] != null)
        {
            try
            {
                var jObj = JObject.FromObject(args[0]);
                var itemId = jObj.Value<int?>("ItemID") ?? 0;
                var shopItemId = jObj.Value<int?>("ShopItemID") ?? -1;
                var qty = jObj.Value<int?>("iQty") ?? 1;
                if (itemId > 0)
                    World.Buy(null, itemId, shopItemId, qty);
            }
            catch { }
            return "true";
        }

        if (string.Equals(path, "world.getQuestValue", StringComparison.OrdinalIgnoreCase))
            return QuestSlotValue(args).ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (string.Equals(path, "world.abandonQuest", StringComparison.OrdinalIgnoreCase) &&
            args is { Length: > 0 } && int.TryParse(args[0]?.ToString(), out var qid))
        {
            lock (World.Sync)
                World.ActiveQuests.Remove(qid);
        }
        return "true";
    }

    /// <summary>
    /// "world.getQuestValue(slot)": the character's progress in a quest-chain slot (AQW stores story progress per slot;
    /// a quest is done when the slot value reaches its "Value"). CoreBots.isCompletedBefore relies on it.
    /// </summary>
    private int QuestSlotValue(object[] args)
    {
        if (args is not { Length: > 0 } || !int.TryParse(args[0]?.ToString(), out var slot) || slot < 0)
            return 0;
        lock (World.Sync)
            return World.SlotProgress.GetValueOrDefault(slot);
    }

    public T CallGameFunction<T>(string path, object[] args)
    {
        if (string.Equals(path, "world.maximumQuestTurnIns", StringComparison.OrdinalIgnoreCase) &&
            typeof(T) == typeof(int))
        {
            return (T)(object)1;
        }
        if (string.Equals(path, "world.getQuestValue", StringComparison.OrdinalIgnoreCase))
        {
            var value = QuestSlotValue(args);
            if (typeof(T) == typeof(int)) return (T)(object)value;
            if (typeof(T) == typeof(long)) return (T)(object)(long)value;
            if (typeof(T) == typeof(string)) return (T)(object)value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return typeof(T) == typeof(bool) ? (T)(object)true : default!;
    }

    public void Dispose() { }

    private JArray BuildShopItemsJArray()
    {
        var arr = new JArray();
        var items = World.CurrentShop?.Items;
        if (items == null)
            return arr;

        foreach (var it in items)
        {
            arr.Add(new JObject
            {
                ["ItemID"] = it.ID,
                ["ShopItemID"] = it.ShopItemID,
                ["sName"] = it.Name,
                ["sType"] = it.CategoryString ?? "Item",
                ["iCost"] = it.Cost,
                ["iLvl"] = it.Level,
                ["EnhPatternID"] = it.EnhancementPatternID,
                ["ProcID"] = it.ProcID,
                ["sMeta"] = it.Meta ?? "",
                ["sES"] = it.ItemGroup ?? "None",
                ["bCoins"] = it.Coins ? 1 : 0,
                ["bUpg"] = it.Upgrade ? 1 : 0,
                ["iStk"] = Math.Max(1, it.MaxStack),
                ["iQty"] = Math.Max(1, it.Quantity)
            });
        }
        return arr;
    }

    private JArray BuildInventoryJArray()
    {
        var arr = new JArray();
        lock (World.Sync)
        {
            foreach (var it in World.Inventory)
            {
                arr.Add(new JObject
                {
                    ["ItemID"] = it.ID,
                    ["CharItemID"] = it.CharItemID,
                    ["sName"] = it.Name,
                    ["sType"] = it.CategoryString ?? "Item",
                    ["iLvl"] = it.Level,
                    ["EnhLvl"] = it.EnhancementLevel,
                    ["EnhPatternID"] = it.EnhancementPatternID,
                    ["ProcID"] = it.ProcID,
                    ["sES"] = it.ItemGroup ?? "None",
                    ["sMeta"] = it.Meta ?? "",
                    ["iStk"] = Math.Max(1, it.MaxStack),
                    ["iQty"] = it.Quantity,
                    ["bCoins"] = it.Coins ? 1 : 0,
                    ["bUpg"] = it.Upgrade ? 1 : 0,
                    ["bEquip"] = it.Equipped ? 1 : 0
                });
            }
        }
        return arr;
    }
}

public sealed class StatsApi : ApiBase
{
    public StatsApi(SimContext ctx) : base(ctx) { }

    public int Deaths { get => World.Deaths; set => World.Deaths = value; }
    public int Drops { get => World.DropsPicked; set => World.DropsPicked = value; }
    public int Kills { get => World.Kills; set => World.Kills = value; }
    public int QuestsAccepted { get => World.QuestsAcceptedCount; set => World.QuestsAcceptedCount = value; }
    public int QuestsCompleted { get => World.QuestsCompletedCount; set => World.QuestsCompletedCount = value; }
    public int Relogins { get => World.Relogins; set => World.Relogins = value; }
    public int InventorySpace => Ctx.Scenario.Player.InventorySlots;
    public int InventoryFilledSpace => World.Inventory.Count;
    public int InventoryFreeSpace => Math.Max(0, InventorySpace - InventoryFilledSpace);
    public int BankSpace => Ctx.Scenario.Player.BankSlots;
    public int BankFilledSpace => World.Bank.Count;
    public int BankFreeSpace => Math.Max(0, BankSpace - BankFilledSpace);
    public TimeSpan Time => TimeSpan.FromMilliseconds(Ctx.Clock.NowMs);
    public TimeSpan SessionTime => Time;

    public void RecordItemPickup(int quantity) { }
    public void UpdateGameplaySnapshot(int experience, int requiredExperience, int level, int gold, int classId, int classPoints, int classPointsToRank, int classRank, bool connected) { }
    public void Reset() { }
    public void GetSpace() { }
}

public sealed class AurasApi : ApiBase
{
    public AurasApi(SimContext ctx) : base(ctx) { }

    public List<Aura> Auras { get; } = new();
    public List<AuraSnapshot> Snapshots { get; } = new();

    public AuraSnapshot? GetAuraSnapshot(string auraName) => null;
    public int GetAuraStacks(string auraName) => 0;
    public bool HasActiveAura(string auraName) => false;
    public Aura? GetAura(string auraName) => null;
    public float GetAuraValue(string auraName) => 0f;
    public bool HasAnyActiveAura(string[] auraNames) => false;
    public bool TryGetAura(string auraName, out Aura? aura) { aura = null; return false; }
    public string? GetPlayerAura(string playerName) => null;
    public string GetMonsterAura(string monsterName) => "";
    public string GetMonsterAura(int monID) => "";
}

public sealed class UltraBossHelperApi : ApiBase
{
    public UltraBossHelperApi(SimContext ctx) : base(ctx) { }

    public bool IsCounterAttackEnabled { get; private set; }
    public bool IsCounterAttackActive => false;
    public void EnableCounterAttack() => IsCounterAttackEnabled = true;
    public void DisableCounterAttack() => IsCounterAttackEnabled = false;
    public void SetAttacksStopped(bool shouldStop) { }
    public (bool, bool, bool) AnalyzeChargeMechanics(IScriptSelfAuras auras, string positiveChargeName, string negativeChargeName, string reversedSuffix) => (false, false, false);
    public bool CheckAuraThreshold(IScriptSelfAuras auras, string auraName, int threshold, string comparison) => false;
    public List<Aura> GetAurasMatchingPattern(List<Aura> auras, string namePattern) => new();
    public bool ShouldUseSkill(IScriptSelfAuras selfAuras, Dictionary<string, Func<float, bool>> conditions) => true;
    public bool ShouldUseSkill(IScriptTargetAuras targetAuras, Dictionary<string, Func<float, bool>> conditions) => true;
}

public sealed class LiteApi : ApiBase, IPropertyDefaults
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public LiteApi(SimContext ctx) : base(ctx) { }

    public bool TryGetDefault(string propertyName, out object? value)
    {
        value = _values.TryGetValue(propertyName, out var v) ? v : false;
        return true;
    }

    public T Get<T>(string optionName) =>
        _values.TryGetValue(optionName, out var v) && v is T typed ? typed : default!;

    public void Set<T>(string optionName, T value) =>
        _values[optionName] = value;
}

public sealed class AccountsApi : ApiBase
{
    private readonly Dictionary<string, HashSet<string>> _tags = new(StringComparer.OrdinalIgnoreCase);

    public AccountsApi(SimContext ctx) : base(ctx) { }

    public List<string> GetTags() => GetTags(World.Username);
    public List<string> GetTags(string username) =>
        _tags.TryGetValue(username ?? World.Username, out var set) ? set.ToList() : new List<string>();
    public bool HasTag(string tag) => HasTag(World.Username, tag);
    public bool HasTag(string username, string tag) =>
        _tags.TryGetValue(username ?? World.Username, out var set) && set.Contains(tag);
    public bool AddTag(string tag) => AddTag(World.Username, tag);
    public bool AddTag(string username, string tag)
    {
        var key = username ?? World.Username;
        if (!_tags.TryGetValue(key, out var set))
            _tags[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return set.Add(tag);
    }
    public bool RemoveTag(string tag) => RemoveTag(World.Username, tag);
    public bool RemoveTag(string username, string tag) =>
        _tags.TryGetValue(username ?? World.Username, out var set) && set.Remove(tag);
    public void AddTags(string[] tags) => AddTags(World.Username, tags);
    public bool AddTags(string username, string[] tags)
    {
        if (tags == null) return false;
        foreach (var t in tags) AddTag(username, t);
        return true;
    }
    public void RemoveTags(string[] tags) => RemoveTags(World.Username, tags);
    public bool RemoveTags(string username, string[] tags)
    {
        if (tags == null) return false;
        foreach (var t in tags) RemoveTag(username, t);
        return true;
    }
    public void SetTags(string[] tags) => SetTags(World.Username, tags);
    public bool SetTags(string username, string[] tags)
    {
        ClearTags(username);
        return AddTags(username, tags);
    }
    public void ClearTags() => ClearTags(World.Username);
    public bool ClearTags(string username) => _tags.Remove(username ?? World.Username);
    public List<ManagedAccount> GetAllAccounts() => new();
}

public sealed class BoostsApi : ApiBase
{
    public BoostsApi(SimContext ctx) : base(ctx) { }

    public bool Enabled { get; set; }
    public bool UsingBoosts => Enabled && (UseExperienceBoost || UseClassBoost || UseGoldBoost || UseReputationBoost);

    public bool UseExperienceBoost { get => World.XpBoostActive; set => World.XpBoostActive = value; }
    public bool UseClassBoost { get => World.ClassBoostActive; set => World.ClassBoostActive = value; }
    public bool UseGoldBoost { get => World.GoldBoostActive; set => World.GoldBoostActive = value; }
    public bool UseReputationBoost { get => World.RepBoostActive; set => World.RepBoostActive = value; }

    public int ExperienceBoostID { get; set; } = 1;
    public int ClassBoostID { get; set; } = 2;
    public int GoldBoostID { get; set; } = 3;
    public int ReputationBoostID { get; set; } = 4;

    public void SetExperienceBoostID(int id = 1) => ExperienceBoostID = id > 0 ? id : 1;
    public void SetClassBoostID(int id = 2) => ClassBoostID = id > 0 ? id : 2;
    public void SetGoldBoostID(int id = 3) => GoldBoostID = id > 0 ? id : 3;
    public void SetReputationBoostID(int id = 4) => ReputationBoostID = id > 0 ? id : 4;

    public void Start() => Enabled = true;
    public void Stop() => Enabled = false;
    public Task StartAsync() { Enabled = true; return Task.CompletedTask; }
    public Task StopAsync() { Enabled = false; return Task.CompletedTask; }
    public void SetAllBoostsIDs() { }
}

/// <summary>
/// Registers a minimal IServiceProvider in CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
/// so scripts calling Ioc.Default.GetRequiredService&lt;ILogService&gt;() (like CoreBots) succeed.
/// </summary>
public sealed class SimServiceProvider : IServiceProvider
{
    private static readonly object Gate = new();
    private static SimLogService? _logService;

    public static SimLogService Attach(SimulationTrace trace)
    {
        lock (Gate)
        {
            if (_logService == null)
            {
                _logService = new SimLogService();
                try
                {
                    Ioc.Default.ConfigureServices(new SimServiceProvider());
                }
                catch
                {
                    // Ioc.Default may already be configured by an earlier run in the same process.
                }
            }
            _logService.Bind(trace);
            return _logService;
        }
    }

    public object? GetService(Type serviceType) =>
        serviceType == typeof(ILogService) ? _logService : null;
}

public sealed class SimLogService : ILogService
{
    private SimulationTrace? _trace;

    public void Bind(SimulationTrace trace) => _trace = trace;

    public void DebugLog(string message) => _trace?.Log(message);
    public void ScriptLog(string message) => _trace?.Log(message);
    public void FlashLog(string message) { }
    public void ClearLog(LogType logType) { }
    public List<string> GetLogs(LogType logType) =>
        _trace?.ScriptLog.Select(e => $"[{e.Clock}] {e.Text}").ToList() ?? new List<string>();
}
