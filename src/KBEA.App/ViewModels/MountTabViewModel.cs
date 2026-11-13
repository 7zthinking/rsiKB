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
    private string? _catalogDirectoryName;
    private bool _disposed;

    /// <summary>标签是否已关闭。关闭后不再回填任何后台结果。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>关闭标签：停止回填后台扫描结果。</summary>
    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>编目模式下的目录名：非 null 时 Title 返回它，替代空间/路径名。</summary>
    public string? CatalogDirectoryName
    {
        get => _catalogDirectoryName;
        set { if (SetProperty(ref _catalogDirectoryName, value)) OnPropertyChanged(nameof(Title)); }
    }

    // 静态剪贴板：跨标签/窗口的复制/剪切操作共享
    private static List<string> _clipboardPaths = [];
    private static bool _clipboardIsCut;
    private static string _clipboardSourceDirectory = string.Empty;
    public static bool HasClipboard => _clipboardPaths.Count > 0;
    public static bool CanPasteTo(string targetDir) => HasClipboard && Directory.Exists(targetDir);

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
        CopyCommand = new RelayCommand(CopySelected, () => SelectedFile is not null);
        CutCommand = new RelayCommand(CutSelected, () => SelectedFile is not null);
        PasteCommand = new RelayCommand(Paste, () => CanPasteTo(CurrentPath));
        DeleteCommand = new RelayCommand(DeleteSelected, () => SelectedFile is not null);
        PermanentDeleteCommand = new RelayCommand(PermanentDeleteSelected, () => SelectedFile is not null);
        RenameCommand = new RelayCommand(RenameSelected, () => SelectedFile is not null && !SelectedFile.IsDirectory);
        SelectAllCommand = new RelayCommand(() => { /* DataGrid 绑定，code-behind 处理 */ });
        OpenWithCommand = new RelayCommand(OpenSelectedWithShell, () => SelectedFile is not null);
        ShowInExplorerCommand = new RelayCommand(ShowSelectedInExplorer, () => SelectedFile is not null);
        AddToWorkspaceCommand = new RelayCommand(AddSelectedToWorkspace, () => SelectedFile is not null && IsLocal);
        if (!IsComputer) Directories.Add(new FolderNodeViewModel(this, RootPath, IsLocal ? RootPath : Space.Name, _files));
        Refresh();
    }

    public SpaceDefinition Space { get; private set; }
    public string RootPath { get; private set; }
    public bool IsLocal { get; private set; }
    private MetadataStore? _metadata;
    /// <summary>占位标签：仅用于在标签栏末尾显示“＋”，不代表真实的浏览标签。</summary>
    public bool IsPlaceholder { get; init; }
    public bool IsComputer => CurrentPath.Equals(ComputerPath, StringComparison.OrdinalIgnoreCase);
    public string Title => CatalogDirectoryName ?? (IsComputer ? ComputerPath : new DirectoryInfo(CurrentPath).Name is { Length: > 0 } name ? name : CurrentPath);
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
    public RelayCommand CopyCommand { get; private set; } = null!;
    public RelayCommand CutCommand { get; private set; } = null!;
    public RelayCommand PasteCommand { get; private set; } = null!;
    public RelayCommand DeleteCommand { get; private set; } = null!;
    public RelayCommand PermanentDeleteCommand { get; private set; } = null!;
    public RelayCommand RenameCommand { get; private set; } = null!;
    public RelayCommand SelectAllCommand { get; private set; } = null!;
    public RelayCommand OpenWithCommand { get; private set; } = null!;
    public RelayCommand ShowInExplorerCommand { get; private set; } = null!;
    public RelayCommand AddToWorkspaceCommand { get; private set; } = null!;
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
                CopyCommand.NotifyCanExecuteChanged();
                CutCommand.NotifyCanExecuteChanged();
                DeleteCommand.NotifyCanExecuteChanged();
                PermanentDeleteCommand.NotifyCanExecuteChanged();
                RenameCommand.NotifyCanExecuteChanged();
                OpenWithCommand.NotifyCanExecuteChanged();
                ShowInExplorerCommand.NotifyCanExecuteChanged();
                AddToWorkspaceCommand.NotifyCanExecuteChanged();
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

    /// <summary>让当前标签切换到另一个"空间"（非本地、带 MetadataStore）。</summary>
    public void SwitchSpace(SpaceDefinition space)
    {
        CatalogDirectoryName = null;
        if (Space?.Id == space.Id && RootPath.Equals(space.Path, StringComparison.OrdinalIgnoreCase) && !IsLocal)
        {
            // 已经在这个空间了，直接导航到根
            Navigate(space.Path);
            return;
        }
        Space = space;
        IsLocal = false;
        RootPath = space.Path;
        _metadata = new MetadataStore(space.Path);
        Directories.Clear();
        Directories.Add(new FolderNodeViewModel(this, RootPath, space.Name, _files));
        _back.Clear();
        _forward.Clear();
        Navigate(space.Path, recordHistory: false);

        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(IsComputer));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(SyncText));
        OnPropertyChanged(nameof(CanUseGit));
        OnPropertyChanged(nameof(Title));
        _ = RefreshGitStatusAsync();
    }

    /// <summary>让当前标签切换到另一个本地文件夹。</summary>
    public void SwitchLocal(string fullPath)
    {
        CatalogDirectoryName = null;
        var rootPath = Path.GetPathRoot(fullPath) ?? fullPath;
        if (RootPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase) && IsLocal)
        {
            Navigate(fullPath);
            return;
        }
        Space = new SpaceDefinition { Name = "本地目录", Path = rootPath };
        IsLocal = true;
        RootPath = rootPath;
        _metadata = null;
        Directories.Clear();
        Directories.Add(new FolderNodeViewModel(this, RootPath, RootPath, _files));
        _back.Clear();
        _forward.Clear();
        Navigate(fullPath, recordHistory: false);

        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(IsComputer));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(SyncText));
        OnPropertyChanged(nameof(CanUseGit));
        OnPropertyChanged(nameof(Title));
        _ = RefreshGitStatusAsync();
    }

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
        LoadFiles(_files.GetEntries(path, RootPath));
        _ = RefreshGitStatusAsync();
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
        if (IsComputer) { LoadComputer(); return; }
        if (!Directory.Exists(CurrentPath)) return;

        // 文件列表立即显示；Git 状态涉及索引扫描，大仓库可能耗时较久，放到后台补齐
        LoadFiles(_files.GetEntries(CurrentPath, RootPath));
        _ = RefreshGitStatusAsync();
    }

    /// <summary>后台刷新 Git 状态与文件状态标记。</summary>
    private async Task RefreshGitStatusAsync()
    {
        try
        {
            var path = CurrentPath;
            var root = await Task.Run(() => _git.DiscoverRepositoryRoot(path));
            if (root is null)
            {
                GitRepositoryRoot = string.Empty;
                GitStatus = new GitStatusInfo(false, 0, string.Empty, string.Empty, 0);
                return;
            }

            // 一次扫描同时取回仓库概览与文件状态（原先分成两次 RetrieveStatus，大仓库要扫两遍）
            var snapshot = await Task.Run(() => _git.GetSnapshot(root));

            // 期间可能已经切换目录，或标签已被关闭 → 丢弃过期结果
            if (!string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase)) return;
            if (_disposed) return;

            GitRepositoryRoot = root;
            GitStatus = snapshot.Status;
            ApplyFileStates(snapshot.FileStates);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            GitStatus = new GitStatusInfo(false, 0, string.Empty, string.Empty, 0);
        }
    }

    /// <summary>把后台取到的 Git 文件状态合并进已显示的文件列表。</summary>
    private void ApplyFileStates(IReadOnlyDictionary<string, string> states)
    {
        if (states.Count == 0 || Files.Count == 0) return;
        for (var i = 0; i < Files.Count; i++)
        {
            var entry = Files[i];
            var gitPath = string.IsNullOrEmpty(GitRepositoryRoot)
                ? entry.RelativePath
                : Path.GetRelativePath(GitRepositoryRoot, entry.FullPath);
            if (states.TryGetValue(gitPath, out var state)) Files[i] = entry with { GitState = state };
        }
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
        Files.Clear();
        foreach (var entry in entries) Files.Add(entry);
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

    // === 文件操作 ===

    private void CopySelected()
    {
        if (SelectedFile is null) return;
        _clipboardPaths = new List<string> { SelectedFile.FullPath };
        _clipboardIsCut = false;
        _clipboardSourceDirectory = string.Empty;
        PasteCommand.NotifyCanExecuteChanged();
    }

    private void CutSelected()
    {
        if (SelectedFile is null) return;
        _clipboardPaths = new List<string> { SelectedFile.FullPath };
        _clipboardIsCut = true;
        _clipboardSourceDirectory = CurrentPath;
        PasteCommand.NotifyCanExecuteChanged();
    }

    private void Paste()
    {
        if (_clipboardPaths.Count == 0 || !Directory.Exists(CurrentPath)) return;
        try
        {
            foreach (var source in _clipboardPaths)
            {
                if (!File.Exists(source) && !Directory.Exists(source)) continue;
                var target = Path.Combine(CurrentPath, Path.GetFileName(source));
                if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;

                if (_clipboardIsCut)
                {
                    if (File.Exists(source)) File.Move(source, target);
                    else if (Directory.Exists(source)) Directory.Move(source, target);
                }
                else
                {
                    if (File.Exists(source)) File.Copy(source, target, overwrite: true);
                    else if (Directory.Exists(source)) CopyDirectoryRecursive(source, target);
                }
            }
            if (_clipboardIsCut) { _clipboardPaths = []; PasteCommand.NotifyCanExecuteChanged(); }
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show($"粘贴失败：{ex.Message}", "KBEA", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private static void CopyDirectoryRecursive(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(target, name), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            CopyDirectoryRecursive(dir, Path.Combine(target, name));
        }
    }

    private void DeleteSelected()
    {
        DeleteSelectedCore(permanent: false);
    }

    private void PermanentDeleteSelected()
    {
        DeleteSelectedCore(permanent: true);
    }

    private void DeleteSelectedCore(bool permanent)
    {
        if (SelectedFile is null) return;
        var label = permanent ? "永久删除" : "移到回收站";
        var confirmMsg = permanent
            ? $"确定要永久删除“{SelectedFile.Name}”吗？此操作无法撤销。"
            : $"确定要把“{SelectedFile.Name}”移到回收站吗？";
        var result = System.Windows.MessageBox.Show(
            confirmMsg, $"KBEA - {label}",
            System.Windows.MessageBoxButton.YesNo,
            permanent ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes) return;
        try
        {
            var path = SelectedFile.FullPath;
            var opts = permanent
                ? Microsoft.VisualBasic.FileIO.RecycleOption.DeletePermanently
                : Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
            if (SelectedFile.IsDirectory)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.AllDialogs, opts);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.AllDialogs, opts);
            Refresh();
        }
        catch (OperationCanceledException)
        {
            // 用户在系统对话框点了"取消"，静默
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show($"删除失败：{ex.Message}", "KBEA", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void RenameSelected()
    {
        if (SelectedFile is null) return;
        var name = SelectedFile.Name;
        var ext = Path.GetExtension(name);
        var dialog = new KBEA.App.Views.InputDialog("重命名", "新文件名：", name);
        if (dialog.ShowDialog() != true) return;
        var newName = (dialog.Value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == name) return;
        if (string.IsNullOrEmpty(Path.GetExtension(newName)) && !string.IsNullOrEmpty(ext)) newName += ext;
        var target = Path.Combine(CurrentPath, newName);
        try
        {
            if (SelectedFile.IsDirectory) Directory.Move(SelectedFile.FullPath, target);
            else File.Move(SelectedFile.FullPath, target);
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show($"重命名失败：{ex.Message}", "KBEA", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OpenSelectedWithShell()
    {
        if (SelectedFile is null) return;
        var path = SelectedFile.IsDirectory ? SelectedFile.FullPath : SelectedFile.FullPath;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void ShowSelectedInExplorer()
    {
        if (SelectedFile is null) return;
        try
        {
            var args = SelectedFile.IsDirectory ? SelectedFile.FullPath : $"/select,\"{SelectedFile.FullPath}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch { }
    }

    private void AddSelectedToWorkspace()
    {
        if (SelectedFile is null) return;
        var message = SelectedFile.IsDirectory
            ? $"要将文件夹“{SelectedFile.Name}”添加为新空间吗？"
            : $"要将文件所在文件夹“{Path.GetDirectoryName(SelectedFile.FullPath)}”添加为新空间吗？";
        var result = System.Windows.MessageBox.Show(message, "KBEA", System.Windows.MessageBoxButton.YesNo);
        if (result != System.Windows.MessageBoxResult.Yes) return;
        var folder = SelectedFile.IsDirectory ? SelectedFile.FullPath : Path.GetDirectoryName(SelectedFile.FullPath)!;
        System.Windows.MessageBox.Show($"已请求添加文件夹：{folder}\n\n请在主界面点击“＋ 新建空间”完成添加。", "KBEA");
    }

    /// <summary>外部文件拖入：将文件复制到当前目录。</summary>
    public void HandleFilesDropped(IReadOnlyList<string> paths)
    {
        try
        {
            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                var target = Path.Combine(CurrentPath, name);
                if (File.Exists(path)) File.Copy(path, target, overwrite: true);
                else if (Directory.Exists(path)) CopyDirectoryRecursive(path, target);
            }
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show($"拖入失败：{ex.Message}", "KBEA", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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
