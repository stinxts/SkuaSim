using System.Collections.ObjectModel;
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
    private bool _suppressScriptSelectionSync;
    private bool _suppressScenarioSelectionSync;

    public MainViewModel()
    {
        ClientState = new ClientStateViewModel();
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
    public DiagnosticsViewModel Diagnostics { get; }
    public ObservableCollection<SpeedPreset> SpeedPresets { get; }
    public ObservableCollection<ScriptEntry> FilteredScripts { get; } = new();
    public ObservableCollection<ScenarioEntry> AvailableScenarios { get; } = new();

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

        int? seedOverride = int.TryParse(SeedText.Trim(), out var parsedSeed) ? parsedSeed : null;

        ClientState.Reset();
        Diagnostics.Reset();
        Diagnostics.SelectedTabIndex = 0;
        _logCursor = 0;
        _warnCursor = 0;

        VirtualClockText = "00:00:00.000";
        RealTimeText = "0.00 s";
        PausedTimeText = "0.00 s";
        ApiCallsText = "0";
        LastApiCallText = "-";

        try
        {
            var session = _hostService.StartSimulation(scriptPath, scenarioPath, seedOverride, SelectedSpeed.Multiplier);
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
            ScenarioPathText = picked;
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
            }
        }
        finally
        {
            _suppressScenarioSelectionSync = false;
        }

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
        if (report.Outcome is SimulationOutcome.CompileError or SimulationOutcome.ScriptException)
            Diagnostics.SelectedTabIndex = 1;

        RefreshCommandStates();
    }

    private void ApplyLiveSnapshot(SimulationLiveSnapshot snap)
    {
        VirtualClockText = FormatDuration(snap.VirtualTime);
        RealTimeText = $"{snap.ActiveRealTime.TotalSeconds:F2} s";
        PausedTimeText = $"{snap.PausedTime.TotalSeconds:F2} s";
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
            if (logs.Count > 0)
                Diagnostics.AppendLogs(logs);
            if (warns.Count > 0)
                Diagnostics.AppendWarnings(warns);
            if (logs.Count == 0 && warns.Count == 0)
                break;
        }

        Diagnostics.SyncDiagnostics(
            session.GetSynthesizedSnapshot(),
            session.GetUnimplementedSnapshot());
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
        _ => report.Outcome.ToString()
    };

    private static string FormatDuration(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
}
