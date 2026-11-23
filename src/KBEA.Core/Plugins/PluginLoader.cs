using System.Reflection;
using System.Runtime.Loader;

namespace KBEA.Core.Plugins;

/// <summary>
/// 插件加载器：把 manifest → 程序集 → IPluginEntryPoint 实例串起来。
/// 每个插件独立可卸载 AssemblyLoadContext；单个插件失败只记录原因，
/// 绝不让单个插件失败中断整体加载（沿用 PluginCatalog 的容错策略）。
/// </summary>
public sealed class PluginLoader
{
    /// <summary>单个插件的加载结果：被跳过时 SkipReason 非空，失败时 Error 非空。</summary>
    public sealed record LoadedPlugin(
        PluginManifest Manifest,
        IPluginEntryPoint? Instance,
        AssemblyLoadContext? Context,
        string? SkipReason,
        Exception? Error);

    /// <summary>加载插件清单；宿主上下文在 Init 时注入。</summary>
    public IReadOnlyList<LoadedPlugin> Load(IReadOnlyList<(PluginManifest Manifest, string Directory)> plugins, IPluginHostContext context)
    {
        var results = new List<LoadedPlugin>();
        foreach (var (manifest, directory) in plugins)
        {
            try { results.Add(LoadOne(manifest, directory, context)); }
            catch (Exception ex)
            {
                // 兜底：加载器自身的意外异常也按单插件失败处理，绝不中断整体
                results.Add(new LoadedPlugin(manifest, null, null, null, ex));
            }
        }
        return results;
    }

    private static LoadedPlugin LoadOne(PluginManifest manifest, string directory, IPluginHostContext context)
    {
        if (!manifest.Enabled)
            return new LoadedPlugin(manifest, null, null, "已在 plugin.json 中禁用", null);

        if (!IsHostVersionCompatible(manifest.MinHostVersion, context.HostVersion))
            return new LoadedPlugin(manifest, null, null,
                $"要求宿主版本 ≥ {manifest.MinHostVersion}，当前 {context.HostVersion}", null);

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            return new LoadedPlugin(manifest, null, null, "plugin.json 未声明 entryAssembly", null);

        var assemblyPath = Path.Combine(directory, manifest.EntryAssembly);
        if (!File.Exists(assemblyPath))
            return new LoadedPlugin(manifest, null, null, $"入口程序集不存在：{manifest.EntryAssembly}", null);

        AssemblyLoadContext? loadContext = null;
        try
        {
            // 独立 ALC：自带插件目录解析器，插件自带依赖（如 git 插件的 LibGit2Sharp.dll）从插件目录加载
            loadContext = new PluginLoadContext(directory, manifest.Id);
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var entryType = FindEntryType(assembly);
            if (entryType is null)
                return new LoadedPlugin(manifest, null, loadContext, "未找到 IPluginEntryPoint 实现", null);

            var instance = (IPluginEntryPoint)Activator.CreateInstance(entryType)!;
            instance.Init(context);
            return new LoadedPlugin(manifest, instance, loadContext, null, null);
        }
        catch (Exception ex)
        {
            // 激活失败 → 卸载刚建的 ALC（best-effort），记录错误
            try { loadContext?.Unload(); } catch { }
            return new LoadedPlugin(manifest, null, null, null, ex);
        }
    }

    /// <summary>
    /// 反射查找 IPluginEntryPoint 的非抽象实现；优先 KBEA.Plugin.* 命名空间。
    /// 加载失败的部分类型忽略（ReflectionTypeLoadException 只取可用的类型）。
    /// </summary>
    private static Type? FindEntryType(Assembly assembly)
    {
        Type?[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }

        Type? fallback = null;
        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || type.IsInterface) continue;
            if (!typeof(IPluginEntryPoint).IsAssignableFrom(type)) continue;
            if (type.Namespace?.StartsWith("KBEA.Plugin", StringComparison.Ordinal) == true) return type;
            fallback ??= type;
        }
        return fallback;
    }

    /// <summary>
    /// MinHostVersion 兼容判断（SemVer 前缀比较）：宿主版本须 ≥ 最低要求；
    /// MinHostVersion 为空 = 不限制；无法解析的版本号按不兼容处理（宁可不加载）。
    /// </summary>
    public static bool IsHostVersionCompatible(string minHostVersion, string hostVersion)
    {
        if (string.IsNullOrWhiteSpace(minHostVersion)) return true;
        var minimum = ParseVersionPrefix(minHostVersion);
        var host = ParseVersionPrefix(hostVersion);
        if (minimum is null || host is null) return false;
        if (host.Major != minimum.Major) return host.Major > minimum.Major;
        if (host.Minor != minimum.Minor) return host.Minor > minimum.Minor;
        return host.Build >= minimum.Build;
    }

    /// <summary>解析版本前缀（容忍 "v0.3.0"、"0.3"、"0.3.0-beta" 等写法）；无法解析返回 null。</summary>
    private static Version? ParseVersionPrefix(string text)
    {
        try
        {
            var cleaned = text.Trim().TrimStart('v', 'V');
            var end = 0;
            while (end < cleaned.Length && (char.IsDigit(cleaned[end]) || cleaned[end] == '.')) end++;
            return Version.Parse(cleaned[..end]);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }
}

/// <summary>
/// 插件专用 ALC：托管依赖优先从插件目录解析（如 git 插件自带的 LibGit2Sharp.dll）。
/// KBEA.Core 是宿主契约程序集，强制回落默认上下文——若从插件目录再加载一份，
/// 插件类型与宿主类型在 CLR 层面不同源，接口转换会直接失败。
/// 非收集型（isCollectible=false）：设计文档约定禁用插件以重启生效、不做热卸载；
/// collectible ALC 在 GC 判定无引用时会自动回收，导致仍在使用的插件对象后续
/// 程序集解析抛 "AssemblyLoadContext is unloading"（P2 git 插件实测踩坑）。
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _directory;

    public PluginLoadContext(string directory, string name) : base(name, isCollectible: false)
        => _directory = directory;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name?.Equals("KBEA.Core", StringComparison.Ordinal) == true) return null;
        var candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }
}
