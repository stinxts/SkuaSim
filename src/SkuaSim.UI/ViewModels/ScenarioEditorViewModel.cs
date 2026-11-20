using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkuaSim.Core.Runtime;
using SkuaSim.Core.Scenarios;
using SkuaSim.UI.Models;
using SkuaSim.UI.Services;

namespace SkuaSim.UI.ViewModels;

public partial class ScenarioEditorViewModel : ViewModelBase
{
    private Scenario _baseScenario = new();
    private IFileDialogService? _dialogService;
    private Func<string>? _scriptPathProvider;
    private SimulationHostService? _hostService;
    private Func<string, Task>? _onSavedCallback;
    private Action<double>? _onVirtualMinutesChangedCallback;
    private bool _suppressDirty;
    private bool _suppressBulkSync;

    public ScenarioEditorViewModel()
    {
        foreach (var slot in new[] { "Weapon", "Armor", "Helm", "Cape", "Pet" })
        {
            var slotItems = AqwBoostGearCatalog.GetItemsForSlot(slot);
            EquipmentBoosts.Add(new GearSlotBoostEditorRow
            {
                Slot = slot,
                AvailableItems = slotItems,
                SelectedItem = slotItems.FirstOrDefault(),
                OnChanged = HandleSubItemChanged
            });
        }

        foreach (var awe in EnhancementUnlockCatalog.AweEnhancements)
        {
            AweEnhancements.Add(new EnhancementCheckItem
            {
                Name = awe.Name,
                Group = awe.Group,
                Slot = awe.Slot,
                QuestId = awe.QuestId,
                OnChanged = HandleAweItemChanged
            });
        }

        foreach (var forge in EnhancementUnlockCatalog.ForgeEnhancements)
        {
            var item = new EnhancementCheckItem
            {
                Name = forge.Name,
                Group = forge.Group,
                Slot = forge.Slot,
                QuestId = forge.QuestId,
                OnChanged = HandleForgeItemChanged
            };
            AllForgeEnhancements.Add(item);
            switch (forge.Group)
            {
                case "ForgeWeapon": ForgeWeaponEnhancements.Add(item); break;
                case "ForgeHelm": ForgeHelmEnhancements.Add(item); break;
                case "ForgeCape": ForgeCapeEnhancements.Add(item); break;
            }
        }
    }

    public ObservableCollection<ClassCatalogOption> KnownClasses { get; } = new();
    public ObservableCollection<GearSlotBoostEditorRow> EquipmentBoosts { get; } = new();
    public ObservableCollection<EnhancementCheckItem> AweEnhancements { get; } = new();
    public ObservableCollection<EnhancementCheckItem> AllForgeEnhancements { get; } = new();
    public ObservableCollection<EnhancementCheckItem> ForgeWeaponEnhancements { get; } = new();
    public ObservableCollection<EnhancementCheckItem> ForgeHelmEnhancements { get; } = new();
    public ObservableCollection<EnhancementCheckItem> ForgeCapeEnhancements { get; } = new();
    public ObservableCollection<ScriptOptionEditorRow> ScriptOptions { get; } = new();
    public ObservableCollection<ScriptOptionCategoryGroup> ScriptOptionCategories { get; } = new();

    public IReadOnlyList<string> DurationUnitOptions { get; } = new[] { "Minutes", "Hours", "Days", "Weeks", "Unlimited" };

    [ObservableProperty] public partial string LoadedFilePath { get; set; } = "";
    [ObservableProperty] public partial string EditorStatusText { get; set; } = "Default scenario (AQW StatEngine auto)";
    [ObservableProperty] public partial bool IsDirty { get; set; }
    [ObservableProperty] public partial bool IsInspectingOptions { get; set; }
    [ObservableProperty] public partial bool HasAnyScriptOptions { get; set; }
    [ObservableProperty] public partial bool HasMultipleOptionCategories { get; set; }
    [ObservableProperty] public partial bool HasSavedSkuaCfg { get; set; }
    [ObservableProperty] public partial string LastOptionsStorage { get; set; } = "";
    [ObservableProperty] public partial string ScriptOptionsSummary { get; set; } = "Click 'Inspect Script' to load the exact Skua Bot.Config options for the selected .cs script.";

    // General
    [ObservableProperty] public partial string ScenarioName { get; set; } = "New Scenario";
    [ObservableProperty] public partial string ScenarioDescription { get; set; } = "";
    [ObservableProperty] public partial string SeedText { get; set; } = "42";

    // Player & Class (backed by Collector databank & authentic AQW StatEngine)
    [ObservableProperty] public partial string Username { get; set; } = "SimPlayer";
    [ObservableProperty] public partial string LevelText { get; set; } = "100";
    [ObservableProperty] public partial string ClassName { get; set; } = "ArchPaladin";
    [ObservableProperty] public partial ClassCatalogOption? SelectedKnownClass { get; set; }
    [ObservableProperty] public partial string ClassRankText { get; set; } = "10";
    [ObservableProperty] public partial string GoldText { get; set; } = "1000000";
    [ObservableProperty] public partial string CoinsText { get; set; } = "0";
    [ObservableProperty] public partial bool IsMember { get; set; } = true;
    [ObservableProperty] public partial string InventorySlotsText { get; set; } = "200";
    [ObservableProperty] public partial string BankSlotsText { get; set; } = "600";

    // Live Class + Boost + Enhancement Real-Stats Preview
    [ObservableProperty] public partial string ClassSourceBadge { get; set; } = "ARCHETYPE";
    [ObservableProperty] public partial IBrush ClassSourceBadgeBackground { get; set; } = DiagnosticPalette.ApiBadgeBg;
    [ObservableProperty] public partial string StatsPreviewLine1 { get; set; } = "Real stats preview updates automatically from databank + enhancements.";
    [ObservableProperty] public partial string StatsPreviewLine2 { get; set; } = "";

