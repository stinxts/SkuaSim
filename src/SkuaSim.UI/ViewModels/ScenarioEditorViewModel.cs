using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
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
    private bool _suppressDirty;

    public ObservableCollection<ScenarioItemEditorRow> InventoryItems { get; } = new();
    public ObservableCollection<ScenarioItemEditorRow> BankItems { get; } = new();
    public ObservableCollection<ScriptOptionEditorRow> ScriptOptions { get; } = new();

    public IReadOnlyList<string> CommonCategories { get; } = new[]
    {
        "Item", "Class", "Armor", "Helm", "Cape", "Sword", "Staff", "Dagger", "Axe", "Bow", "Polearm", "Pet"
    };

    [ObservableProperty]
    public partial string LoadedFilePath { get; set; } = "";

    [ObservableProperty]
    public partial string EditorStatusText { get; set; } = "Default blank scenario (auto AQW stats)";

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    public partial bool IsInspectingOptions { get; set; }

    [ObservableProperty]
    public partial string ScriptOptionsSummary { get; set; } = "Click 'Inspect Script Options' to discover Bot.Config options for the selected .cs script.";

    // General
    [ObservableProperty] public partial string ScenarioName { get; set; } = "New Scenario";
    [ObservableProperty] public partial string ScenarioDescription { get; set; } = "";
    [ObservableProperty] public partial string SeedText { get; set; } = "42";

    // Player
    [ObservableProperty] public partial string Username { get; set; } = "SimPlayer";
    [ObservableProperty] public partial string LevelText { get; set; } = "100";
    [ObservableProperty] public partial string ClassName { get; set; } = "ArchPaladin";
    [ObservableProperty] public partial string ClassRankText { get; set; } = "10";
    [ObservableProperty] public partial string GoldText { get; set; } = "1000000";
    [ObservableProperty] public partial string CoinsText { get; set; } = "0";
    [ObservableProperty] public partial bool IsMember { get; set; } = true;
    [ObservableProperty] public partial bool Invulnerable { get; set; }
    [ObservableProperty] public partial string InventorySlotsText { get; set; } = "200";
    [ObservableProperty] public partial string BankSlotsText { get; set; } = "600";

    // Optional overrides (blank => auto AQW StatEngine)
    [ObservableProperty] public partial string MaxHealthOverrideText { get; set; } = "";
    [ObservableProperty] public partial string DpsOverrideText { get; set; } = "";
    [ObservableProperty] public partial string RegenOverrideText { get; set; } = "";

    // Location & Limits
    [ObservableProperty] public partial string StartMap { get; set; } = "battleon";
    [ObservableProperty] public partial string StartCell { get; set; } = "Enter";
    [ObservableProperty] public partial string StartPad { get; set; } = "Spawn";
    [ObservableProperty] public partial string MaxVirtualMinutesText { get; set; } = "360";
    [ObservableProperty] public partial string MaxRealSecondsText { get; set; } = "180";
    [ObservableProperty] public partial string StallRealSecondsText { get; set; } = "15";

    // Quests (comma-separated IDs)
    [ObservableProperty] public partial string CompletedQuestsText { get; set; } = "";
    [ObservableProperty] public partial string ActiveQuestsText { get; set; } = "";

    // Quick add inputs
    [ObservableProperty] public partial string NewInvName { get; set; } = "";
    [ObservableProperty] public partial string NewInvCategory { get; set; } = "Item";
    [ObservableProperty] public partial string NewInvQuantityText { get; set; } = "1";
    [ObservableProperty] public partial bool NewInvEquipped { get; set; }
    [ObservableProperty] public partial string NewInvEnhPatText { get; set; } = "0";
    [ObservableProperty] public partial string NewInvEnhLvlText { get; set; } = "0";
    [ObservableProperty] public partial string NewInvProcIdText { get; set; } = "0";

    [ObservableProperty] public partial string NewBankName { get; set; } = "";
    [ObservableProperty] public partial string NewBankQuantityText { get; set; } = "1";

    public void AttachServices(
        IFileDialogService dialogService,
        SimulationHostService? hostService,
        Func<string> scriptPathProvider,
        Func<string, Task> onSavedCallback)
    {
        _dialogService = dialogService;
        _hostService = hostService;
        _scriptPathProvider = scriptPathProvider;
        _onSavedCallback = onSavedCallback;
    }

    public void LoadFromScenario(Scenario scenario, string? sourcePath = null)
    {
        _suppressDirty = true;
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
            ClassName = p.Class ?? "";
            ClassRankText = p.ClassRank.ToString(CultureInfo.InvariantCulture);
            GoldText = p.Gold.ToString(CultureInfo.InvariantCulture);
            CoinsText = p.Coins.ToString(CultureInfo.InvariantCulture);
            IsMember = p.IsMember;
            Invulnerable = p.Invulnerable;
            InventorySlotsText = p.InventorySlots.ToString(CultureInfo.InvariantCulture);
            BankSlotsText = p.BankSlots.ToString(CultureInfo.InvariantCulture);
            MaxHealthOverrideText = p.MaxHealth.HasValue ? p.MaxHealth.Value.ToString(CultureInfo.InvariantCulture) : "";
            DpsOverrideText = p.DamagePerSecond.HasValue ? p.DamagePerSecond.Value.ToString(CultureInfo.InvariantCulture) : "";
            RegenOverrideText = p.RegenPercentPerSecond.HasValue ? p.RegenPercentPerSecond.Value.ToString(CultureInfo.InvariantCulture) : "";

            StartMap = scenario.Location.Map ?? "battleon";
            StartCell = scenario.Location.Cell ?? "Enter";
            StartPad = scenario.Location.Pad ?? "Spawn";

            MaxVirtualMinutesText = scenario.Limits.MaxVirtualMinutes.ToString(CultureInfo.InvariantCulture);
            MaxRealSecondsText = scenario.Limits.MaxRealSeconds.ToString(CultureInfo.InvariantCulture);
            StallRealSecondsText = scenario.Limits.StallRealSeconds.ToString(CultureInfo.InvariantCulture);

            CompletedQuestsText = string.Join(", ", scenario.Quests.Completed);
            ActiveQuestsText = string.Join(", ", scenario.Quests.Active);

            InventoryItems.Clear();
            foreach (var it in scenario.Inventory)
            {
                InventoryItems.Add(new ScenarioItemEditorRow
                {
                    Name = it.Name ?? (it.Id.HasValue ? $"#{it.Id}" : ""),
                    Category = string.IsNullOrWhiteSpace(it.Category) ? "Item" : it.Category,
                    Quantity = Math.Max(1, it.Quantity),
                    Equipped = it.Equipped,
                    EnhancementPatternID = it.EnhancementPatternID ?? 0,
                    EnhancementLevel = it.EnhancementLevel ?? 0,
                    ProcID = it.ProcID ?? 0
                });
            }

            BankItems.Clear();
            foreach (var it in scenario.Bank)
            {
                BankItems.Add(new ScenarioItemEditorRow
                {
                    Name = it.Name ?? (it.Id.HasValue ? $"#{it.Id}" : ""),
                    Category = string.IsNullOrWhiteSpace(it.Category) ? "Item" : it.Category,
                    Quantity = Math.Max(1, it.Quantity)
                });
            }

            ApplyScenarioOptionsToEditorRows(scenario.Options);

            IsDirty = false;
            EditorStatusText = string.IsNullOrWhiteSpace(LoadedFilePath)
                ? "Editing in-memory scenario"
                : $"Loaded {Path.GetFileName(LoadedFilePath)}";
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    public Scenario BuildScenario(int? toolbarSeedOverride = null)
    {
        // Round-trip clone base scenario to keep untouched sections (Maps, World, BotOptions, Factions, etc.)
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
        clone.Player.Invulnerable = Invulnerable;
        clone.Player.InventorySlots = Math.Max(10, ParseIntOr(InventorySlotsText, 200));
        clone.Player.BankSlots = Math.Max(10, ParseIntOr(BankSlotsText, 600));
        clone.Player.MaxHealth = ParseNullableInt(MaxHealthOverrideText);
        clone.Player.DamagePerSecond = ParseNullableInt(DpsOverrideText);
        clone.Player.RegenPercentPerSecond = ParseNullableDouble(RegenOverrideText);

        clone.Location.Map = string.IsNullOrWhiteSpace(StartMap) ? "battleon" : StartMap.Trim();
        clone.Location.Cell = string.IsNullOrWhiteSpace(StartCell) ? "Enter" : StartCell.Trim();
        clone.Location.Pad = string.IsNullOrWhiteSpace(StartPad) ? "Spawn" : StartPad.Trim();

        clone.Limits.MaxVirtualMinutes = Math.Max(0.1, ParseDoubleOr(MaxVirtualMinutesText, 360));
        clone.Limits.MaxRealSeconds = Math.Max(1, ParseDoubleOr(MaxRealSecondsText, 180));
        clone.Limits.StallRealSeconds = Math.Max(1, ParseDoubleOr(StallRealSecondsText, 15));

        clone.Quests.Completed = ParseIntList(CompletedQuestsText);
        clone.Quests.Active = ParseIntList(ActiveQuestsText);

        clone.Inventory = InventoryItems
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new ItemSetup
            {
                Name = i.Name.Trim(),
                Category = string.IsNullOrWhiteSpace(i.Category) ? null : i.Category.Trim(),
                Quantity = Math.Max(1, i.Quantity),
                Equipped = i.Equipped,
                EnhancementPatternID = i.EnhancementPatternID > 0 ? i.EnhancementPatternID : null,
                EnhancementLevel = i.EnhancementLevel > 0 ? i.EnhancementLevel : null,
                ProcID = i.ProcID > 0 ? i.ProcID : null
            })
            .ToList();

        clone.Bank = BankItems
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new ItemSetup
            {
                Name = i.Name.Trim(),
                Category = string.IsNullOrWhiteSpace(i.Category) ? null : i.Category.Trim(),
                Quantity = Math.Max(1, i.Quantity)
            })
            .ToList();

        var options = new Dictionary<string, JsonElement>(clone.Options, StringComparer.OrdinalIgnoreCase);
        foreach (var opt in ScriptOptions)
        {
            if (string.IsNullOrWhiteSpace(opt.Name))
                continue;

            if (opt.IsBool)
            {
                options[opt.Name] = JsonSerializer.SerializeToElement(opt.BoolValue);
            }
            else if (opt.IsChoice)
            {
                options[opt.Name] = JsonSerializer.SerializeToElement(opt.SelectedChoice ?? "");
            }
            else if (opt.ValueType.Equals("Int32", StringComparison.OrdinalIgnoreCase) && int.TryParse(opt.TextValue, out var iv))
            {
                options[opt.Name] = JsonSerializer.SerializeToElement(iv);
            }
            else if (opt.ValueType.Equals("Double", StringComparison.OrdinalIgnoreCase) &&
                     double.TryParse(opt.TextValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv))
            {
                options[opt.Name] = JsonSerializer.SerializeToElement(dv);
            }
            else
            {
                options[opt.Name] = JsonSerializer.SerializeToElement(opt.TextValue ?? "");
            }
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
            },
            Inventory = new List<ItemSetup>
            {
                new() { Name = "Default Staff", Category = "Staff", Quantity = 1, Equipped = true, EnhancementLevel = 0, EnhancementPatternID = 0 }
            }
        };
        LoadFromScenario(starter, "");
        MarkDirty("Created Lv 1 Starter template (unsaved)");
    }

    [RelayCommand]
    private void NewEndgameTemplate()
    {
        var endgame = new Scenario
        {
            Name = "Lv 100 Endgame ArchPaladin",
            Description = "Level 100 character with full Lucky Lv 100 enhancements.",
            Seed = 42,
            Player = new PlayerSetup
            {
                Username = "EndgameHero",
                Level = 100,
                Gold = 5_000_000,
                Coins = 2_000,
                IsMember = true,
                Class = "ArchPaladin",
                ClassRank = 10
            },
            Inventory = new List<ItemSetup>
            {
                new() { Name = "Burning Blade of Abezeth", Category = "Sword", Equipped = true, EnhancementPatternID = 9, EnhancementLevel = 100, ProcID = 9 },
                new() { Name = "ArchPaladin", Category = "Class", Equipped = true, EnhancementPatternID = 9, EnhancementLevel = 100 },
                new() { Name = "Forge Helm", Category = "Helm", Equipped = true, EnhancementPatternID = 10, EnhancementLevel = 100 },
                new() { Name = "Lament Cape", Category = "Cape", Equipped = true, EnhancementPatternID = 30, EnhancementLevel = 100 }
            }
        };
        LoadFromScenario(endgame, "");
        MarkDirty("Created Lv 100 Endgame template (unsaved)");
    }

    [RelayCommand]
    private void AddInventoryItem()
    {
        if (string.IsNullOrWhiteSpace(NewInvName))
            return;

        InventoryItems.Add(new ScenarioItemEditorRow
        {
            Name = NewInvName.Trim(),
            Category = string.IsNullOrWhiteSpace(NewInvCategory) ? "Item" : NewInvCategory.Trim(),
            Quantity = Math.Max(1, ParseIntOr(NewInvQuantityText, 1)),
            Equipped = NewInvEquipped,
            EnhancementPatternID = Math.Max(0, ParseIntOr(NewInvEnhPatText, 0)),
            EnhancementLevel = Math.Max(0, ParseIntOr(NewInvEnhLvlText, 0)),
            ProcID = Math.Max(0, ParseIntOr(NewInvProcIdText, 0))
        });

        NewInvName = "";
        NewInvQuantityText = "1";
        NewInvEquipped = false;
        NewInvEnhPatText = "0";
        NewInvEnhLvlText = "0";
        NewInvProcIdText = "0";
        MarkDirty();
    }

    [RelayCommand]
    private void RemoveInventoryItem(ScenarioItemEditorRow? row)
    {
        if (row != null && InventoryItems.Remove(row))
            MarkDirty();
    }

    [RelayCommand]
    private void AddBankItem()
    {
        if (string.IsNullOrWhiteSpace(NewBankName))
            return;

        BankItems.Add(new ScenarioItemEditorRow
        {
            Name = NewBankName.Trim(),
            Category = "Item",
            Quantity = Math.Max(1, ParseIntOr(NewBankQuantityText, 1))
        });

        NewBankName = "";
        NewBankQuantityText = "1";
        MarkDirty();
    }

    [RelayCommand]
    private void RemoveBankItem(ScenarioItemEditorRow? row)
    {
        if (row != null && BankItems.Remove(row))
            MarkDirty();
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

        var startDir = !string.IsNullOrWhiteSpace(LoadedFilePath) && File.Exists(LoadedFilePath)
            ? Path.GetDirectoryName(LoadedFilePath)
            : Directory.Exists("scenarios") ? Path.GetFullPath("scenarios") : Directory.GetCurrentDirectory();

        var suggested = string.IsNullOrWhiteSpace(ScenarioName)
            ? "custom_scenario.json"
            : string.Concat(ScenarioName.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_')) + ".json";

        var path = await _dialogService.PickSaveFileAsync(
            "Save Scenario (.json)",
            startDir,
            suggested,
            "json",
            new[] { ("Scenario JSON", new[] { "*.json" }), ("All Files", new[] { "*.*" }) });

        if (string.IsNullOrWhiteSpace(path))
            return;

        await WriteScenarioToPathAsync(path);
    }

    [RelayCommand]
    private async Task InspectScriptOptionsAsync()
    {
        if (_hostService == null || !_hostService.IsReady || _scriptPathProvider == null)
        {
            ScriptOptionsSummary = "Skua/VibeSkua is not configured yet.";
            return;
        }

        var scriptPath = _scriptPathProvider()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            ScriptOptionsSummary = "Select a valid .cs script in the top bar first.";
            return;
        }

        IsInspectingOptions = true;
        ScriptOptionsSummary = $"Inspecting {Path.GetFileName(scriptPath)}...";
        try
        {
            var result = await _hostService.InspectScriptAsync(scriptPath);
            if (!result.Success)
            {
                ScriptOptionsSummary = result.ErrorMessage ?? "Failed to inspect script (compile errors).";
                return;
            }

            ScriptOptions.Clear();
            foreach (var d in result.Options)
            {
                var isBool = d.ValueType.Equals("Boolean", StringComparison.OrdinalIgnoreCase);
                var isChoice = !isBool && d.AllowedValues.Count > 0;
                var row = new ScriptOptionEditorRow
                {
                    Name = d.Name,
                    DisplayName = string.IsNullOrWhiteSpace(d.DisplayName) ? d.Name : d.DisplayName,
                    Category = d.Category,
                    Description = d.Description,
                    ValueType = d.ValueType,
                    IsBool = isBool,
                    IsChoice = isChoice
                };
                foreach (var allowed in d.AllowedValues)
                    row.AllowedValues.Add(allowed);

                var defStr = d.DefaultValue?.ToString() ?? "";
                if (isBool)
                    row.BoolValue = d.DefaultValue is bool b ? b : bool.TryParse(defStr, out var pb) && pb;
                else if (isChoice)
                    row.SelectedChoice = row.AllowedValues.FirstOrDefault(v => string.Equals(v, defStr, StringComparison.OrdinalIgnoreCase))
                                         ?? row.AllowedValues.FirstOrDefault();
                else
                    row.TextValue = defStr;

                ScriptOptions.Add(row);
            }

            // Overlay any already configured option values from current scenario
            ApplyScenarioOptionsToEditorRows(BuildScenario().Options);

            ScriptOptionsSummary = result.Options.Count > 0
                ? $"Found {result.Options.Count} option(s) in {result.ScriptClass} (storage: {result.OptionsStorage ?? "-"})"
                : $"{result.ScriptClass} defines no configurable Bot.Config options.";
        }
        catch (Exception ex)
        {
            ScriptOptionsSummary = $"Inspect failed: {ex.Message}";
        }
        finally
        {
            IsInspectingOptions = false;
        }
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

    private void ApplyScenarioOptionsToEditorRows(Dictionary<string, JsonElement> optionMap)
    {
        if (optionMap == null || optionMap.Count == 0)
            return;

        foreach (var (key, val) in optionMap)
        {
            var existing = ScriptOptions.FirstOrDefault(o => string.Equals(o.Name, key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (existing.IsBool)
                    existing.BoolValue = val.ValueKind == JsonValueKind.True || (val.ValueKind == JsonValueKind.String && bool.TryParse(val.GetString(), out var b) && b);
                else if (existing.IsChoice)
                    existing.SelectedChoice = val.ToString();
                else
                    existing.TextValue = val.ValueKind == JsonValueKind.String ? (val.GetString() ?? "") : val.ToString();
            }
            else
            {
                var isBool = val.ValueKind is JsonValueKind.True or JsonValueKind.False;
                ScriptOptions.Add(new ScriptOptionEditorRow
                {
                    Name = key,
                    DisplayName = key,
                    Category = "Scenario",
                    Description = "Loaded from scenario JSON",
                    ValueType = isBool ? "Boolean" : "String",
                    IsBool = isBool,
                    BoolValue = val.ValueKind == JsonValueKind.True,
                    TextValue = val.ValueKind == JsonValueKind.String ? (val.GetString() ?? "") : val.ToString()
                });
            }
        }
    }

    private void MarkDirty(string? status = null)
    {
        if (_suppressDirty)
            return;
        IsDirty = true;
        if (!string.IsNullOrWhiteSpace(status))
            EditorStatusText = status;
        else if (!EditorStatusText.EndsWith("*"))
            EditorStatusText = string.IsNullOrWhiteSpace(LoadedFilePath) ? "Unsaved scenario*" : $"{Path.GetFileName(LoadedFilePath)}*";
    }

    private static int ParseIntOr(string? text, int fallback) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static long ParseLongOr(string? text, long fallback) =>
        long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double ParseDoubleOr(string? text, double fallback) =>
        double.TryParse(text?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int? ParseNullableInt(string? text) =>
        !string.IsNullOrWhiteSpace(text) && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? ParseNullableDouble(string? text) =>
        !string.IsNullOrWhiteSpace(text) && double.TryParse(text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static List<int> ParseIntList(string? csv)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(csv))
            return list;
        foreach (var part in csv.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out var id) && id > 0 && !list.Contains(id))
                list.Add(id);
        }
        return list;
    }
}
