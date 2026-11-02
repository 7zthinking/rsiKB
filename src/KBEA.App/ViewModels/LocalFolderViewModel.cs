using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using KBEA.Core.Files;

namespace KBEA.App.ViewModels;

public sealed class LocalFolderViewModel : ObservableObject
{
    private readonly FileBrowserService _files;
    private bool _loaded;

    public LocalFolderViewModel(string fullPath, string name, FileBrowserService files, bool placeholder = false)
    {
        FullPath = fullPath;
        Name = name;
        _files = files;
        if (!placeholder) EnsureDummy();
    }

    public string FullPath { get; }
    public string Name { get; }
    public ObservableCollection<LocalFolderViewModel> Children { get; } = [];
    public bool IsExpanded { get; set; }

    public void Expand()
    {
        if (_loaded) return;
        _loaded = true;
        Children.Clear();
        foreach (var directory in _files.GetDirectories(FullPath, FullPath))
            Children.Add(new LocalFolderViewModel(directory.FullPath, directory.Name, _files));
    }

    private void EnsureDummy()
    {
        try
        {
            if (Directory.EnumerateDirectories(FullPath).Any(x => !Path.GetFileName(x).StartsWith('.')))
                Children.Add(new LocalFolderViewModel(FullPath, "正在加载…", _files, placeholder: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