    // Location & Limits
    [ObservableProperty] public partial string StartMap { get; set; } = "battleon";
    [ObservableProperty] public partial string StartCell { get; set; } = "Enter";
    [ObservableProperty] public partial string StartPad { get; set; } = "Spawn";
    [ObservableProperty] public partial string VirtualDurationAmountText { get; set; } = "6";
    [ObservableProperty] public partial string VirtualDurationUnit { get; set; } = "Hours";
    [ObservableProperty] public partial bool IsVirtualDurationAmountEnabled { get; set; } = true;
    [ObservableProperty] public partial string MaxRealSecondsText { get; set; } = "0";
    [ObservableProperty] public partial string StallRealSecondsText { get; set; } = "15";
    [ObservableProperty] public partial string MaxNoProgressMinutesText { get; set; } = "60";

    // Active 2x Consumable & Server Boosts
    [ObservableProperty] public partial bool XpBoost { get; set; }
    [ObservableProperty] public partial bool ClassPointsBoost { get; set; }
    [ObservableProperty] public partial bool GoldBoost { get; set; }
    [ObservableProperty] public partial bool RepBoost { get; set; }
    [ObservableProperty] public partial bool ServerBoost { get; set; }

    // Master & Per-Category checkboxes for Awe & Forge
    [ObservableProperty] public partial bool AllAweSelected { get; set; }
    [ObservableProperty] public partial bool AllForgeSelected { get; set; }
    [ObservableProperty] public partial bool AllForgeWeaponSelected { get; set; }
    [ObservableProperty] public partial bool AllForgeHelmSelected { get; set; }
    [ObservableProperty] public partial bool AllForgeCapeSelected { get; set; }

    public void AttachServices(
        IFileDialogService dialogService,
        SimulationHostService? hostService,
        Func<string> scriptPathProvider,
        Func<string, Task> onSavedCallback,
        Action<double>? onVirtualMinutesChangedCallback = null)
    {
        _dialogService = dialogService;
        _hostService = hostService;
        _scriptPathProvider = scriptPathProvider;
        _onSavedCallback = onSavedCallback;
        _onVirtualMinutesChangedCallback = onVirtualMinutesChangedCallback;
        RefreshKnownClasses(forceReload: false);
    }

    public void RefreshKnownClasses(bool forceReload = false)
    {
        if (_hostService == null)
            return;

        var classes = _hostService.DiscoverKnownClasses(forceReload);
        _suppressDirty = true;
        try
        {
            KnownClasses.Clear();
            foreach (var c in classes)
                KnownClasses.Add(c);
            SyncSelectedKnownClassFromName();
        }
        finally
        {
            _suppressDirty = false;
        }
        RefreshStatsPreview();
    }

    public double GetCurrentVirtualMinutes()
    {
        if (string.Equals(VirtualDurationUnit, "Unlimited", StringComparison.OrdinalIgnoreCase))
            return 0;

        var amount = Math.Max(0.1, ParseDoubleOr(VirtualDurationAmountText, 360));
        return VirtualDurationUnit switch
        {
            "Hours" => amount * 60.0,
            "Days" => amount * 1440.0,
            "Weeks" => amount * 10080.0,
            _ => amount
        };
    }

