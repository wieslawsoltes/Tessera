using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Tessera.Services;

/// <summary>
/// The native app works with local file paths. This small boundary permits UI
/// tests to supply picker decisions while still running real file IO, serializers,
/// validation, command handlers and error paths. It never simulates a terminal.
/// </summary>
public interface IWorkspaceFileDialogs
{
    Task<IReadOnlyList<string>> OpenFilesAsync(Window owner, FilePickerOpenOptions options);
    Task<string?> SaveFileAsync(Window owner, FilePickerSaveOptions options);
    Task<string?> OpenFolderAsync(Window owner, FolderPickerOpenOptions options);
}

internal sealed class NativeWorkspaceFileDialogs : IWorkspaceFileDialogs
{
    public async Task<IReadOnlyList<string>> OpenFilesAsync(Window owner, FilePickerOpenOptions options)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(options);
        try { return files.Select(LocalPath).ToArray(); }
        finally { foreach (var file in files) file.Dispose(); }
    }

    public async Task<string?> SaveFileAsync(Window owner, FilePickerSaveOptions options)
    {
        using var file = await owner.StorageProvider.SaveFilePickerAsync(options);
        return file is null ? null : LocalPath(file);
    }

    public async Task<string?> OpenFolderAsync(Window owner, FolderPickerOpenOptions options)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(options);
        try { return folders.FirstOrDefault() is { } folder ? LocalPath(folder) : null; }
        finally { foreach (var folder in folders) folder.Dispose(); }
    }

    private static string LocalPath(IStorageItem item) => item.TryGetLocalPath()
        ?? throw new NotSupportedException("Tessera requires a local filesystem path for this operation.");
}
