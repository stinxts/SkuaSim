using System.Diagnostics;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using Newtonsoft.Json.Linq;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Options;
using SkuaSim.Collector.Models;
using SkuaSim.Collector.Parsing;
using SkuaSim.Collector.Storage;

namespace SkuaSim.Collector;

/// <summary>
/// Passive data collection plugin for Skua and VibeSkua.
/// Listens to incoming game packets and events on a background channel without interfering
/// with running scripts or game state, and merges anonymized game data into gamedata.json.
/// </summary>
public sealed class SkuaSimCollectorPlugin : ISkuaPlugin
{
    private const string MenuButtonName = "SkuaSim Collector";
    private const string OptEnabled = "enabled";
    private const string OptCombatDetails = "collect_combat_details";
    private const string OptFlushSeconds = "flush_interval_seconds";
    private const string OptMirrorFile = "mirror_file_path";
    private const string OptPacketSchemas = "dev_capture_packet_schemas";

    public string Name => "SkuaSim Collector";
    public string Author => "SkuaSim";
    public string Description => "Passive game data collector (maps, monsters, damage, drops, shops, quests, classes, auras, and procs) for the SkuaSim test environment.";
    public string OptionsStorage => "skuasim_collector_options";

    public List<IOption> Options { get; } = new()
    {
        new Option<bool>(OptEnabled, "Active Collection", "Enables or pauses passive background data collection.", true),
        new Option<bool>(OptCombatDetails, "Combat Details", "Collects monster min/max/avg damage, attack intervals, skill cues, zones, procs, and aura effects.", true),
        new Option<int>(OptFlushSeconds, "Save Interval (s)", "Interval in seconds to merge collected data into gamedata.json.", 30),
        new Option<string>(OptMirrorFile, "Mirror File Path (Optional)", "Extra file path to mirror gamedata.json (e.g., SkuaSim project folder).", GetDefaultWorkspaceMirrorPath()),
        new Option<bool>(OptPacketSchemas, "Packet Schema Sampler", "Accumulates strictly anonymized sample payloads per combat/boss/event packet type into packet_schemas.json across sessions (zero personal data).", true)
    };

    private IScriptInterface? _bot;
    private IPluginHelper? _helper;
    private IPluginContainer? _container;
    private PacketCollectorEngine _engine = new();
    private GameDatabaseStore? _store;

    private Channel<Action>? _workChannel;
    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private Task? _flushTimerTask;
    private EventHandler? _processExitHandler;
    private volatile bool _runtimePaused;
    private int _playerRefreshPending;

    public void Load(IServiceProvider provider, IPluginHelper helper)
    {
        _helper = helper;
        _bot = provider.GetService(typeof(IScriptInterface)) as IScriptInterface ?? IScriptInterface.Instance;

        if (provider.GetService(typeof(IPluginManager)) is IPluginManager pm)
        {
            try { _container = pm.GetContainer(this); } catch { }
        }

        _engine = new PacketCollectorEngine
        {
            CollectCombatDetails = GetBoolOption(OptCombatDetails, true)
        };
        _engine.SchemaSampler.Enabled = GetBoolOption(OptPacketSchemas, true);

        _store = CreateStore();

        _workChannel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => ProcessQueueLoopAsync(_cts.Token));
        _flushTimerTask = Task.Run(() => PeriodicFlushLoopAsync(_cts.Token));

        if (_bot?.Flash != null)
        {
            _bot.Flash.FlashCall += OnFlashCall;
        }

        RegisterMessengerHandlers();

        _processExitHandler = (_, _) => FlushSafe();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;

        try
        {
            _helper.AddMenuButton(MenuButtonName, OnMenuButtonClicked);
        }
        catch
        {
            // Ignore if host menu isn't ready
        }

        // Capture initial map state if already logged in and loaded
        ScheduleMapSnapshot(delayMs: 1000);

