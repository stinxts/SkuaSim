using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkuaSim.Core.Compilation;
using SkuaSim.Core.Runtime;
using SkuaSim.UI.Models;
using SkuaSim.UI.Services;

namespace SkuaSim.UI.ViewModels;

public partial class DiagnosticsViewModel : ViewModelBase
{
    private const int MaxUiLogRows = 4_000;
    private const int MaxUiWarningRows = 2_000;
    private const int MaxUiTraceRows = 5_000;

    private IFileDialogService? _dialogService;
    private SimulationReport? _lastReport;

    [ObservableProperty]
    public partial bool HasFatalError { get; set; }

    [ObservableProperty]
    public partial string FatalErrorTitle { get; set; } = "";

    [ObservableProperty]
    public partial string FatalErrorDetails { get; set; } = "";

    [ObservableProperty]
    public partial bool HasCompileErrors { get; set; }

    [ObservableProperty]
    public partial string LogFilter { get; set; } = "";

    [ObservableProperty]
    public partial bool AutoScrollLogs { get; set; } = true;

    [ObservableProperty]
    public partial int TotalLogsCount { get; set; }

    [ObservableProperty]
    public partial int TotalWarningsCount { get; set; }

    [ObservableProperty]
    public partial string LogsTabHeader { get; set; } = "Logs (0)";

    [ObservableProperty]
    public partial string ErrorsTabHeader { get; set; } = "Errors & Diagnostics";

    [ObservableProperty]
    public partial string TraceTabHeader { get; set; } = "API Trace (0)";

    [ObservableProperty]
    public partial string TraceFilter { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedTraceKind { get; set; } = "All";

    [ObservableProperty]
    public partial bool AutoScrollTrace { get; set; } = true;

    [ObservableProperty]
    public partial int TotalTraceCount { get; set; }

    [ObservableProperty]
    public partial string TraceNoticeText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasReport { get; set; }

    [ObservableProperty]
    public partial string ExportStatusText { get; set; } = "";

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    public IReadOnlyList<string> TraceKindOptions { get; } = new[] { "All", "api", "world", "log", "warn", "synth", "info" };

    public ObservableCollection<LogRowItem> AllLogs { get; } = new();
    public ObservableCollection<LogRowItem> FilteredLogs { get; } = new();
    public ObservableCollection<LogRowItem> Warnings { get; } = new();
    public ObservableCollection<CompileErrorRowItem> CompileErrors { get; } = new();
    public ObservableCollection<DiagnosticRowItem> SynthesizedItems { get; } = new();
    public ObservableCollection<DiagnosticRowItem> UnimplementedCalls { get; } = new();
    public ObservableCollection<TraceRowItem> AllTraceEntries { get; } = new();
    public ObservableCollection<TraceRowItem> FilteredTraceEntries { get; } = new();
    public ObservableCollection<DiagnosticRowItem> TopApiCalls { get; } = new();

    public void AttachDialogService(IFileDialogService dialogService) => _dialogService = dialogService;

    partial void OnLogFilterChanged(string value) => RebuildFilteredLogs();
    partial void OnTraceFilterChanged(string value) => RebuildFilteredTrace();
    partial void OnSelectedTraceKindChanged(string value) => RebuildFilteredTrace();
    partial void OnHasReportChanged(bool value)
    {
        ExportMarkdownReportCommand.NotifyCanExecuteChanged();
        ExportJsonReportCommand.NotifyCanExecuteChanged();
    }

    public void Reset()
    {
        HasFatalError = false;
        FatalErrorTitle = "";
        FatalErrorDetails = "";
        HasCompileErrors = false;
        TotalLogsCount = 0;
        TotalWarningsCount = 0;
        TotalTraceCount = 0;
        TraceNoticeText = "";
        ExportStatusText = "";
        _lastReport = null;
        HasReport = false;

        AllLogs.Clear();
        FilteredLogs.Clear();
        Warnings.Clear();
        CompileErrors.Clear();
        SynthesizedItems.Clear();
        UnimplementedCalls.Clear();
        AllTraceEntries.Clear();
        FilteredTraceEntries.Clear();
        TopApiCalls.Clear();
        UpdateTabHeaders();
    }

    public void AppendTimeline(IReadOnlyList<IndexedTraceEntry> entries, bool droppedAny)
    {
        if (droppedAny && string.IsNullOrEmpty(TraceNoticeText))
            TraceNoticeText = "Ring buffer wrapped — showing most recent events.";

        if (entries.Count == 0)
            return;

        var filter = (TraceFilter ?? "").Trim();
        var kind = SelectedTraceKind ?? "All";
        foreach (var item in entries)
        {
            var e = item.Entry;
            var row = new TraceRowItem(item.Seq, e.Clock, e.Kind, e.Thread, e.Text);
            AllTraceEntries.Add(row);
            TotalTraceCount++;
            if (MatchesTraceFilter(row, filter, kind))
                FilteredTraceEntries.Add(row);
        }

        while (AllTraceEntries.Count > MaxUiTraceRows)
            AllTraceEntries.RemoveAt(0);
        while (FilteredTraceEntries.Count > MaxUiTraceRows)
            FilteredTraceEntries.RemoveAt(0);

        UpdateTabHeaders();
    }

    public void AppendLogs(IReadOnlyList<TraceEntry> entries)
    {
        if (entries.Count == 0)
            return;

        var filter = (LogFilter ?? "").Trim();
        foreach (var e in entries)
        {
            var row = new LogRowItem(e.Clock, e.Thread, e.Text);
            AllLogs.Add(row);
            TotalLogsCount++;
            if (MatchesFilter(row, filter))
                FilteredLogs.Add(row);
        }

        while (AllLogs.Count > MaxUiLogRows)
            AllLogs.RemoveAt(0);
        while (FilteredLogs.Count > MaxUiLogRows)
            FilteredLogs.RemoveAt(0);

        UpdateTabHeaders();
    }

    public void AppendWarnings(IReadOnlyList<TraceEntry> entries)
    {
        if (entries.Count == 0)
            return;

        foreach (var e in entries)
        {
            Warnings.Add(new LogRowItem(e.Clock, e.Thread, e.Text, IsWarning: true));
            TotalWarningsCount++;
        }

        while (Warnings.Count > MaxUiWarningRows)
            Warnings.RemoveAt(0);

        UpdateTabHeaders();
    }

    public void SyncDiagnostics(
        IReadOnlyList<SynthesizedDataRecord> synthesized,
        IReadOnlyDictionary<string, long> unimplemented)
    {
        var synthRows = synthesized
            .Select(s => new DiagnosticRowItem("SYNTH", s.Key, s.Description, s.Count))
            .ToList();
        SyncCollection(SynthesizedItems, synthRows);

        var unimplRows = unimplemented
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new DiagnosticRowItem("STUB", kv.Key, "API member called without full simulation", kv.Value))
            .ToList();
        SyncCollection(UnimplementedCalls, unimplRows);

        UpdateTabHeaders();
    }

