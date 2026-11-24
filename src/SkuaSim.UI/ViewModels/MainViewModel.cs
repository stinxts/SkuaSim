using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkuaSim.Core.Runtime;
using SkuaSim.UI.Models;
using SkuaSim.UI.Services;

namespace SkuaSim.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly SimulationHostService? _hostService;
    private readonly DispatcherTimer? _pollTimer;
    private readonly List<ScriptEntry> _allScripts = new();

    private IFileDialogService? _dialogService;
    private SimulationSession? _activeSession;
    private int _logCursor;
    private int _warnCursor;
    private long _traceCursor;
    private bool _suppressScriptSelectionSync;
    private bool _suppressScenarioSelectionSync;
    private bool _suppressDurationSync;
    private double _lastLogicalWidth = 1920;
    private double _lastLogicalHeight = 1080;

    public MainViewModel()
    {
        ClientState = new ClientStateViewModel();
        ScenarioEditor = new ScenarioEditorViewModel();
        Diagnostics = new DiagnosticsViewModel();

        SpeedPresets = new ObservableCollection<SpeedPreset>
        {
            new("Instant (Max)", 0),
            new("1x (Real-time)", 1),
            new("5x", 5),
            new("20x", 20),
            new("100x", 100)
        };
        SelectedSpeed = SpeedPresets[0];

        DisplayScalePresets = new ObservableCollection<DisplayScalePreset>
        {
            new("Auto (Screen)", 1.0, IsAuto: true),
            new("HD (720p)", 0.86, IsAuto: false),
            new("Full-HD (1080p)", 1.00, IsAuto: false),
            new("2K (1440p)", 1.18, IsAuto: false),
            new("4K (2160p)", 1.45, IsAuto: false),
            new("8K (4320p)", 2.10, IsAuto: false)
        };
        SelectedDisplayScale = DisplayScalePresets[0];

        VirtualDurationPresets = new ObservableCollection<VirtualDurationPreset>
        {
            new("30 min", 30, false),
            new("1 hour", 60, false),
            new("6 hours", 360, false),
            new("1 day (24h)", 1440, false),
            new("3 days", 4320, false),
            new("7 days (1 wk)", 10080, false),
            new("14 days (2 wks)", 20160, false),
            new("30 days", 43200, false),
            new("Unlimited", 0, false),
            new("Custom...", 360, true)
        };
        SelectedVirtualDuration = VirtualDurationPresets[2]; // 6 hours default

        if (Design.IsDesignMode)
        {
            ClientFlavorText = "VibeSkua (Design Preview)";
            IsSkuaConfigured = true;
            StatusText = "Ready";
            return;
        }

        _hostService = new SimulationHostService();
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pollTimer.Tick += (_, _) => PollActiveSession();

        RefreshSkuaConfigurationState();
        _ = LoadWorkspaceAsync();
    }

    public ClientStateViewModel ClientState { get; }
    public ScenarioEditorViewModel ScenarioEditor { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public ObservableCollection<SpeedPreset> SpeedPresets { get; }
    public ObservableCollection<DisplayScalePreset> DisplayScalePresets { get; }
    public ObservableCollection<VirtualDurationPreset> VirtualDurationPresets { get; }
    public IReadOnlyList<string> CustomDurationUnits { get; } = new[] { "Minutes", "Hours", "Days", "Weeks" };
    public ObservableCollection<ScriptEntry> FilteredScripts { get; } = new();
    public ObservableCollection<ScenarioEntry> AvailableScenarios { get; } = new();

    [ObservableProperty]
    public partial int WorkspaceTabIndex { get; set; }

    [ObservableProperty]
    public partial double UiScale { get; set; } = 1.0;

    [ObservableProperty]
    public partial DisplayScalePreset SelectedDisplayScale { get; set; }

    [ObservableProperty]
    public partial VirtualDurationPreset SelectedVirtualDuration { get; set; }

    [ObservableProperty]
    public partial bool IsCustomVirtualDuration { get; set; }

    [ObservableProperty]
    public partial string CustomVirtualAmountText { get; set; } = "24";

    [ObservableProperty]
    public partial string CustomVirtualUnit { get; set; } = "Hours";

    [ObservableProperty]
    public partial string ScriptPathText { get; set; } = "";

    [ObservableProperty]
    public partial string ScriptSearchText { get; set; } = "";

    [ObservableProperty]
    public partial ScriptEntry? SelectedScript { get; set; }

    [ObservableProperty]
    public partial string ScenarioPathText { get; set; } = "";

    [ObservableProperty]
    public partial ScenarioEntry? SelectedScenario { get; set; }

    [ObservableProperty]
    public partial string SeedText { get; set; } = "42";

    /// <summary>Puts a new random seed in the Seed box (UI only, before a run; the run itself stays seeded).</summary>
    [RelayCommand]
    private void RandomSeed() =>
        SeedText = SimulationRunner.NewRandomSeed().ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial SpeedPreset SelectedSpeed { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    public partial string ClientFlavorText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSkuaConfigured { get; set; }

    [ObservableProperty]
    public partial string? SkuaSetupWarning { get; set; }

    [ObservableProperty]
    public partial bool IsCompiling { get; set; }

    [ObservableProperty]
    public partial bool IsSessionActive { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial bool IsStopping { get; set; }

    [ObservableProperty]
    public partial string PauseResumeLabel { get; set; } = "Pause";

    [ObservableProperty]
    public partial string VirtualClockText { get; set; } = "00:00:00.000";

    [ObservableProperty]
    public partial string RealTimeText { get; set; } = "0.00 s";

    [ObservableProperty]
    public partial string PausedTimeText { get; set; } = "0.00 s";

    [ObservableProperty]
    public partial string ApiCallsText { get; set; } = "0";

    [ObservableProperty]
    public partial string LastApiCallText { get; set; } = "-";

    public void AttachServices(IFileDialogService dialogService)
    {
        _dialogService = dialogService;
        Diagnostics.AttachDialogService(dialogService);
        ScenarioEditor.AttachServices(
            dialogService,
            _hostService,
            () => ScriptPathText,
            OnScenarioSavedAsync,
            SyncToolbarVirtualDurationFromEditor);
    }

    public void UpdateAutoScale(double logicalWidth, double logicalHeight)
    {
        if (logicalWidth <= 0 || logicalHeight <= 0)
            return;
        _lastLogicalWidth = logicalWidth;
        _lastLogicalHeight = logicalHeight;
        if (SelectedDisplayScale is { IsAuto: true })
            UiScale = ComputeAutoScale(logicalWidth, logicalHeight);
    }

    private static double ComputeAutoScale(double logicalWidth, double logicalHeight)
    {
        if (logicalWidth < 1360 || logicalHeight < 760)
            return 0.86; // HD (720p) compact fit
        if (logicalWidth <= 2100)
            return 1.00; // Full-HD (1080p)
        if (logicalWidth <= 3000)
            return 1.18; // 2K (1440p) at 100% OS DPI
        if (logicalWidth <= 5200)
            return 1.45; // 4K (2160p) at 100% OS DPI
        return 2.10;     // 8K (4320p) at 100% OS DPI
    }

    partial void OnSelectedDisplayScaleChanged(DisplayScalePreset value)
    {
        if (value == null)
            return;
        UiScale = value.IsAuto
            ? ComputeAutoScale(_lastLogicalWidth, _lastLogicalHeight)
            : value.ScaleFactor;
    }

    partial void OnSelectedVirtualDurationChanged(VirtualDurationPreset value)
    {
        if (value == null)
            return;
        IsCustomVirtualDuration = value.IsCustom;
        if (!_suppressDurationSync)
        {
            ScenarioEditor.SetVirtualMinutesFromToolbar(GetSelectedVirtualMinutes());
        }
    }

    partial void OnCustomVirtualAmountTextChanged(string value)
    {
        if (!_suppressDurationSync && IsCustomVirtualDuration)
            ScenarioEditor.SetVirtualMinutesFromToolbar(GetSelectedVirtualMinutes());
    }

    partial void OnCustomVirtualUnitChanged(string value)
    {
        if (!_suppressDurationSync && IsCustomVirtualDuration)
            ScenarioEditor.SetVirtualMinutesFromToolbar(GetSelectedVirtualMinutes());
    }

    private double GetSelectedVirtualMinutes()
    {
        if (SelectedVirtualDuration == null)
            return 360;
        if (!SelectedVirtualDuration.IsCustom)
            return SelectedVirtualDuration.Minutes;

        var amount = double.TryParse(CustomVirtualAmountText?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : 6.0;
        if (amount <= 0)
            return 0;

        return CustomVirtualUnit switch
        {
            "Hours" => amount * 60.0,
            "Days" => amount * 1440.0,
            "Weeks" => amount * 10080.0,
            _ => amount
        };
    }

    private void SyncToolbarVirtualDurationFromEditor(double minutes)
    {
        _suppressDurationSync = true;
        try
        {
            var exact = VirtualDurationPresets.FirstOrDefault(p => !p.IsCustom && Math.Abs(p.Minutes - minutes) < 0.01);
            if (exact != null)
            {
                SelectedVirtualDuration = exact;
                IsCustomVirtualDuration = false;
                return;
            }

            var custom = VirtualDurationPresets.Last(p => p.IsCustom);
            SelectedVirtualDuration = custom;
            IsCustomVirtualDuration = true;

            if (minutes >= 10080 && Math.Abs(minutes % 10080) < 0.01)
            {
                CustomVirtualUnit = "Weeks";
                CustomVirtualAmountText = (minutes / 10080.0).ToString("0.##", CultureInfo.InvariantCulture);
            }
            else if (minutes >= 1440 && Math.Abs(minutes % 1440) < 0.01)
            {
                CustomVirtualUnit = "Days";
                CustomVirtualAmountText = (minutes / 1440.0).ToString("0.##", CultureInfo.InvariantCulture);
            }
            else if (minutes >= 60 && Math.Abs(minutes % 60) < 0.01)
            {
                CustomVirtualUnit = "Hours";
                CustomVirtualAmountText = (minutes / 60.0).ToString("0.##", CultureInfo.InvariantCulture);
            }
            else
            {
                CustomVirtualUnit = "Minutes";
                CustomVirtualAmountText = minutes.ToString("0.##", CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            _suppressDurationSync = false;
        }
    }

    public void Shutdown()
    {
        _pollTimer?.Stop();
        _hostService?.Dispose();
    }

    partial void OnScriptSearchTextChanged(string value) => ApplyScriptFilter();

    partial void OnScriptPathTextChanged(string value) => RefreshCommandStates();

    partial void OnSelectedScriptChanged(ScriptEntry? value)
    {
        if (_suppressScriptSelectionSync || value == null)
            return;
        ScriptPathText = value.FullPath;
    }

    partial void OnSelectedScenarioChanged(ScenarioEntry? value)
    {
        if (_suppressScenarioSelectionSync || value == null)
            return;
        ScenarioPathText = value.FilePath ?? "";
        LoadScenarioIntoEditor(value.FilePath);
    }

    partial void OnSelectedSpeedChanged(SpeedPreset value)
    {
        if (_activeSession != null && value != null)
            _activeSession.SpeedMultiplier = value.Multiplier;
    }

    private bool CanCompile() => IsSkuaConfigured && !IsCompiling && !IsSessionActive && !string.IsNullOrWhiteSpace(ScriptPathText);
    private bool CanRun() => IsSkuaConfigured && !IsCompiling && !IsSessionActive && !string.IsNullOrWhiteSpace(ScriptPathText);
    private bool CanPauseResume() => IsSessionActive && !IsCompiling && !IsStopping;
    private bool CanStop() => IsSessionActive && !IsStopping;

    [RelayCommand(CanExecute = nameof(CanCompile))]
    private async Task CompileAsync()
    {
        if (_hostService == null || string.IsNullOrWhiteSpace(ScriptPathText))
            return;

        var path = ScriptPathText.Trim();
        if (!File.Exists(path))
        {
            Diagnostics.SetError("Script Not Found", $"Could not find script file: {path}");
            Diagnostics.SelectedTabIndex = 1;
            StatusText = "Script not found";
            return;
        }

        IsCompiling = true;
        StatusText = "Compiling...";
        Diagnostics.Reset();
        RefreshCommandStates();

        try
        {
            var result = await _hostService.CompileAsync(path);
            if (result.Success)
            {
                Diagnostics.SetCompileSuccess(result);
                StatusText = $"Compiled OK ({result.Elapsed.TotalMilliseconds:F0} ms, {result.IncludedFiles.Count} include(s))";
                Diagnostics.SelectedTabIndex = 0;
            }
            else
            {
                Diagnostics.SetCompileFailure(path, result.Errors);
                StatusText = $"Compile failed ({result.Errors.Count} error(s))";
                Diagnostics.SelectedTabIndex = 1;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.SetError("Internal Compile Error", ex.ToString());
            StatusText = "Compile error";
            Diagnostics.SelectedTabIndex = 1;
        }
        finally
        {
            IsCompiling = false;
            RefreshCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        if (_hostService == null || string.IsNullOrWhiteSpace(ScriptPathText))
            return;

        var scriptPath = ScriptPathText.Trim();
        if (!File.Exists(scriptPath))
        {
            Diagnostics.SetError("Script Not Found", $"Could not find script file: {scriptPath}");
            Diagnostics.SelectedTabIndex = 1;
            StatusText = "Script not found";
            return;
        }

        var scenarioPath = string.IsNullOrWhiteSpace(ScenarioPathText) ? null : ScenarioPathText.Trim();
        if (scenarioPath != null && !File.Exists(scenarioPath))
        {
            Diagnostics.SetError("Scenario Not Found", $"Could not find scenario file: {scenarioPath}");
            Diagnostics.SelectedTabIndex = 1;
            StatusText = "Scenario not found";
            return;
        }

        // If the user typed a different scenario path directly in the toolbar box, sync editor before running.
        if (scenarioPath != null &&
            !string.Equals(Path.GetFullPath(scenarioPath), ScenarioEditor.LoadedFilePath, StringComparison.OrdinalIgnoreCase))
        {
            LoadScenarioIntoEditor(scenarioPath);
        }

        int? seedOverride = int.TryParse(SeedText.Trim(), out var parsedSeed) ? parsedSeed : null;
        var virtualMinutesOverride = GetSelectedVirtualMinutes();
        var scenarioToRun = ScenarioEditor.BuildScenario(seedOverride, virtualMinutesOverride);
        scenarioToRun.Seed = seedOverride; // the toolbar decides (empty = the default seed 42)

        WorkspaceTabIndex = 0;
        ClientState.Reset();
        Diagnostics.Reset();
        Diagnostics.SelectedTabIndex = 0;
        _logCursor = 0;
        _warnCursor = 0;
        _traceCursor = 0;

        VirtualClockText = "00:00:00.000";
        RealTimeText = "0.00 s";
        PausedTimeText = "0.00 s";
        ApiCallsText = "0";
        LastApiCallText = "-";

        try
        {
            var session = _hostService.StartSimulation(scriptPath, scenarioToRun, seedOverride, SelectedSpeed.Multiplier);
            _activeSession = session;
            IsSessionActive = true;
            IsPaused = false;
            IsStopping = false;
            PauseResumeLabel = "Pause";
            StatusText = "Compiling script...";
            RefreshCommandStates();

            _ = session.Completion.ContinueWith(
                t => Dispatcher.UIThread.Post(() => OnSessionCompleted(session, t.Result)),
                TaskScheduler.Default);
            PollActiveSession();
            _pollTimer?.Start();
        }
        catch (Exception ex)
        {
            Diagnostics.SetError("Failed to Start Simulation", ex.ToString());
            Diagnostics.SelectedTabIndex = 1;
            StatusText = "Start failed";
            IsSessionActive = false;
            RefreshCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPauseResume))]
    private void PauseResume()
    {
        if (_activeSession == null)
            return;

        if (_activeSession.IsPaused)
        {
            _activeSession.Resume();
            IsPaused = false;
            PauseResumeLabel = "Pause";
            StatusText = "Running";
        }
        else
        {
            _activeSession.Pause();
            IsPaused = true;
            PauseResumeLabel = "Resume";
            StatusText = "Paused";
        }
        RefreshCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (_activeSession == null)
            return;

        IsStopping = true;
        StatusText = "Stopping...";
        RefreshCommandStates();
        _activeSession.Stop("stopped by user from UI");
    }

    [RelayCommand]
    private async Task BrowseScriptAsync()
    {
        if (_dialogService == null)
            return;

        var initialDir = File.Exists(ScriptPathText)
            ? Path.GetDirectoryName(ScriptPathText)
            : _hostService?.ScriptsDirectory;

        var picked = await _dialogService.PickOpenFileAsync(
            "Select Skua Script (.cs)",
            initialDir,
            new[] { ("C# Scripts", new[] { "*.cs" }), ("All Files", new[] { "*.*" }) });

        if (!string.IsNullOrWhiteSpace(picked))
            ScriptPathText = picked;
    }

    [RelayCommand]
    private async Task BrowseScenarioAsync()
    {
        if (_dialogService == null)
            return;

        var initialDir = File.Exists(ScenarioPathText)
            ? Path.GetDirectoryName(ScenarioPathText)
            : Directory.Exists("scenarios") ? Path.GetFullPath("scenarios") : Directory.GetCurrentDirectory();

        var picked = await _dialogService.PickOpenFileAsync(
            "Select Scenario (.json)",
            initialDir,
            new[] { ("Scenario JSON", new[] { "*.json" }), ("All Files", new[] { "*.*" }) });

        if (!string.IsNullOrWhiteSpace(picked))
        {
            ScenarioPathText = picked;
            LoadScenarioIntoEditor(picked);
        }
    }

    [RelayCommand]
    private async Task BrowseSkuaFolderAsync()
    {
        if (_dialogService == null || _hostService == null)
            return;

        var picked = await _dialogService.PickFolderAsync(
            "Select Skua or VibeSkua Assemblies Folder",
            _hostService.AssembliesDirectory);

        if (string.IsNullOrWhiteSpace(picked))
            return;

        _hostService.ReconfigureSkua(picked);
        RefreshSkuaConfigurationState();
        await LoadWorkspaceAsync();
    }

    [RelayCommand]
    private async Task RefreshWorkspaceAsync()
    {
        await LoadWorkspaceAsync();
    }

    private async Task LoadWorkspaceAsync()
    {
        if (_hostService == null)
            return;

        var scenarios = _hostService.DiscoverScenarios();
        AvailableScenarios.Clear();
        foreach (var s in scenarios)
            AvailableScenarios.Add(s);

        _suppressScenarioSelectionSync = true;
        try
        {
            if (string.IsNullOrWhiteSpace(ScenarioPathText))
            {
                var preferred = AvailableScenarios.FirstOrDefault(s => s.FilePath != null) ?? AvailableScenarios.FirstOrDefault();
                SelectedScenario = preferred;
                ScenarioPathText = preferred?.FilePath ?? "";
                LoadScenarioIntoEditor(preferred?.FilePath);
            }
        }
        finally
        {
            _suppressScenarioSelectionSync = false;
        }

        ScenarioEditor.RefreshKnownClasses(forceReload: true);

        var scripts = await _hostService.DiscoverScriptsAsync();
        _allScripts.Clear();
        _allScripts.AddRange(scripts);
        ApplyScriptFilter();
    }

    private void ApplyScriptFilter()
    {
        var query = (ScriptSearchText ?? "").Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _allScripts.Take(200)
            : _allScripts.Where(s => s.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(200);

        _suppressScriptSelectionSync = true;
        try
        {
            FilteredScripts.Clear();
            foreach (var item in matches)
                FilteredScripts.Add(item);
        }
        finally
        {
            _suppressScriptSelectionSync = false;
        }
    }

    private void PollActiveSession()
    {
        var session = _activeSession;
        if (session == null)
            return;

        var snap = session.GetLiveSnapshot();
        ApplyLiveSnapshot(snap);
        DrainIncrementalTrace(session, drainAll: false);
    }

    private void OnSessionCompleted(SimulationSession session, SimulationReport report)
    {
        if (!ReferenceEquals(session, _activeSession))
            return;

        _pollTimer?.Stop();
        DrainIncrementalTrace(session, drainAll: true);

        var finalSnap = session.GetLiveSnapshot();
        ApplyLiveSnapshot(finalSnap);
        ClientState.ApplySnapshot(report.FinalState);
        Diagnostics.ApplyReportOutcome(report);

        IsSessionActive = false;
        IsCompiling = false;
        IsPaused = false;
        IsStopping = false;
        PauseResumeLabel = "Pause";

        StatusText = FormatOutcomeStatus(report);
        if (report.Assertions.Count > 0)
            StatusText += report.AssertionsFailed == 0
                ? $" · Assertions passed ({report.Assertions.Count}/{report.Assertions.Count})"
                : $" · {report.AssertionsFailed} assertion(s) FAILED (see Errors & Diagnostics)";
        if (report.Outcome is SimulationOutcome.CompileError or SimulationOutcome.ScriptException or SimulationOutcome.Stalled || report.AssertionsFailed > 0)
            Diagnostics.SelectedTabIndex = 1;

        RefreshCommandStates();
    }

    private void ApplyLiveSnapshot(SimulationLiveSnapshot snap)
    {
        VirtualClockText = FormatDuration(snap.VirtualTime);
        RealTimeText = FormatRealTime(snap.ActiveRealTime);
        PausedTimeText = FormatRealTime(snap.PausedTime);
        ApiCallsText = $"{snap.TotalApiCalls:N0}";
        LastApiCallText = string.IsNullOrWhiteSpace(snap.LastApiCall) ? "-" : snap.LastApiCall;

        if (snap.State != SimulationState.Completed)
        {
            var prevCompiling = IsCompiling;
            var prevPaused = IsPaused;
            var nextCompiling = snap.State == SimulationState.Compiling;

            IsCompiling = nextCompiling;
            IsPaused = snap.IsPaused;
            PauseResumeLabel = snap.IsPaused ? "Resume" : "Pause";
            StatusText = snap.State switch
            {
                SimulationState.Compiling => "Compiling...",
                SimulationState.Paused => "Paused",
                SimulationState.Stopping => "Stopping...",
                SimulationState.Running => "Running",
                _ => StatusText
            };

            if (prevCompiling != nextCompiling || prevPaused != snap.IsPaused)
                RefreshCommandStates();
        }

        if (snap.State != SimulationState.Compiling)
            ClientState.ApplySnapshot(snap.World);
    }

    private void DrainIncrementalTrace(SimulationSession session, bool drainAll)
    {
        var iterations = drainAll ? 50 : 2;
        for (var i = 0; i < iterations; i++)
        {
            var logs = session.ReadScriptLogsSince(_logCursor, out _logCursor, 400);
            var warns = session.ReadWarningsSince(_warnCursor, out _warnCursor, 300);
            var timeline = session.ReadTimelineSince(_traceCursor, out _traceCursor, out var dropped, 500);
            if (logs.Count > 0)
                Diagnostics.AppendLogs(logs);
            if (warns.Count > 0)
                Diagnostics.AppendWarnings(warns);
            if (timeline.Count > 0 || dropped)
                Diagnostics.AppendTimeline(timeline, dropped);
            if (logs.Count == 0 && warns.Count == 0 && timeline.Count == 0)
                break;
        }

        Diagnostics.SyncDiagnostics(
            session.GetSynthesizedSnapshot(),
            session.GetUnimplementedSnapshot());
    }

    private void LoadScenarioIntoEditor(string? path)
    {
        if (_hostService == null)
            return;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ScenarioEditor.LoadFromScenario(new Core.Scenarios.Scenario { Name = "Custom Scenario" }, "");
            return;
        }

        try
        {
            var loaded = _hostService.LoadScenarioFile(path);
            ScenarioEditor.LoadFromScenario(loaded, Path.GetFullPath(path));
            SeedText = (loaded.Seed ?? SimulationRunner.DefaultSeed).ToString(System.Globalization.CultureInfo.InvariantCulture);
            SyncToolbarVirtualDurationFromEditor(loaded.Limits.MaxVirtualMinutes);
        }
        catch (Exception ex)
        {
            Diagnostics.SetError("Scenario Load Error", ex.Message);
        }
    }

    private Task OnScenarioSavedAsync(string savedPath)
    {
        if (_hostService == null)
            return Task.CompletedTask;

        var scenarios = _hostService.DiscoverScenarios();
        AvailableScenarios.Clear();
        foreach (var s in scenarios)
            AvailableScenarios.Add(s);

        _suppressScenarioSelectionSync = true;
        try
        {
            ScenarioPathText = savedPath;
            SelectedScenario = AvailableScenarios.FirstOrDefault(s =>
                s.FilePath != null && string.Equals(Path.GetFullPath(s.FilePath), Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressScenarioSelectionSync = false;
        }
        return Task.CompletedTask;
    }

    private void RefreshSkuaConfigurationState()
    {
        if (_hostService == null)
            return;

        IsSkuaConfigured = _hostService.IsReady;
        SkuaSetupWarning = _hostService.ErrorMessage;
        ClientFlavorText = _hostService.IsReady
            ? $"{_hostService.ClientFlavor} ({_hostService.AssembliesDirectory})"
            : "Skua/VibeSkua not detected";
        RefreshCommandStates();
    }

    private void RefreshCommandStates()
    {
        CompileCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
        PauseResumeCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private static string FormatOutcomeStatus(SimulationReport report) => report.Outcome switch
    {
        SimulationOutcome.Completed => $"Completed in {FormatDuration(report.VirtualDuration)} virtual",
        SimulationOutcome.StoppedByScript => $"Stopped by script ({report.StopReason ?? "Bot.Stop()"})",
        SimulationOutcome.LimitReached => $"Stopped ({report.StopReason ?? "limit reached"})",
        SimulationOutcome.ScriptException => "Failed — Unhandled script exception",
        SimulationOutcome.CompileError => $"Failed — {report.CompileErrors.Count} compile error(s)",
        SimulationOutcome.Stalled => "Stalled — no progress, likely an infinite loop (see Errors & Diagnostics)",
        _ => report.Outcome.ToString()
    };

    private static string FormatDuration(TimeSpan t) =>
        t.Days > 0
            ? $"{t.Days}d {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}"
            : $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";

    private static string FormatRealTime(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        if (t.TotalMinutes >= 1)
            return $"{t.Minutes:00}m {t.Seconds:00}s";
        return $"{t.TotalSeconds:F2} s";
    }
}
