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
    public const string ComputerPath = "我的电脑";
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

    public MountTabViewModel(SpaceDefinition space, FileBrowserService files, GitService git)
    {
        Space = space;
        RootPath = space.Path;
        _files = files;
        _git = git;
        _metadata = new MetadataStore(space.Path);
        _currentPath = space.Path;
        Initialize();
    }

    public MountTabViewModel(string path, FileBrowserService files, GitService git)
    {
        var fullPath = Path.GetFullPath(path);
        var rootPath = Path.GetPathRoot(fullPath) ?? fullPath;
        Space = new SpaceDefinition { Name = "本地目录", Path = rootPath };
        RootPath = rootPath;
        IsLocal = true;
        _files = files;
        _git = git;
        _currentPath = fullPath;
        Initialize();
    }

    public MountTabViewModel(FileBrowserService files, GitService git)
    {
        Space = new SpaceDefinition { Name = ComputerPath, Path = ComputerPath };
        RootPath = ComputerPath;
        IsLocal = true;
        _files = files;
        _git = git;
        _currentPath = ComputerPath;
        Initialize();
    }

    private MountTabViewModel()
    {
        Space = new SpaceDefinition { Name = "＋", Path = string.Empty };
        RootPath = string.Empty;
        IsLocal = true;
        IsPlaceholder = true;
        _files = null!;
        _git = null!;
        _metadata = null;
        _currentPath = string.Empty;
    }

    /// <summary>标签栏末尾的“＋”占位标签，固定宽度，不参与选中与关闭。</summary>
    public static MountTabViewModel CreatePlaceholder() => new();

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
            if (AddressText.Trim().Equals(ComputerPath, StringComparison.OrdinalIgnoreCase)) { NavigateComputer(); return; }
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
                    Space.Path = newRoot;
                    Directories.Clear();
                    Directories.Add(new FolderNodeViewModel(this, RootPath, RootPath, _files));
                }
            }
            Navigate(fullPath);
        });
        TogglePreviewCommand = new RelayCommand(() => IsPreviewVisible = !IsPreviewVisible);
        if (!IsComputer) Directories.Add(new FolderNodeViewModel(this, RootPath, IsLocal ? RootPath : Space.Name, _files));
        Refresh();
    }

    public SpaceDefinition Space { get; }
    public string RootPath { get; private set; }
    public bool IsLocal { get; }
    /// <summary>占位标签：仅用于在标签栏末尾显示“＋”，不代表真实的浏览标签。</summary>
    public bool IsPlaceholder { get; init; }
    public bool IsComputer => CurrentPath.Equals(ComputerPath, StringComparison.OrdinalIgnoreCase);
    public string Title => IsComputer ? ComputerPath : new DirectoryInfo(CurrentPath).Name is { Length: > 0 } name ? name : CurrentPath;
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
    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (!SetProperty(ref _currentPath, value)) return;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsComputer));
        }
    }
    public string AddressText { get => _addressText; set => SetProperty(ref _addressText, value); }
    public bool IsOnline => IsComputer || Directory.Exists(RootPath);
    public string OnlineText => IsOnline ? "在线" : "离线";
    public string SyncText => IsLocal ? "本地浏览" : Space.LastSyncAt.HasValue ? $"同步 {Space.LastSyncAt.Value:MM-dd HH:mm}" : "尚未同步";
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
    private string _gitRepositoryRoot = string.Empty;
    public string GitRepositoryRoot { get => _gitRepositoryRoot; private set => SetProperty(ref _gitRepositoryRoot, value); }
    public event EventHandler? GitStatusChanged;
    public GitStatusInfo GitStatus
    {
        get => _gitStatus;
        private set
        {
            if (!SetProperty(ref _gitStatus, value)) return;
            OnPropertyChanged(nameof(CanUseGit));
            GitStatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
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
        if (IsComputer)
        {
            RootPath = Path.GetPathRoot(path) ?? path;
            Space.Path = RootPath;
        }
        CurrentPath = path;
        AddressText = path;
        RefreshGitStatus();
        LoadFiles(_files.GetEntries(path, RootPath));
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        UpCommand.NotifyCanExecuteChanged();
    }

    private void NavigateComputer(bool recordHistory = true)
    {
        if (recordHistory && !IsComputer)
        {
            _back.Push(CurrentPath);
            _forward.Clear();
        }
        RootPath = ComputerPath;
        Space.Path = ComputerPath;
        CurrentPath = ComputerPath;
        AddressText = ComputerPath;
        RefreshGitStatus();
        LoadComputer();
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        UpCommand.NotifyCanExecuteChanged();
    }

    private void LoadComputer()
    {
        Files.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel;
                var name = $"{label} ({drive.Name.TrimEnd('\\')})";
                Files.Add(new FileEntry(name, drive.RootDirectory.FullName, name, true, 0, DateTime.Now));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        SelectedFile = null;
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(OnlineText));
        OnPropertyChanged(nameof(SyncText));
        RefreshGitStatus();
        if (IsComputer) LoadComputer();
        else if (Directory.Exists(CurrentPath)) LoadFiles(_files.GetEntries(CurrentPath, RootPath));
    }

    private void RefreshGitStatus()
    {
        GitRepositoryRoot = _git.DiscoverRepositoryRoot(CurrentPath) ?? string.Empty;
        GitStatus = string.IsNullOrEmpty(GitRepositoryRoot)
            ? new GitStatusInfo(false, 0, string.Empty, string.Empty, 0)
            : _git.GetStatus(GitRepositoryRoot);
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
        var states = string.IsNullOrEmpty(GitRepositoryRoot) ? new Dictionary<string, string>() : _git.GetFileStates(GitRepositoryRoot);
        Files.Clear();
        foreach (var entry in entries)
        {
            var gitPath = string.IsNullOrEmpty(GitRepositoryRoot) ? entry.RelativePath : Path.GetRelativePath(GitRepositoryRoot, entry.FullPath);
            states.TryGetValue(gitPath, out var state);
            Files.Add(entry with { GitState = state ?? string.Empty });
        }
        SelectedFile = null;
    }

    private void Back()
    {
        if (_back.Count == 0) return;
        _forward.Push(CurrentPath);
        var target = _back.Pop();
        if (target.Equals(ComputerPath, StringComparison.OrdinalIgnoreCase)) NavigateComputer(false);
        else Navigate(target, false);
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
    }

    private void Forward()
    {
        if (_forward.Count == 0) return;
        _back.Push(CurrentPath);
        var target = _forward.Pop();
        if (target.Equals(ComputerPath, StringComparison.OrdinalIgnoreCase)) NavigateComputer(false);
        else Navigate(target, false);
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
    }

    private bool CanGoUp() => IsLocal
        ? !IsComputer
        : !string.Equals(CurrentPath, RootPath, StringComparison.OrdinalIgnoreCase);

    private void Up()
    {
        if (IsComputer) return;
        var parent = Directory.GetParent(CurrentPath)?.FullName;
        if (parent is null)
        {
            if (IsLocal) NavigateComputer();
            return;
        }
        if (!IsLocal && parent.Length < RootPath.Length) return;
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
        if (IsComputer) { LoadComputer(); return; }
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