        Log("[SkuaSim Collector] Passive data collector started.");
    }

    public void Unload()
    {
        try
        {
            _helper?.RemoveMenuButton(MenuButtonName);
        }
        catch { }

        if (_processExitHandler != null)
        {
            AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
            _processExitHandler = null;
        }

        if (_bot?.Flash != null)
        {
            _bot.Flash.FlashCall -= OnFlashCall;
        }

        try
        {
            StrongReferenceMessenger.Default.UnregisterAll(this);
        }
        catch { }

        try
        {
            _cts?.Cancel();
            _workChannel?.Writer.TryComplete();
            if (_workerTask != null)
                Task.WaitAny(new[] { _workerTask }, TimeSpan.FromSeconds(2));
        }
        catch { }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }

        FlushSafe();
        Log("[SkuaSim Collector] Passive data collector stopped and data saved.");
    }

    private void RegisterMessengerHandlers()
    {
        var messenger = StrongReferenceMessenger.Default;
        int channel = (int)MessageChannels.GameEvents;

        messenger.Register<SkuaSimCollectorPlugin, MapChangedMessage, int>(this, channel, static (r, m) => r.OnMapChanged(m));
        messenger.Register<SkuaSimCollectorPlugin, CellChangedMessage, int>(this, channel, static (r, m) => r.OnCellChanged(m));
        messenger.Register<SkuaSimCollectorPlugin, MonsterKilledMessage, int>(this, channel, static (r, m) => r.OnMonsterKilled(m));
        messenger.Register<SkuaSimCollectorPlugin, ItemDroppedMessage, int>(this, channel, static (r, m) => r.OnItemDropped(m));
        messenger.Register<SkuaSimCollectorPlugin, ShopLoadedMessage, int>(this, channel, static (r, m) => r.OnShopLoaded(m));
        messenger.Register<SkuaSimCollectorPlugin, QuestAcceptedMessage, int>(this, channel, static (r, m) => r.OnQuestAccepted(m));
        messenger.Register<SkuaSimCollectorPlugin, QuestTurninMessage, int>(this, channel, static (r, m) => r.OnQuestTurnedIn(m));
        messenger.Register<SkuaSimCollectorPlugin, PlayerDeathMessage, int>(this, channel, static (r, m) => r.OnPlayerDeath(m));
        messenger.Register<SkuaSimCollectorPlugin, CounterAttackMessage, int>(this, channel, static (r, m) => r.OnCounterAttack(m));
        messenger.Register<SkuaSimCollectorPlugin, ExtensionPacketMessage, int>(this, channel, static (r, m) => r.OnExtensionPacket(m));
    }

    private void OnFlashCall(string name, object[] args)
    {
        if (!IsCollectingActive() || args == null || args.Length == 0)
            return;

        if (string.Equals(name, "pext", StringComparison.Ordinal))
        {
            if (args[0] is string rawJson && !string.IsNullOrWhiteSpace(rawJson))
            {
                EnqueueWork(() => _engine.ProcessExtensionPacketJson(rawJson));
                if (rawJson.Contains("\"updateClass\"", StringComparison.Ordinal) ||
                    rawJson.Contains("\"equipItem\"", StringComparison.Ordinal) ||
                    rawJson.Contains("\"sAct\"", StringComparison.Ordinal))
                {
                    SchedulePlayerStateRefresh(delayMs: 350);
                }
            }
        }
        else if (string.Equals(name, "packet", StringComparison.Ordinal))
        {
            if (args[0] is string rawPacket && !string.IsNullOrWhiteSpace(rawPacket))
            {
                EnqueueWork(() => _engine.ProcessClientPacket(rawPacket));
            }
        }
    }

    private void OnExtensionPacket(ExtensionPacketMessage message)
    {
        // Note: "pext" via FlashCall already captures raw JSON strings directly.
        // This handler serves as a fallback if an ExtensionPacketMessage is published as a JObject without FlashCall.
        if (!IsCollectingActive() || message.Packet == null)
            return;

        if (message.Packet is JObject jObj && (_bot?.Flash == null))
        {
            EnqueueWork(() => _engine.ProcessExtensionPacketJObject(jObj));
        }
    }

    private void OnMapChanged(MapChangedMessage message)
    {
        if (!IsCollectingActive())
            return;

        ScheduleMapSnapshot(delayMs: 1500);
    }

    private void OnCellChanged(CellChangedMessage message)
    {
        if (!IsCollectingActive())
            return;

        var map = message.Map;
        var cell = message.Cell;
        var pad = message.Pad;
        EnqueueWork(() => _engine.RecordCellChange(map, cell, pad));
    }

    private void OnMonsterKilled(MonsterKilledMessage message)
    {
        if (!IsCollectingActive())
            return;

        var mapId = message.MapID;
        EnqueueWork(() => _engine.RecordMonsterKilled(mapId));
    }

    private void OnItemDropped(ItemDroppedMessage message)
    {
        if (!IsCollectingActive() || message.Item == null)
            return;

        var item = message.Item;
        var added = message.AddedToInv;
        var qtyNow = message.QuantityNow;
        EnqueueWork(() => _engine.RecordItemDropped(item, added, qtyNow));
    }

    private void OnShopLoaded(ShopLoadedMessage message)
    {
        if (!IsCollectingActive() || message.Info == null)
            return;

        var shopInfo = message.Info;
        EnqueueWork(() => _engine.RecordShopLoaded(shopInfo));
    }

    private void OnQuestAccepted(QuestAcceptedMessage message)
    {
        if (!IsCollectingActive() || message.QuestID <= 0)
            return;

        var questId = message.QuestID;
        EnqueueWork(() =>
        {
            var quest = TryFindQuestInTree(questId);
            if (quest != null)
            {
                _engine.RecordQuestModel(quest, accepted: true, turnedIn: false);
            }
        });
        if (_bot != null)
            SyncActiveQuests(_bot);
    }

    /// <summary>
    /// Mirrors the client's active quest IDs into the engine (RAM only, never persisted as a list). They are the
    /// denominator of quest-gated drop rates and identify quest-gated map items.
    /// </summary>
    private void SyncActiveQuests(IScriptInterface bot)
    {
        try
        {
            var active = bot.Quests.Active;
            if (active == null)
                return;
            var ids = active.Where(q => q != null).Select(q => q.ID).ToList();
            EnqueueWork(() => _engine.SyncActiveQuests(ids));
        }
        catch
        {
            // Quest list unavailable during transitions; the engine keeps its last known state.
        }
    }

    /// <summary>
    /// Samples how many players are in the current room (Skua Map.PlayerCount: a number, no names). The client does
    /// not expose the room cap ("world.areaData.intMaxPlayers" was missing in all 58 probes), so the peak count per
    /// map is kept as a lower bound.
    /// </summary>
    private void SamplePlayerCount(IScriptInterface bot)
    {
        try
        {
            if (!bot.Map.Loaded)
                return;
            var mapName = bot.Map.Name;
            var count = bot.Map.PlayerCount;
            if (count > 0)
                EnqueueWork(() => _engine.RecordPlayerCount(mapName, count));
        }
        catch
        {
            // Player list unavailable during transitions.
        }
    }

    private void OnQuestTurnedIn(QuestTurninMessage message)
    {
        if (!IsCollectingActive() || message.QuestID <= 0)
            return;

        var questId = message.QuestID;
        EnqueueWork(() =>
        {
            var quest = TryFindQuestInTree(questId);
            if (quest != null)
            {
                _engine.RecordQuestModel(quest, accepted: false, turnedIn: true);
            }
        });
        if (_bot != null)
            SyncActiveQuests(_bot);
    }

    private void OnPlayerDeath(PlayerDeathMessage message)
    {
        if (!IsCollectingActive())
            return;

        EnqueueWork(() => _engine.RecordPlayerDeath());
    }

    private void OnCounterAttack(CounterAttackMessage message)
    {
        if (!IsCollectingActive())
            return;

        var faded = message.Faded;
        EnqueueWork(() => _engine.RecordCounterAttack(faded));
    }

    private void ScheduleMapSnapshot(int delayMs)
    {
        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0)
                    await Task.Delay(delayMs, token).ConfigureAwait(false);

                if (token.IsCancellationRequested || !IsCollectingActive())
                    return;

                var bot = _bot;
                if (bot == null || !bot.Player.LoggedIn || !bot.Map.Loaded)
                    return;

                var mapName = bot.Map.Name;
                var mapFile = bot.Map.FilePath;
                var cell = bot.Player.Cell;
                var pad = bot.Player.Pad;
                var cells = bot.Map.Cells;
                var monsters = bot.Monsters.MapMonsters;

                EnqueueWork(() => _engine.RecordMapSnapshot(mapName, mapFile, cell, pad, cells, monsters));
                CaptureLivePlayerSnapshot(bot);
                SyncActiveQuests(bot);
                SamplePlayerCount(bot);
            }
            catch
            {
                // Never propagate exceptions from background map snapshot
            }
        }, token);
    }

    private void SchedulePlayerStateRefresh(int delayMs)
    {
        if (Interlocked.CompareExchange(ref _playerRefreshPending, 1, 0) != 0)
            return;

        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0)
                    await Task.Delay(delayMs, token).ConfigureAwait(false);

                if (token.IsCancellationRequested || !IsCollectingActive())
                    return;

                var bot = _bot;
                if (bot is { Player.LoggedIn: true })
                    CaptureLivePlayerSnapshot(bot);
            }
            catch
            {
                // Ignore transient errors during class/item transitions
            }
            finally
            {
                Interlocked.Exchange(ref _playerRefreshPending, 0);
            }
        }, token);
    }

    private Skua.Core.Models.Quests.Quest? TryFindQuestInTree(int questId)
    {
        try
        {
            var tree = _bot?.Quests?.Tree;
            return tree?.FirstOrDefault(q => q != null && q.ID == questId);
        }
        catch
        {
            return null;
        }
    }

    private void EnqueueWork(Action action)
    {
        _workChannel?.Writer.TryWrite(action);
    }

    private async Task ProcessQueueLoopAsync(CancellationToken token)
    {
        var reader = _workChannel?.Reader;
        if (reader == null)
            return;

        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var work))
                {
                    try
                    {
                        work();
                    }
                    catch
                    {
                        // Swallow individual packet parsing errors to keep worker alive
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async Task PeriodicFlushLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var intervalSec = Math.Clamp(GetIntOption(OptFlushSeconds, 30), 10, 600);
                await Task.Delay(TimeSpan.FromSeconds(intervalSec), token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                    break;

                _engine.CollectCombatDetails = GetBoolOption(OptCombatDetails, true);
                _engine.SchemaSampler.Enabled = GetBoolOption(OptPacketSchemas, true);
                if (IsCollectingActive() && _bot is { Player.LoggedIn: true })
                {
                    CaptureLivePlayerSnapshot(_bot);
                    SyncActiveQuests(_bot);
                    SamplePlayerCount(_bot);
                    await Task.Delay(150, token).ConfigureAwait(false);
                }
                FlushSafe();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore transient timer errors
            }
        }
    }

    public GameDatabase? FlushSafe()
    {
        try
        {
            var store = _store ?? CreateStore();
            _store = store;

            if (_engine.SchemaSampler.HasPendingFlush)
            {
                try
                {
                    var primaryDir = Path.GetDirectoryName(store.PrimaryFilePath) ?? GetPrimaryDataDirectory();
                    var primarySchema = Path.Combine(primaryDir, "packet_schemas.json");
                    var mirrorSchemas = store.MirrorFilePaths
                        .Select(p => Path.GetDirectoryName(p))
                        .Where(d => !string.IsNullOrWhiteSpace(d))
                        .Select(d => Path.Combine(d!, "packet_schemas.json"));
                    _engine.SchemaSampler.FlushToFiles(primarySchema, mirrorSchemas);
                }
                catch { }
            }

            if (!_engine.HasPendingChanges())
                return null;

            var delta = _engine.TakeDeltaSnapshot();
            return store.FlushDelta(delta);
        }
        catch
        {
            return null;
        }
    }

    private void OnMenuButtonClicked()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var store = _store ?? CreateStore();
                _store = store;

                var db = FlushSafe() ?? store.LoadCurrent();
                var active = IsCollectingActive();
                var dataDir = Path.GetDirectoryName(store.PrimaryFilePath) ?? GetPrimaryDataDirectory();

                var totalKills = db.Monsters.Values.Sum(m => m.KillCount);
                var totalDrops = db.Monsters.Values.Sum(m => m.Drops.Values.Sum(d => d.DropEvents));

                var summary =
                    $"Status: {(active ? "ACTIVE (Collecting in background)" : "PAUSED")}\n\n" +
                    $"=== Accumulated Database (gamedata.json) ===\n" +
                    $"• Cataloged Maps: {db.Maps.Count}\n" +
                    $"• Cataloged Monsters: {db.Monsters.Count} ({totalKills} kills)\n" +
                    $"• Cataloged Items: {db.Items.Count} ({totalDrops} drops)\n" +
                    $"• Captured Shops: {db.Shops.Count}\n" +
                    $"• Captured Quests: {db.Quests.Count}\n" +
                    $"• Captured Classes: {db.Classes.Count} ({db.Enhancements.Count} enh, {db.Procs.Count} procs)\n" +
                    $"• Packet Schemas: {(_engine.SchemaSampler.Enabled ? $"ON ({_engine.SchemaSampler.TotalSamplesCaptured} session samples)" : "OFF")}\n\n" +
                    $"=== Current Session (PID {Environment.ProcessId}) ===\n" +
                    $"• Processed Packets: {_engine.SessionPacketsProcessed}\n" +
                    $"• Visited Maps: {_engine.SessionMapsVisited}\n" +
                    $"• Recorded Kills: {_engine.SessionKillsRecorded}\n" +
                    $"• Recorded Drops: {_engine.SessionDropsRecorded}\n\n" +
                    $"Primary File:\n{store.PrimaryFilePath}";

                Log($"[SkuaSim Collector] Maps={db.Maps.Count}, Monsters={db.Monsters.Count}, Kills={totalKills}, Items={db.Items.Count}, Shops={db.Shops.Count}, Quests={db.Quests.Count}, Classes={db.Classes.Count}, Enh={db.Enhancements.Count}, Procs={db.Procs.Count}");

                if (_bot == null)
                    return;

                var toggleLabel = _runtimePaused ? "Resume Collection" : "Pause Collection";
                var result = _bot.ShowMessageBox(
                    summary,
                    "SkuaSim Collector — Status",
                    "Open Folder",
                    "Export .zip",
                    toggleLabel,
                    "Close");

                if (result.Text == "Open Folder")
                {
                    Directory.CreateDirectory(dataDir);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dataDir,
                        UseShellExecute = true
                    });
                }
                else if (result.Text == "Export .zip")
                {
                    var exportDir = Path.Combine(dataDir, "exports");
                    var zipPath = store.ExportZipPackage(exportDir);
                    Log($"[SkuaSim Collector] Exported package: {zipPath}");
                    _bot.ShowMessageBox(
                        $"Data package exported successfully (anonymized, no personal data):\n\n{zipPath}",
                        "SkuaSim Collector — Export Complete");
                }
                else if (result.Text == toggleLabel)
                {
                    _runtimePaused = !_runtimePaused;
                    Log($"[SkuaSim Collector] Collection {(_runtimePaused ? "paused" : "resumed")}.");
                }
            }
            catch (Exception ex)
            {
                Log($"[SkuaSim Collector] Menu error: {ex.Message}");
            }
        });
    }

    private GameDatabaseStore CreateStore()
    {
        var primaryDir = GetPrimaryDataDirectory();
        var primaryFile = Path.Combine(primaryDir, "gamedata.json");

        var mirrors = new List<string>();
        var defaultMirror = GetDefaultWorkspaceMirrorPath();
        if (!string.IsNullOrWhiteSpace(defaultMirror))
            mirrors.Add(defaultMirror);

        var configuredMirror = GetStringOption(OptMirrorFile, string.Empty);
        if (!string.IsNullOrWhiteSpace(configuredMirror))
        {
            var path = configuredMirror.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? configuredMirror
                : Path.Combine(configuredMirror, "gamedata.json");
            mirrors.Add(path);
        }

        return new GameDatabaseStore(primaryFile, mirrors)
        {
            // Skua's QuestData.json improves the one-time v1 -> v2 migration (quest rewards vs. quest-required drops).
            QuestHintsProvider = () => QuestCatalogHints.FromQuestDataFile(FindQuestDataFile())
        };
    }

    private static string? FindQuestDataFile()
    {
        var candidates = new List<string>();
        try
        {
            if (!string.IsNullOrWhiteSpace(ClientFileSources.SkuaDIR))
                candidates.Add(Path.Combine(ClientFileSources.SkuaDIR, "QuestData.json"));
        }
        catch { }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(appData, "VibeSkua", "QuestData.json"));
        candidates.Add(Path.Combine(appData, "Skua", "QuestData.json"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string GetPrimaryDataDirectory()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(ClientFileSources.SkuaDIR))
                return Path.Combine(ClientFileSources.SkuaDIR, "SkuaSim");
        }
        catch { }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "VibeSkua", "SkuaSim");
    }

    private static string GetDefaultWorkspaceMirrorPath()
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var desktopFolder in new[] { "Área de Trabalho", "Desktop" })
            {
                var candidate = Path.Combine(userProfile, "OneDrive", desktopFolder, "SkuaSim", "data", "gamedata.json");
                var parent = Path.GetDirectoryName(Path.GetDirectoryName(candidate));
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    return candidate;
            }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var candidate2 = Path.Combine(desktop, "SkuaSim", "data", "gamedata.json");
            var parent2 = Path.GetDirectoryName(Path.GetDirectoryName(candidate2));
            if (!string.IsNullOrEmpty(parent2) && Directory.Exists(parent2))
                return candidate2;
        }
        catch { }

        return string.Empty;
    }

    private void CaptureLivePlayerSnapshot(IScriptInterface bot)
    {
        try
        {
            if (!bot.Player.LoggedIn)
                return;

            var uid = 0;
            try
            {
                uid = bot.Flash?.GetGameObject<int>("world.myAvatar.uid", 0) ?? 0;
            }
            catch
            {
                // Ignore Flash read errors during transitions
            }

            var currentClass = bot.Player.CurrentClass;
            var className = currentClass?.Name;
            var username = bot.Player.Username;
            var level = bot.Player.Level;
            var maxHp = bot.Player.MaxHealth;
            var stats = bot.Player.Stats;
            var skills = bot.Player.Skills;

            int weaponEnhPat = 0, weaponEnhLvl = 0, weaponProcId = 0;
            int classEnhPat = currentClass?.EnhancementPatternID ?? 0;
            int classEnhLvl = currentClass?.EnhancementLevel ?? 0;
            int helmEnhPat = 0, helmEnhLvl = 0;
            int capeEnhPat = 0, capeEnhLvl = 0;

            var items = bot.Inventory?.Items;
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item == null || !item.Equipped)
                        continue;

                    if (string.Equals(item.ItemGroup, "Weapon", StringComparison.OrdinalIgnoreCase))
                    {
                        weaponEnhPat = item.EnhancementPatternID;
                        weaponEnhLvl = item.EnhancementLevel;
                        weaponProcId = item.ProcID;
                    }
                    else if (string.Equals(item.CategoryString, "Class", StringComparison.OrdinalIgnoreCase))
                    {
                        classEnhPat = item.EnhancementPatternID;
                        classEnhLvl = item.EnhancementLevel;
                    }
                    else if (string.Equals(item.CategoryString, "Helm", StringComparison.OrdinalIgnoreCase))
                    {
                        helmEnhPat = item.EnhancementPatternID;
                        helmEnhLvl = item.EnhancementLevel;
                    }
                    else if (string.Equals(item.CategoryString, "Cape", StringComparison.OrdinalIgnoreCase))
                    {
                        capeEnhPat = item.EnhancementPatternID;
                        capeEnhLvl = item.EnhancementLevel;
                    }
                }
            }

            EnqueueWork(() => _engine.RecordLivePlayerState(
                uid,
                className,
                level,
                maxHp,
                stats,
                skills,
                weaponEnhPat,
                weaponEnhLvl,
                classEnhPat,
                classEnhLvl,
                helmEnhPat,
                helmEnhLvl,
                capeEnhPat,
                capeEnhLvl,
                username,
                weaponProcId));
        }
        catch
        {
            // Never propagate exceptions from background stat sampling
        }
    }

    private bool IsCollectingActive()
    {
        return !_runtimePaused && GetBoolOption(OptEnabled, true);
    }

    private bool GetBoolOption(string name, bool fallback)
    {
        try
        {
            var opt = _container?.OptionContainer?.Options?.FirstOrDefault(o => o.Name == name);
            if (opt != null && _container?.OptionContainer != null)
                return _container.OptionContainer.Get<bool>(opt);
        }
        catch { }
        return fallback;
    }

    private int GetIntOption(string name, int fallback)
    {
        try
        {
            var opt = _container?.OptionContainer?.Options?.FirstOrDefault(o => o.Name == name);
            if (opt != null && _container?.OptionContainer != null)
                return _container.OptionContainer.Get<int>(opt);
        }
        catch { }
        return fallback;
    }

    private string GetStringOption(string name, string fallback)
    {
        try
        {
            var opt = _container?.OptionContainer?.Options?.FirstOrDefault(o => o.Name == name);
            if (opt != null && _container?.OptionContainer != null)
                return _container.OptionContainer.Get<string>(opt) ?? fallback;
        }
        catch { }
        return fallback;
    }

    private void Log(string message)
    {
        try
        {
            _bot?.Log(message);
        }
        catch { }
    }
}