    public void SetVirtualMinutesFromToolbar(double minutes)
    {
        _suppressDirty = true;
        try
        {
            ApplyVirtualMinutesToFields(minutes);
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    private void ApplyVirtualMinutesToFields(double minutes)
    {
        if (minutes <= 0)
        {
            VirtualDurationUnit = "Unlimited";
            VirtualDurationAmountText = "0";
            IsVirtualDurationAmountEnabled = false;
            return;
        }

        IsVirtualDurationAmountEnabled = true;
        if (minutes >= 10080 && Math.Abs(minutes % 10080) < 0.01)
        {
            VirtualDurationUnit = "Weeks";
            VirtualDurationAmountText = (minutes / 10080.0).ToString("0.##", CultureInfo.InvariantCulture);
        }
        else if (minutes >= 1440 && Math.Abs(minutes % 1440) < 0.01)
        {
            VirtualDurationUnit = "Days";
            VirtualDurationAmountText = (minutes / 1440.0).ToString("0.##", CultureInfo.InvariantCulture);
        }
        else if (minutes >= 60 && Math.Abs(minutes % 60) < 0.01)
        {
            VirtualDurationUnit = "Hours";
            VirtualDurationAmountText = (minutes / 60.0).ToString("0.##", CultureInfo.InvariantCulture);
        }
        else
        {
            VirtualDurationUnit = "Minutes";
            VirtualDurationAmountText = minutes.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    public void LoadFromScenario(Scenario scenario, string? sourcePath = null)
    {
        _suppressDirty = true;
        _suppressBulkSync = true;
        try
        {
            _baseScenario = scenario;
            LoadedFilePath = sourcePath ?? scenario.SourcePath ?? "";

            ScenarioName = scenario.Name ?? "";
            ScenarioDescription = scenario.Description ?? "";
            SeedText = (scenario.Seed ?? 42).ToString(CultureInfo.InvariantCulture);

            var p = scenario.Player;
            Username = p.Username ?? "SimPlayer";
            LevelText = p.Level.ToString(CultureInfo.InvariantCulture);
            ClassName = string.IsNullOrWhiteSpace(p.Class) ? "ArchPaladin" : p.Class.Trim();
            SyncSelectedKnownClassFromName();
            ClassRankText = p.ClassRank.ToString(CultureInfo.InvariantCulture);
            GoldText = p.Gold.ToString(CultureInfo.InvariantCulture);
            CoinsText = p.Coins.ToString(CultureInfo.InvariantCulture);
            IsMember = p.IsMember;
            InventorySlotsText = p.InventorySlots.ToString(CultureInfo.InvariantCulture);
            BankSlotsText = p.BankSlots.ToString(CultureInfo.InvariantCulture);

            StartMap = scenario.Location.Map ?? "battleon";
            StartCell = scenario.Location.Cell ?? "Enter";
            StartPad = scenario.Location.Pad ?? "Spawn";

            ApplyVirtualMinutesToFields(scenario.Limits.MaxVirtualMinutes);
            MaxRealSecondsText = scenario.Limits.MaxRealSeconds.ToString(CultureInfo.InvariantCulture);
            StallRealSecondsText = scenario.Limits.StallRealSeconds.ToString(CultureInfo.InvariantCulture);
            MaxNoProgressMinutesText = scenario.Limits.MaxNoProgressMinutes.ToString(CultureInfo.InvariantCulture);

            // Boosts
            XpBoost = scenario.Boosts.XpBoost;
            ClassPointsBoost = scenario.Boosts.ClassPointsBoost;
            GoldBoost = scenario.Boosts.GoldBoost;
            RepBoost = scenario.Boosts.RepBoost;
            ServerBoost = scenario.Boosts.ServerBoost;

            foreach (var row in EquipmentBoosts)
            {
                var found = scenario.Boosts.EquipmentBoosts.FirstOrDefault(b =>
                    b.Slot.Equals(row.Slot, StringComparison.OrdinalIgnoreCase));
                row.SelectedItem = ResolveGearCatalogSelection(row, found);
            }

            // Awe & Forge checklists
            var unlockedAwe = new HashSet<string>(scenario.Enhancements.UnlockedAwe, StringComparer.OrdinalIgnoreCase);
            var unlockedForge = new HashSet<string>(scenario.Enhancements.UnlockedForge, StringComparer.OrdinalIgnoreCase);

            foreach (var item in AweEnhancements)
                item.IsChecked = unlockedAwe.Contains(item.Name);
            foreach (var item in AllForgeEnhancements)
                item.IsChecked = unlockedForge.Contains(item.Name);

            AllAweSelected = AweEnhancements.Count > 0 && AweEnhancements.All(i => i.IsChecked);
            SyncForgeCategoryCheckStates();

            ApplyScenarioOptionsToEditorRows(scenario.Options);

            IsDirty = false;
            EditorStatusText = string.IsNullOrWhiteSpace(LoadedFilePath)
                ? "Editing in-memory scenario"
                : $"Loaded {Path.GetFileName(LoadedFilePath)}";
        }
        finally
        {
            _suppressBulkSync = false;
            _suppressDirty = false;
        }

        RefreshStatsPreview();
    }

    public Scenario BuildScenario(int? toolbarSeedOverride = null, double? toolbarVirtualMinutesOverride = null)
    {
        // Round-trip clone base scenario to keep untouched advanced sections (Maps, World, BotOptions, Factions, Quests, etc.)
        var clone = JsonSerializer.Deserialize<Scenario>(_baseScenario.ToJson(), Scenario.JsonOptions) ?? new Scenario();
        clone.SourcePath = string.IsNullOrWhiteSpace(LoadedFilePath) ? null : LoadedFilePath;

        clone.Name = string.IsNullOrWhiteSpace(ScenarioName) ? "Custom Scenario" : ScenarioName.Trim();
        clone.Description = ScenarioDescription?.Trim() ?? "";
        clone.Seed = toolbarSeedOverride ?? ParseIntOr(SeedText, 42);

        clone.Player.Username = string.IsNullOrWhiteSpace(Username) ? "SimPlayer" : Username.Trim();
        clone.Player.Level = Math.Clamp(ParseIntOr(LevelText, 100), 1, 100);
        clone.Player.Class = string.IsNullOrWhiteSpace(ClassName) ? null : ClassName.Trim();
        clone.Player.ClassRank = Math.Clamp(ParseIntOr(ClassRankText, 10), 1, 10);
        clone.Player.Gold = Math.Max(0, ParseLongOr(GoldText, 1_000_000));
        clone.Player.Coins = Math.Max(0, ParseIntOr(CoinsText, 0));
        clone.Player.IsMember = IsMember;
        clone.Player.Invulnerable = false;
        clone.Player.InventorySlots = Math.Max(10, ParseIntOr(InventorySlotsText, 200));
        clone.Player.BankSlots = Math.Max(10, ParseIntOr(BankSlotsText, 600));
        // Always compute real AQW player stats via StatEngine (no artificial HP/DPS/Regen overrides)
        clone.Player.MaxHealth = null;
        clone.Player.DamagePerSecond = null;
        clone.Player.RegenPercentPerSecond = null;

        clone.Location.Map = string.IsNullOrWhiteSpace(StartMap) ? "battleon" : StartMap.Trim();
        clone.Location.Cell = string.IsNullOrWhiteSpace(StartCell) ? "Enter" : StartCell.Trim();
        clone.Location.Pad = string.IsNullOrWhiteSpace(StartPad) ? "Spawn" : StartPad.Trim();

        clone.Limits.MaxVirtualMinutes = toolbarVirtualMinutesOverride ?? GetCurrentVirtualMinutes();
        clone.Limits.MaxRealSeconds = ParseRealSecondsOrUnlimited(MaxRealSecondsText);
        clone.Limits.StallRealSeconds = Math.Max(2, ParseDoubleOr(StallRealSecondsText, 15));
        clone.Limits.MaxNoProgressMinutes = Math.Max(0, ParseDoubleOr(MaxNoProgressMinutesText, 60));

        // Boosts & Real AQW Boosted Equipment
        clone.Boosts = new BoostsSetup
        {
            XpBoost = XpBoost,
            ClassPointsBoost = ClassPointsBoost,
            GoldBoost = GoldBoost,
            RepBoost = RepBoost,
            ServerBoost = ServerBoost,
            EquipmentBoosts = EquipmentBoosts
                .Where(r => r.SelectedItem != null && !string.IsNullOrWhiteSpace(r.SelectedItem.Meta))
                .Select(r => new GearSlotBoostSetup
                {
                    Slot = r.Slot,
                    ItemName = r.SelectedItem!.Name,
                    Meta = r.SelectedItem.Meta
                })
                .ToList()
        };

        // Unlocked Awe & Forge Enhancements
        clone.Enhancements = new EnhancementsSetup
        {
            UnlockedAwe = AweEnhancements.Where(i => i.IsChecked).Select(i => i.Name).ToList(),
            UnlockedForge = AllForgeEnhancements.Where(i => i.IsChecked).Select(i => i.Name).ToList()
        };

        // Script Options (Bot.Config)
        var options = new Dictionary<string, JsonElement>(clone.Options, StringComparer.OrdinalIgnoreCase);
        var editableOpts = ScriptOptions.Where(o => !o.IsHeader && !string.IsNullOrWhiteSpace(o.Name)).ToList();
        var duplicateNames = editableOpts
            .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var opt in editableOpts)
        {
            var serialized = SerializeOptionValue(opt);
            options[opt.Name] = serialized;
            if (duplicateNames.Contains(opt.Name) && !string.IsNullOrWhiteSpace(opt.Category))
                options[$"{opt.Category}:{opt.Name}"] = serialized;
        }
        clone.Options = options;
        return clone;
    }

    [RelayCommand]
    private void NewStarterTemplate()
    {
        var starter = new Scenario
        {
            Name = "Lv 1 Starter Mage",
            Description = "Fresh level 1 non-member account using AQW StatEngine defaults.",
            Seed = 42,
            Player = new PlayerSetup
            {
                Username = "StarterMage",
                Level = 1,
                Gold = 0,
                Coins = 0,
                IsMember = false,
                Class = "Mage",
                ClassRank = 1,
                InventorySlots = 40,
                BankSlots = 30
            }
        };
        LoadFromScenario(starter, "");
        _onVirtualMinutesChangedCallback?.Invoke(starter.Limits.MaxVirtualMinutes);
        MarkDirty("Created Lv 1 Starter template (unsaved)");
    }

    [RelayCommand]
    private void NewEndgameTemplate()
    {
        var endgame = new Scenario
        {
            Name = "Lv 100 Endgame Meta",
            Description = "Level 100 character with All Awe + All Forge enhancements and real AQW endgame boost loadout.",
            Seed = 42,
            Player = new PlayerSetup
            {
                Username = "EndgameHero",
                Level = 100,
                Gold = 5_000_000,
                Coins = 2_000,
                IsMember = true,
                Class = KnownClasses.Any(c => c.Name.Equals("Void Highlord", StringComparison.OrdinalIgnoreCase))
                    ? "Void Highlord"
                    : "ArchPaladin",
                ClassRank = 10
            },
            Boosts = new BoostsSetup
            {
                XpBoost = true,
                ClassPointsBoost = true,
                GoldBoost = true,
                RepBoost = true,
                EquipmentBoosts = new List<GearSlotBoostSetup>
                {
                    BuildGearSetup("Weapon", "Necrotic Sword of Doom"),
                    BuildGearSetup("Armor", "Radiant Goddess of War"),
                    BuildGearSetup("Helm", "Head of the Legion Beast"),
                    BuildGearSetup("Cape", "Awescended Omni Wings"),
                    BuildGearSetup("Pet", "Polly Roger")
                }
            },
            Enhancements = new EnhancementsSetup
            {
                UnlockedAwe = EnhancementUnlockCatalog.AweEnhancements.Select(e => e.Name).ToList(),
                UnlockedForge = EnhancementUnlockCatalog.ForgeEnhancements.Select(e => e.Name).ToList()
            }
        };
        LoadFromScenario(endgame, "");
        _onVirtualMinutesChangedCallback?.Invoke(endgame.Limits.MaxVirtualMinutes);
        MarkDirty("Created Lv 100 Endgame Meta template (unsaved)");
    }

    [RelayCommand]
    private void ApplyEndgameGearBoosts()
    {
        _suppressDirty = true;
        try
        {
            SetSlotGearItem("Weapon", "Necrotic Sword of Doom");
            SetSlotGearItem("Armor", "Radiant Goddess of War");
            SetSlotGearItem("Helm", "Head of the Legion Beast");
            SetSlotGearItem("Cape", "Awescended Omni Wings");
            SetSlotGearItem("Pet", "Polly Roger");
        }
        finally
        {
            _suppressDirty = false;
        }
        MarkDirty("Applied AQW Endgame gear (NSoD + RGoW + HotLB + Awescended + Polly Roger)");
        RefreshStatsPreview();
    }

    [RelayCommand]
    private void ClearGearBoosts()
    {
        _suppressDirty = true;
        try
        {
            foreach (var row in EquipmentBoosts)
                row.SelectedItem = row.AvailableItems.FirstOrDefault();
        }
        finally
        {
            _suppressDirty = false;
        }
        MarkDirty("Cleared all equipment boosts");
        RefreshStatsPreview();
    }

    [RelayCommand]
    private async Task InspectScriptOptionsAsync()
    {
        if (_hostService == null || !_hostService.IsReady)
        {
            ScriptOptionsSummary = "Configure a Skua / VibeSkua folder first to inspect script options.";
            return;
        }

        var scriptPath = _scriptPathProvider?.Invoke() ?? "";
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            ScriptOptionsSummary = "Select an existing .cs script in the toolbar first.";
            return;
        }

        IsInspectingOptions = true;
        ScriptOptionsSummary = $"Compiling & inspecting {Path.GetFileName(scriptPath)}...";
        try
        {
            var result = await _hostService.InspectScriptAsync(scriptPath);
            if (!result.Success)
            {
                ScriptOptionsSummary = $"Inspection failed: {result.ErrorMessage ?? "compile error"}";
                return;
            }

            var existingValues = BuildScenario().Options;

            _suppressDirty = true;
            try
            {
                ScriptOptions.Clear();
                ScriptOptionCategories.Clear();

                var groups = new Dictionary<string, ScriptOptionCategoryGroup>(StringComparer.OrdinalIgnoreCase);

                foreach (var desc in result.Options)
                {
                    var isBool = !desc.IsHeader && IsBooleanTypeName(desc.ValueType);
                    var isChoice = !desc.IsHeader && !isBool && desc.AllowedValues.Count > 0;
                    var cleanDesc = CleanOptionDescription(desc.Description);

                    var row = new ScriptOptionEditorRow
                    {
                        Name = desc.Name,
                        DisplayName = string.IsNullOrWhiteSpace(desc.DisplayName) ? desc.Name : desc.DisplayName,
                        Category = string.IsNullOrWhiteSpace(desc.Category) ? "Options" : desc.Category,
                        Description = cleanDesc,
                        ValueType = desc.ValueType,
                        DefaultValue = desc.DefaultValue,
                        IsHeader = desc.IsHeader,
                        IsBool = isBool,
                        IsChoice = isChoice,
                        OnChanged = HandleOptionRowChanged
                    };

                    foreach (var choice in desc.AllowedValues)
                        row.AllowedValues.Add(choice);

                    if (!row.IsHeader)
                    {
                        var scopedKey = $"{row.Category}:{row.Name}";
                        if (existingValues.TryGetValue(scopedKey, out var scopedEl))
                            ApplyJsonElementToOptionRow(row, scopedEl);
                        else if (existingValues.TryGetValue(row.Name, out var scenarioEl))
                            ApplyJsonElementToOptionRow(row, scenarioEl);
                        else
                            ApplyDefaultToOptionRow(row, desc.DefaultValue);
                    }

                    ScriptOptions.Add(row);

                    if (!groups.TryGetValue(row.Category, out var group))
                    {
                        group = new ScriptOptionCategoryGroup { Category = row.Category };
                        groups[row.Category] = group;
                        ScriptOptionCategories.Add(group);
                    }
                    group.Options.Add(row);
                }

                LastOptionsStorage = result.OptionsStorage ?? "";
                HasMultipleOptionCategories = ScriptOptionCategories.Count > 1;
                HasAnyScriptOptions = ScriptOptions.Any(o => !o.IsHeader);
                HasSavedSkuaCfg = HasAnyScriptOptions &&
                                  !string.IsNullOrWhiteSpace(LastOptionsStorage) &&
                                  _hostService.TryLoadSkuaCfgOptions(LastOptionsStorage) != null;
            }
            finally
            {
                _suppressDirty = false;
            }

            var editableCount = ScriptOptions.Count(o => !o.IsHeader);
            ScriptOptionsSummary = ScriptOptions.Count == 0
                ? $"No configurable Bot.Config options declared in {Path.GetFileName(scriptPath)}."
                : string.IsNullOrWhiteSpace(result.OptionsStorage)
                    ? $"{result.ScriptClass} · {editableCount} option(s)"
                    : $"{result.ScriptClass} · storage '{result.OptionsStorage}' · {editableCount} option(s)";
        }
        catch (Exception ex)
        {
            ScriptOptionsSummary = $"Inspection error: {ex.Message}";
        }
        finally
        {
            IsInspectingOptions = false;
        }
    }

    [RelayCommand]
    private void ResetScriptOptionsToDefaults()
    {
        if (!HasAnyScriptOptions)
            return;

        _suppressDirty = true;
        try
        {
            foreach (var row in ScriptOptions)
            {
                if (!row.IsHeader)
                    ApplyDefaultToOptionRow(row, row.DefaultValue);
            }
        }
        finally
        {
            _suppressDirty = false;
        }
        MarkDirty("Reset script options to script defaults");
    }

    [RelayCommand]
    private void LoadSavedSkuaOptions()
    {
        if (_hostService == null || string.IsNullOrWhiteSpace(LastOptionsStorage))
            return;

        var cfg = _hostService.TryLoadSkuaCfgOptions(LastOptionsStorage);
        if (cfg == null || cfg.Count == 0)
        {
            ScriptOptionsSummary = $"No saved Skua .cfg found for storage '{LastOptionsStorage}'.";
            return;
        }

        var applied = 0;
        _suppressDirty = true;
        try
        {
            foreach (var row in ScriptOptions)
            {
                if (row.IsHeader)
                    continue;

                if (cfg.TryGetValue($"{row.Category}:{row.Name}", out var raw) ||
                    cfg.TryGetValue(row.Name, out raw))
                {
                    ApplyRawStringToOptionRow(row, raw);
                    applied++;
                }
            }
        }
        finally
        {
            _suppressDirty = false;
        }

        MarkDirty($"Imported {applied} option(s) from Skua '{LastOptionsStorage}.cfg'");
    }

    [RelayCommand]
    private async Task SaveScenarioAsync()
    {
        if (string.IsNullOrWhiteSpace(LoadedFilePath))
        {
            await SaveScenarioAsAsync();
            return;
        }

        await WriteScenarioToPathAsync(LoadedFilePath);
    }

    [RelayCommand]
    private async Task SaveScenarioAsAsync()
    {
        if (_dialogService == null)
            return;

        var defaultName = string.IsNullOrWhiteSpace(ScenarioName)
            ? "custom_scenario.json"
            : $"{SanitizeFileName(ScenarioName)}.json";

        var initialDir = !string.IsNullOrWhiteSpace(LoadedFilePath)
            ? Path.GetDirectoryName(LoadedFilePath)
            : Path.Combine(Directory.GetCurrentDirectory(), "scenarios");

        var path = await _dialogService.PickSaveFileAsync(
            "Save Scenario As",
            initialDir,
            defaultName,
            "json",
            new[] { ("Scenario JSON", new[] { "*.json" }), ("All Files", new[] { "*.*" }) });

        if (!string.IsNullOrWhiteSpace(path))
            await WriteScenarioToPathAsync(path);
    }

    private async Task WriteScenarioToPathAsync(string path)
    {
        try
        {
            var built = BuildScenario();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, built.ToJson());

            _baseScenario = built;
            LoadedFilePath = Path.GetFullPath(path);
            IsDirty = false;
            EditorStatusText = $"Saved {Path.GetFileName(path)}";

            if (_onSavedCallback != null)
                await _onSavedCallback(LoadedFilePath);
        }
        catch (Exception ex)
        {
            EditorStatusText = $"Save failed: {ex.Message}";
        }
    }

    partial void OnSelectedKnownClassChanged(ClassCatalogOption? value)
    {
        if (_suppressDirty || value == null)
            return;
        ClassName = value.Name;
    }

    partial void OnClassNameChanged(string value)
    {
        if (!_suppressDirty)
        {
            _suppressDirty = true;
            try { SyncSelectedKnownClassFromName(); }
            finally { _suppressDirty = false; }
            MarkDirty();
        }
        RefreshStatsPreview();
    }

    partial void OnLevelTextChanged(string value) { MarkDirty(); RefreshStatsPreview(); }
    partial void OnClassRankTextChanged(string value) { MarkDirty(); RefreshStatsPreview(); }
    partial void OnXpBoostChanged(bool value) { MarkDirty(); RefreshStatsPreview(); }
    partial void OnClassPointsBoostChanged(bool value) { MarkDirty(); RefreshStatsPreview(); }
    partial void OnGoldBoostChanged(bool value) { MarkDirty(); RefreshStatsPreview(); }
    partial void OnRepBoostChanged(bool value) { MarkDirty(); RefreshStatsPreview(); }
    partial void OnServerBoostChanged(bool value) { MarkDirty(); RefreshStatsPreview(); }

    partial void OnVirtualDurationUnitChanged(string value)
    {
        IsVirtualDurationAmountEnabled = !string.Equals(value, "Unlimited", StringComparison.OrdinalIgnoreCase);
        if (!_suppressDirty)
        {
            MarkDirty();
            _onVirtualMinutesChangedCallback?.Invoke(GetCurrentVirtualMinutes());
        }
    }

    partial void OnVirtualDurationAmountTextChanged(string value)
    {
        if (!_suppressDirty)
        {
            MarkDirty();
            _onVirtualMinutesChangedCallback?.Invoke(GetCurrentVirtualMinutes());
        }
    }

    partial void OnAllAweSelectedChanged(bool value)
    {
        if (_suppressBulkSync)
            return;
        _suppressBulkSync = true;
        try
        {
            foreach (var item in AweEnhancements)
                item.IsChecked = value;
        }
        finally
        {
            _suppressBulkSync = false;
        }
        MarkDirty();
        RefreshStatsPreview();
    }

    partial void OnAllForgeSelectedChanged(bool value)
    {
        if (_suppressBulkSync)
            return;
        _suppressBulkSync = true;
        try
        {
            foreach (var item in AllForgeEnhancements)
                item.IsChecked = value;
            AllForgeWeaponSelected = value;
            AllForgeHelmSelected = value;
            AllForgeCapeSelected = value;
        }
        finally
        {
            _suppressBulkSync = false;
        }
        MarkDirty();
        RefreshStatsPreview();
    }

    partial void OnAllForgeWeaponSelectedChanged(bool value) => SetForgeCategoryChecked(ForgeWeaponEnhancements, value);
    partial void OnAllForgeHelmSelectedChanged(bool value) => SetForgeCategoryChecked(ForgeHelmEnhancements, value);
    partial void OnAllForgeCapeSelectedChanged(bool value) => SetForgeCategoryChecked(ForgeCapeEnhancements, value);

    private void SetForgeCategoryChecked(ObservableCollection<EnhancementCheckItem> categoryItems, bool value)
    {
        if (_suppressBulkSync)
            return;
        _suppressBulkSync = true;
        try
        {
            foreach (var item in categoryItems)
                item.IsChecked = value;
            SyncForgeCategoryCheckStates();
        }
        finally
        {
            _suppressBulkSync = false;
        }
        MarkDirty();
        RefreshStatsPreview();
    }

    private void HandleAweItemChanged()
    {
        if (_suppressBulkSync)
            return;
        _suppressBulkSync = true;
        try
        {
            AllAweSelected = AweEnhancements.Count > 0 && AweEnhancements.All(i => i.IsChecked);
        }
        finally
        {
            _suppressBulkSync = false;
        }
        MarkDirty();
        RefreshStatsPreview();
    }

    private void HandleForgeItemChanged()
    {
        if (_suppressBulkSync)
            return;
        _suppressBulkSync = true;
        try
        {
            SyncForgeCategoryCheckStates();
        }
        finally
        {
            _suppressBulkSync = false;
        }
        MarkDirty();
        RefreshStatsPreview();
    }

    private void SyncForgeCategoryCheckStates()
    {
        AllForgeWeaponSelected = ForgeWeaponEnhancements.Count > 0 && ForgeWeaponEnhancements.All(i => i.IsChecked);
        AllForgeHelmSelected = ForgeHelmEnhancements.Count > 0 && ForgeHelmEnhancements.All(i => i.IsChecked);
        AllForgeCapeSelected = ForgeCapeEnhancements.Count > 0 && ForgeCapeEnhancements.All(i => i.IsChecked);
        AllForgeSelected = AllForgeEnhancements.Count > 0 && AllForgeEnhancements.All(i => i.IsChecked);
    }

    private void HandleSubItemChanged()
    {
        if (_suppressDirty)
            return;
        MarkDirty();
        RefreshStatsPreview();
    }

    private void HandleOptionRowChanged()
    {
        if (_suppressDirty)
            return;
        MarkDirty();
    }

    private void SyncSelectedKnownClassFromName()
    {
        var match = KnownClasses.FirstOrDefault(c => c.Name.Equals(ClassName?.Trim(), StringComparison.OrdinalIgnoreCase));
        SelectedKnownClass = match;
    }

    private void SetSlotGearItem(string slot, string itemName)
    {
        var row = EquipmentBoosts.FirstOrDefault(r => r.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));
        if (row == null)
            return;
        row.SelectedItem = row.AvailableItems.FirstOrDefault(i => i.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                           ?? row.AvailableItems.FirstOrDefault();
    }

    private static GearSlotBoostSetup BuildGearSetup(string slot, string itemName)
    {
        var catalog = AqwBoostGearCatalog.Find(slot, itemName);
        return new GearSlotBoostSetup
        {
            Slot = slot,
            ItemName = catalog?.Name ?? itemName,
            Meta = catalog?.Meta ?? ""
        };
    }

    private static AqwBoostItemInfo? ResolveGearCatalogSelection(GearSlotBoostEditorRow row, GearSlotBoostSetup? setup)
    {
        var none = row.AvailableItems.FirstOrDefault();
        if (setup == null)
            return none;

        if (!string.IsNullOrWhiteSpace(setup.ItemName))
        {
            var byName = row.AvailableItems.FirstOrDefault(i =>
                i.Name.Equals(setup.ItemName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byName != null)
                return byName;
        }

        var resolvedMeta = AqwBoostGearCatalog.ResolveMeta(setup);
        if (!string.IsNullOrWhiteSpace(resolvedMeta))
        {
            var byMeta = row.AvailableItems.FirstOrDefault(i =>
                !string.IsNullOrWhiteSpace(i.Meta) &&
                i.Meta.Equals(resolvedMeta, StringComparison.OrdinalIgnoreCase));
            if (byMeta != null)
                return byMeta;
        }

        return none;
    }

    private void RefreshStatsPreview()
    {
        if (_hostService == null || !_hostService.IsReady)
        {
            ClassSourceBadge = "OFFLINE";
            ClassSourceBadgeBackground = DiagnosticPalette.InfoBadgeBg;
            StatsPreviewLine1 = "Real starting stats preview loads once Skua / VibeSkua is detected.";
            StatsPreviewLine2 = "";
            return;
        }

        try
        {
            var previewScenario = BuildScenario();
            var profile = _hostService.PreviewScenarioStats(previewScenario);
            if (profile == null)
                return;

            var hp = profile.ComputedMaxHealth;
            var dps = (int)Math.Round(profile.EffectiveDps);

            if (profile.UsesCapturedClassData)
            {
                ClassSourceBadge = $"REAL DB ({profile.CapturedSkillCount} skills · {profile.CapturedSamples} samples)";
                ClassSourceBadgeBackground = DiagnosticPalette.WorldBadgeBg;
            }
            else
            {
                ClassSourceBadge = $"ARCHETYPE ({profile.StatModel})";
                ClassSourceBadgeBackground = DiagnosticPalette.ApiBadgeBg;
            }

            StatsPreviewLine1 =
                $"Starting Stats: HP {hp:N0} | DPS ~{dps:N0}/s | Crit {profile.CriticalChance:P0} (x{profile.CriticalMultiplier:F2}) | Haste {profile.Haste:P0} | Dodge {profile.EvasionChance:P0}";

            var gb = profile.GearBoosts;
            var srv = ServerBoost ? 2.0 : 1.0;
            var xpM = gb.XpMultiplier * (XpBoost ? 2.0 : 1.0) * srv;
            var cpM = gb.ClassPointsMultiplier * (ClassPointsBoost ? 2.0 : 1.0) * srv;
            var gM = gb.GoldMultiplier * (GoldBoost ? 2.0 : 1.0) * srv;
            var repM = gb.RepMultiplier * (RepBoost ? 2.0 : 1.0) * srv;

            var raceParts = gb.RaceDamageMultipliers.Count > 0
                ? " | Race: " + string.Join(", ", gb.RaceDamageMultipliers.Take(3).Select(kv => $"{kv.Key} +{(kv.Value - 1) * 100:0}%"))
                  + (gb.RaceDamageMultipliers.Count > 3 ? $" (+{gb.RaceDamageMultipliers.Count - 3})" : "")
                : "";

            StatsPreviewLine2 =
                $"Multipliers: DmgAll +{(gb.DamageAllMultiplier - 1) * 100:0}%{raceParts} | XP x{xpM:0.##} | CP x{cpM:0.##} | Gold x{gM:0.##} | Rep x{repM:0.##}";
        }
        catch { }
    }

    private void ApplyScenarioOptionsToEditorRows(Dictionary<string, JsonElement> options)
    {
        foreach (var row in ScriptOptions)
        {
            if (row.IsHeader)
                continue;
            if (options.TryGetValue($"{row.Category}:{row.Name}", out var scopedEl))
                ApplyJsonElementToOptionRow(row, scopedEl);
            else if (options.TryGetValue(row.Name, out var el))
                ApplyJsonElementToOptionRow(row, el);
        }
    }

    private static JsonElement SerializeOptionValue(ScriptOptionEditorRow opt)
    {
        if (opt.IsBool)
            return JsonSerializer.SerializeToElement(opt.BoolValue);
        if (opt.IsChoice)
            return JsonSerializer.SerializeToElement(opt.SelectedChoice ?? "");

        var vt = opt.ValueType ?? "";
        if ((vt.Equals("System.Int32", StringComparison.OrdinalIgnoreCase) || vt.Equals("Int32", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(opt.TextValue?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
            return JsonSerializer.SerializeToElement(iv);

        if ((vt.Equals("System.Int64", StringComparison.OrdinalIgnoreCase) || vt.Equals("Int64", StringComparison.OrdinalIgnoreCase)) &&
            long.TryParse(opt.TextValue?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv))
            return JsonSerializer.SerializeToElement(lv);

        if ((vt.Equals("System.Double", StringComparison.OrdinalIgnoreCase) || vt.Equals("Double", StringComparison.OrdinalIgnoreCase) ||
             vt.Equals("System.Single", StringComparison.OrdinalIgnoreCase) || vt.Equals("Single", StringComparison.OrdinalIgnoreCase) ||
             vt.Equals("System.Decimal", StringComparison.OrdinalIgnoreCase) || vt.Equals("Decimal", StringComparison.OrdinalIgnoreCase)) &&
            double.TryParse(opt.TextValue?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dv))
            return JsonSerializer.SerializeToElement(dv);

        return JsonSerializer.SerializeToElement(opt.TextValue ?? "");
    }

    private static bool IsBooleanTypeName(string? valueType) =>
        !string.IsNullOrWhiteSpace(valueType) &&
        (valueType.Equals("System.Boolean", StringComparison.OrdinalIgnoreCase) ||
         valueType.Equals("Boolean", StringComparison.OrdinalIgnoreCase) ||
         valueType.Equals("bool", StringComparison.OrdinalIgnoreCase));

    private static string CleanOptionDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "";
        return Regex.Replace(description.Trim(), @"\t+", " ");
    }

    private static void ApplyJsonElementToOptionRow(ScriptOptionEditorRow row, JsonElement el)
    {
        if (row.IsBool)
            row.BoolValue = el.ValueKind == JsonValueKind.True || (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b) && b);
        else if (row.IsChoice)
        {
            var str = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
            row.SelectedChoice = row.AllowedValues.FirstOrDefault(v => string.Equals(v, str, StringComparison.OrdinalIgnoreCase)) ?? str;
        }
        else
            row.TextValue = el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.ToString();
    }

    private static void ApplyRawStringToOptionRow(ScriptOptionEditorRow row, string raw)
    {
        if (row.IsBool)
            row.BoolValue = bool.TryParse(raw.Trim(), out var b) && b;
        else if (row.IsChoice)
            row.SelectedChoice = row.AllowedValues.FirstOrDefault(v => string.Equals(v, raw.Trim(), StringComparison.OrdinalIgnoreCase))
                                 ?? row.AllowedValues.FirstOrDefault();
        else
            row.TextValue = raw;
    }

    private static void ApplyDefaultToOptionRow(ScriptOptionEditorRow row, object? defaultValue)
    {
        if (row.IsBool)
            row.BoolValue = defaultValue is bool b ? b : bool.TryParse(defaultValue?.ToString(), out var pb) && pb;
        else if (row.IsChoice)
        {
            var str = defaultValue?.ToString();
            row.SelectedChoice = row.AllowedValues.FirstOrDefault(v => string.Equals(v, str, StringComparison.OrdinalIgnoreCase))
                                 ?? row.AllowedValues.FirstOrDefault();
        }
        else
            row.TextValue = defaultValue?.ToString() ?? "";
    }

    private void MarkDirty(string? customStatus = null)
    {
        if (_suppressDirty)
            return;
        IsDirty = true;
        EditorStatusText = customStatus ?? (string.IsNullOrWhiteSpace(LoadedFilePath)
            ? "Modified in-memory scenario (unsaved)"
            : $"Modified {Path.GetFileName(LoadedFilePath)} (unsaved)");
    }

    private static int ParseIntOr(string? text, int fallback) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static long ParseLongOr(string? text, long fallback) =>
        long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double ParseDoubleOr(string? text, double fallback) =>
        double.TryParse(text?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double ParseRealSecondsOrUnlimited(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            trimmed == "∞" ||
            trimmed.Equals("inf", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("infinite", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("unlimited", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        return double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? Math.Max(0, v)
            : 0;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "custom_scenario" : cleaned.ToLowerInvariant();
    }
}