    public void SetCompileFailure(string scriptPath, IReadOnlyList<CompileDiagnostic> errors)
    {
        HasFatalError = true;
        HasCompileErrors = errors.Count > 0;
        FatalErrorTitle = $"Compile Error ({errors.Count} error(s)) — {Path.GetFileName(scriptPath)}";
        FatalErrorDetails = string.Join(Environment.NewLine, errors.Select(e => e.ToString()));

        CompileErrors.Clear();
        foreach (var err in errors)
            CompileErrors.Add(new CompileErrorRowItem(err.File, err.Line, err.Column, err.Id, err.Message));

        UpdateTabHeaders();
    }

    public void SetCompileSuccess(ScriptCompileResult result)
    {
        HasFatalError = false;
        HasCompileErrors = false;
        FatalErrorTitle = "";
        FatalErrorDetails = "";
        CompileErrors.Clear();

        var includesText = result.IncludedFiles.Count > 0
            ? $"{result.IncludedFiles.Count} include(s)"
            : "standalone script";
        AppendLogs(new[]
        {
            new TraceEntry(0, "00:00:00.000", "log", "ui",
                $"Compile succeeded in {result.Elapsed.TotalMilliseconds:F0} ms ({includesText}, {result.WarningCount} Roslyn warning(s)).")
        });
        UpdateTabHeaders();
    }

    public void SetError(string title, string details)
    {
        HasFatalError = true;
        HasCompileErrors = false;
        FatalErrorTitle = title;
        FatalErrorDetails = details;
        CompileErrors.Clear();
        UpdateTabHeaders();
    }

    public void ApplyReportOutcome(SimulationReport report)
    {
        if (report.Outcome == SimulationOutcome.CompileError)
        {
            SetCompileFailure(report.ScriptPath, report.CompileErrors);
        }
        else if (report.Outcome == SimulationOutcome.ScriptException)
        {
            HasFatalError = true;
            HasCompileErrors = false;
            FatalErrorTitle = $"Unhandled Script Exception — {Path.GetFileName(report.ScriptPath)}";
            FatalErrorDetails = report.ExceptionText ?? report.StopReason ?? "Script threw an unhandled exception.";
            CompileErrors.Clear();
        }
        else if (report.Outcome == SimulationOutcome.Stalled)
        {
            HasFatalError = true;
            HasCompileErrors = false;
            FatalErrorTitle = $"Stalled (No Progress) — {Path.GetFileName(report.ScriptPath)}";
            FatalErrorDetails = report.StopReason ?? "The player made no progress for the configured virtual time.";
            CompileErrors.Clear();
        }
        else
        {
            HasFatalError = false;
            HasCompileErrors = false;
            FatalErrorTitle = "";
            FatalErrorDetails = "";
            CompileErrors.Clear();
        }

        _lastReport = report;
        HasReport = true;

        var topCalls = report.TopApiCalls
            .OrderByDescending(kv => kv.Value)
            .Take(25)
            .Select(kv => new DiagnosticRowItem("API", kv.Key, "", kv.Value))
            .ToList();
        SyncCollection(TopApiCalls, topCalls);

        SyncDiagnostics(report.SynthesizedData, report.UnimplementedCalls);
        UpdateTabHeaders();
    }

