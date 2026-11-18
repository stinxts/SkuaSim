namespace SkuaSim.UI.Services;

public interface IFileDialogService
{
    Task<string?> PickOpenFileAsync(string title, string? initialDirectory, IReadOnlyList<(string Name, string[] Patterns)> filters);
    Task<string?> PickFolderAsync(string title, string? initialDirectory);
    Task CopyTextAsync(string text);
}
