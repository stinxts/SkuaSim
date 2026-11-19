using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace SkuaSim.UI.Services;

public sealed class WindowFileDialogService : IFileDialogService
{
    private readonly Func<Window?> _windowProvider;

    public WindowFileDialogService(Func<Window?> windowProvider) => _windowProvider = windowProvider;

    public async Task<string?> PickOpenFileAsync(
        string title,
        string? initialDirectory,
        IReadOnlyList<(string Name, string[] Patterns)> filters)
    {
        var window = _windowProvider();
        var topLevel = TopLevel.GetTopLevel(window);
        if (topLevel?.StorageProvider == null)
            return null;

        IStorageFolder? startFolder = null;
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            try
            {
                startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(initialDirectory)));
            }
            catch { }
        }

        var fileTypes = filters
            .Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns })
            .ToList();

        var results = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter = fileTypes
        });

        return results.Count > 0 ? results[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(
        string title,
        string? initialDirectory,
        string? suggestedFileName,
        string? defaultExtension,
        IReadOnlyList<(string Name, string[] Patterns)> filters)
    {
        var window = _windowProvider();
        var topLevel = TopLevel.GetTopLevel(window);
        if (topLevel?.StorageProvider == null)
            return null;

        IStorageFolder? startFolder = null;
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            try
            {
                startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(initialDirectory)));
            }
            catch { }
        }

        var fileTypes = filters
            .Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns })
            .ToList();

        var result = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedStartLocation = startFolder,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = defaultExtension,
            FileTypeChoices = fileTypes,
            ShowOverwritePrompt = true
        });

        return result?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title, string? initialDirectory)
    {
        var window = _windowProvider();
        var topLevel = TopLevel.GetTopLevel(window);
        if (topLevel?.StorageProvider == null)
            return null;

        IStorageFolder? startFolder = null;
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            try
            {
                startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(initialDirectory)));
            }
            catch { }
        }

        var results = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder
        });

        return results.Count > 0 ? results[0].TryGetLocalPath() : null;
    }

    public async Task CopyTextAsync(string text)
    {
        var window = _windowProvider();
        var topLevel = TopLevel.GetTopLevel(window);
        if (topLevel?.Clipboard == null)
            return;

        await topLevel.Clipboard.SetTextAsync(text);
    }
}