    [RelayCommand]
    private void ClearLogs()
    {
        AllLogs.Clear();
        FilteredLogs.Clear();
        TotalLogsCount = 0;
        UpdateTabHeaders();
    }

    [RelayCommand]
    private void ClearTrace()
    {
        AllTraceEntries.Clear();
        FilteredTraceEntries.Clear();
        TotalTraceCount = 0;
        TraceNoticeText = "";
        UpdateTabHeaders();
    }

    private bool CanExportReport() => HasReport && _lastReport != null;

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportMarkdownReportAsync()
    {
        if (_dialogService == null || _lastReport == null)
            return;

        var baseName = Path.GetFileNameWithoutExtension(_lastReport.ScriptPath);
        var path = await _dialogService.PickSaveFileAsync(
            "Export Simulation Report (Markdown)",
            Directory.Exists("data") ? Path.GetFullPath("data") : Directory.GetCurrentDirectory(),
            $"{baseName}_report.md",
            "md",
            new[] { ("Markdown Report", new[] { "*.md" }), ("All Files", new[] { "*.*" }) });

        if (string.IsNullOrWhiteSpace(path))
            return;

        await File.WriteAllTextAsync(path, _lastReport.ToMarkdown());
        ExportStatusText = $"Saved {Path.GetFileName(path)}";
    }

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportJsonReportAsync()
    {
        if (_dialogService == null || _lastReport == null)
            return;

        var baseName = Path.GetFileNameWithoutExtension(_lastReport.ScriptPath);
        var path = await _dialogService.PickSaveFileAsync(
            "Export Simulation Report (JSON)",
            Directory.Exists("data") ? Path.GetFullPath("data") : Directory.GetCurrentDirectory(),
            $"{baseName}_report.json",
            "json",
            new[] { ("JSON Report", new[] { "*.json" }), ("All Files", new[] { "*.*" }) });

        if (string.IsNullOrWhiteSpace(path))
            return;

        await File.WriteAllTextAsync(path, _lastReport.ToJson());
        ExportStatusText = $"Saved {Path.GetFileName(path)}";
    }

    [RelayCommand]
    private async Task CopyErrorAsync()
    {
        if (_dialogService == null || string.IsNullOrWhiteSpace(FatalErrorDetails))
            return;
        var payload = string.IsNullOrWhiteSpace(FatalErrorTitle)
            ? FatalErrorDetails
            : $"{FatalErrorTitle}{Environment.NewLine}{Environment.NewLine}{FatalErrorDetails}";
        await _dialogService.CopyTextAsync(payload);
    }

    private void RebuildFilteredLogs()
    {
        var filter = (LogFilter ?? "").Trim();
        var next = AllLogs.Where(l => MatchesFilter(l, filter)).ToList();
        SyncCollection(FilteredLogs, next);
    }

    private static bool MatchesFilter(LogRowItem row, string filter) =>
        string.IsNullOrEmpty(filter) ||
        row.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        row.Clock.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void RebuildFilteredTrace()
    {
        var filter = (TraceFilter ?? "").Trim();
        var kind = SelectedTraceKind ?? "All";
        var next = AllTraceEntries.Where(t => MatchesTraceFilter(t, filter, kind)).ToList();
        SyncCollection(FilteredTraceEntries, next);
    }

    private static bool MatchesTraceFilter(TraceRowItem row, string filter, string kind)
    {
        if (!string.Equals(kind, "All", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(row.Kind, kind, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrEmpty(filter) ||
               row.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               row.Clock.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               row.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateTabHeaders()
    {
        LogsTabHeader = $"Logs ({TotalLogsCount:N0})";
        var parts = new List<string>();
        if (HasFatalError) parts.Add("ERR");
        if (TotalWarningsCount > 0) parts.Add($"{TotalWarningsCount} warn");
        if (SynthesizedItems.Count > 0) parts.Add($"{SynthesizedItems.Count} synth");
        ErrorsTabHeader = parts.Count > 0
            ? $"Errors & Diagnostics ({string.Join(", ", parts)})"
            : "Errors & Diagnostics";
        TraceTabHeader = $"API Trace ({TotalTraceCount:N0})";
    }

    private static void SyncCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> next)
    {
        if (target.Count == next.Count && target.SequenceEqual(next))
            return;
        target.Clear();
        foreach (var item in next)
            target.Add(item);
    }
}
