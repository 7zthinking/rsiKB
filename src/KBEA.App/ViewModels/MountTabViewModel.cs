using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KBEA.Core.Files;
using KBEA.Core.Git;
using KBEA.Core.Models;
using KBEA.Core.Storage;

namespace KBEA.App.ViewModels;

public sealed partial class MountTabViewModel : ObservableObject
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".json", ".xml", ".csv", ".log", ".cs", ".xaml", ".sql", ".py", ".js", ".html", ".css", ".yml", ".yaml" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
    private readonly FileBrowserService _files;
    private readonly GitService _git;
    private readonly MetadataStore? _metadata;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private string _currentPath;
    private string _addressText = string.Empty;
    private FileEntry? _selectedFile;
    private string _previewText = "选择文本文件可在此预览。";
    private BitmapImage? _previewImage;
    private string _searchText = string.Empty;
    private GitStatusInfo _gitStatus = new(false, 0, "", "", 0);
    private bool _isPreviewVisible = true;

    public MountTabViewModel(MountDefinition mount, WorkspaceDefinition workspace, FileBrowserService files, GitService git)
    {
        Mount = mount;
        Workspace = workspace;
        RootPath = mount.Path;
        _files = files;
        _git = git;
        _metadata = new MetadataStore(mount.Path);
        _currentPath = mount.Path;
        Title = mount.Name;
        Initialize();
    }

    public MountTabViewModel(string path, FileBrowserService files, GitService git)
    {
        var fullPath = Path.GetFullPath(path);
        var rootPath = Path.GetPathRoot(fullPath) ?? fullPath;
        Mount = new MountDefinition { Name = "本地目录", Path = rootPath };
        Workspace = new WorkspaceDefinition { Name = "本地目录" };
        RootPath = rootPath;
        IsLocal = true;
        _files = files;
        _git = git;
        _currentPath = fullPath;
        Title = new DirectoryInfo(fullPath).Name is { Length: > 0 } name ? name : fullPath;
        Initialize();
    }

    private void Initialize()
    {
        _addressText = CurrentPath;
        BackCommand = new RelayCommand(Back, () => _back.Count > 0);
        ForwardCommand = new RelayCommand(Forward, () => _forward.Count > 0);
        UpCommand = new RelayCommand(Up, CanGoUp);
        RefreshCommand = new RelayCommand(Refresh);
        OpenCommand = new RelayCommand(OpenSelected, () => SelectedFile is not null);
        SearchCommand = new RelayCommand(Search);
        NavigateAddressCommand = new RelayCommand(() =>
        {
            if (!Directory.Exists(AddressText)) { AddressText = CurrentPath; return; }
            var fullPath = Path.GetFullPath(AddressText);
            var rootPrefix = RootPath.EndsWith(Path.DirectorySeparatorChar) ? RootPath : RootPath + Path.DirectorySeparatorChar;
            if (!IsLocal && !fullPath.Equals(RootPath, StringComparison.OrdinalIgnoreCase) && !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                AddressText = CurrentPath;
                return;
            }
            if (IsLocal)
            {
                var newRoot = Path.GetPathRoot(fullPath) ?? RootPath;
                if (!newRoot.Equals(RootPath, StringComparison.OrdinalIgnoreCase))
                {
                    RootPath = newRoot;
                    Mount.Path = newRoot;
                    Directories.Clear();
                    Directories.Add(new FolderNodeViewModel(this, RootPath, RootPath, _files));
                }
            }
            Navigate(fullPath);
        });
        TogglePreviewCommand = new RelayCommand(() => IsPreviewVisible = !IsPreviewVisible);
        Directories.Add(new FolderNodeViewModel(this, RootPath, IsLocal ? RootPath : Mount.Name, _files));
        Refresh();
    }

    public MountDefinition Mount { get; }
    public WorkspaceDefinition Workspace { get; }
    public string RootPath { get; private set; }
    public bool IsLocal { get; }
    public string Title { get; }
    public event EventHandler? SelectionChanged;
    public ObservableCollection<FileEntry> Files { get; } = [];
    public ObservableCollection<FolderNodeViewModel> Directories { get; } = [];
    public RelayCommand BackCommand { get; private set; } = null!;
    public RelayCommand ForwardCommand { get; private set; } = null!;
    public RelayCommand UpCommand { get; private set; } = null!;
    public RelayCommand RefreshCommand { get; private set; } = null!;
    public RelayCommand OpenCommand { get; private set; } = null!;
    public RelayCommand SearchCommand { get; private set; } = null!;
    public RelayCommand NavigateAddressCommand { get; private set; } = null!;
    public RelayCommand TogglePreviewCommand { get; private set; } = null!;
    public string CurrentPath { get => _currentPath; private set => SetProperty(ref _currentPath, value); }
    public string AddressText { get => _addressText; set => SetProperty(ref _addressText, value); }
    public bool IsOnline => Directory.Exists(RootPath);
    public string OnlineText => IsOnline ? "在线" : "离线";
    public string SyncText => IsLocal ? "本地浏览" : Mount.LastSyncAt.HasValue ? $"同步 {Mount.LastSyncAt.Value:MM-dd HH:mm}" : "尚未同步";
    public bool HasImagePreview => PreviewImage is not null;
    public bool HasTextPreview => PreviewImage is null;

    public FileEntry? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                OpenCommand.NotifyCanExecuteChanged();
                UpdatePreview();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string PreviewText { get => _previewText; private set => SetProperty(ref _previewText, value); }
    public BitmapImage? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (SetProperty(ref _previewImage, value))
            {
                OnPropertyChanged(nameof(HasImagePreview));
                OnPropertyChanged(nameof(HasTextPreview));
            }
        }
    }

    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
    public GitStatusInfo GitStatus { get => _gitStatus; private set { if (SetProperty(ref _gitStatus, value)) OnPropertyChanged(nameof(CanUseGit)); } }
    public bool CanUseGit => GitStatus.IsRepository && IsOnline;
    public bool IsPreviewVisible { get => _isPreviewVisible; set => SetProperty(ref _isPreviewVisible, value); }

    public void Navigate(string path, bool recordHistory = true)
    {
        if (!Directory.Exists(path)) return;
        if (recordHistory && !string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _back.Push(CurrentPath);
            _forward.Clear();
        }
        CurrentPath = path;
        AddressText = path;
        LoadFiles(_files.GetEntries(path, RootPath));
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        UpCommand.NotifyCanExecuteChanged();
    }

    public void Refresh()
    {
        GitStatus = _git.GetStatus(RootPath);
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(OnlineText));
        OnPropertyChanged(nameof(SyncText));
        if (IsOnline) LoadFiles(_files.GetEntries(CurrentPath, RootPath));
    }

    public int RebuildIndex() => _metadata?.IndexFiles() ?? Files.Count;

    public void SelectCatalogFile(string fullPath)
    {
        var directory = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath)! : fullPath;
        Navigate(directory);
        SelectedFile = Files.FirstOrDefault(x => x.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadFiles(IEnumerable<FileEntry> entries)
    {
        var states = _git.GetFileStates(RootPath);
        Files.Clear();
        foreach (var entry in entries)
        {
            states.TryGetValue(entry.RelativePath, out var state);
            Files.Add(entry with { GitState = state ?? string.Empty });
        }
        SelectedFile = null;
    }

    private void Back()
    {
        if (_back.Count == 0) return;
        _forward.Push(CurrentPath);
        Navigate(_back.Pop(), false);
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
    }

    private void Forward()
    {
        if (_forward.Count == 0) return;
        _back.Push(CurrentPath);
        Navigate(_forward.Pop(), false);
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
    }

    private bool CanGoUp() => IsLocal
        ? Directory.GetParent(CurrentPath) is not null
        : !string.Equals(CurrentPath, RootPath, StringComparison.OrdinalIgnoreCase);

    private void Up()
    {
        var parent = Directory.GetParent(CurrentPath)?.FullName;
        if (parent is null || (!IsLocal && parent.Length < RootPath.Length)) return;
        Navigate(parent);
    }

    private void OpenSelected()
    {
        if (SelectedFile is null) return;
        if (SelectedFile.IsDirectory) Navigate(SelectedFile.FullPath);
        else Process.Start(new ProcessStartInfo(SelectedFile.FullPath) { UseShellExecute = true });
    }

    private void Search()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) { Refresh(); return; }
        LoadFiles(_metadata is null
            ? _files.SearchEntries(CurrentPath, CurrentPath, SearchText)
            : _metadata.Search(SearchText));
    }

    private void UpdatePreview()
    {
        PreviewImage = null;
        if (SelectedFile is null || SelectedFile.IsDirectory || !File.Exists(SelectedFile.FullPath))
        {
            PreviewText = SelectedFile?.IsDirectory == true ? "文件夹。双击可打开。" : "选择文本文件或图片可在此预览。";
            return;
        }
        try
        {
            if (ImageExtensions.Contains(Path.GetExtension(SelectedFile.Name)))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(SelectedFile.FullPath);
                image.EndInit();
                image.Freeze();
                PreviewImage = image;
                PreviewText = string.Empty;
            }
            else if (TextExtensions.Contains(Path.GetExtension(SelectedFile.Name))) PreviewText = _files.ReadTextPreview(SelectedFile.FullPath);
            else PreviewText = "此格式暂不支持内置预览。双击可使用系统默认程序打开。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            PreviewText = $"预览失败：{ex.Message}";
        }
    }
}

public sealed class FolderNodeViewModel : ObservableObject
{
    private readonly FileBrowserService _files;
    private bool _loaded;

    public FolderNodeViewModel(MountTabViewModel owner, string fullPath, string name, FileBrowserService files, bool placeholder = false)
    {
        Owner = owner;
        FullPath = fullPath;
        Name = name;
        _files = files;
        if (!placeholder) EnsureDummy();
    }

    public MountTabViewModel Owner { get; }
    public string FullPath { get; }
    public string Name { get; }
    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    public void Expand()
    {
        if (_loaded) return;
        _loaded = true;
        Children.Clear();
        foreach (var directory in _files.GetDirectories(FullPath, Owner.RootPath))
            Children.Add(new FolderNodeViewModel(Owner, directory.FullPath, directory.Name, _files));
    }

    private void EnsureDummy()
    {
        try
        {
            if (Directory.EnumerateDirectories(FullPath).Any(x => !Path.GetFileName(x).StartsWith('.'))) Children.Add(new FolderNodeViewModel(Owner, FullPath, "正在加载…", _files, placeholder: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
