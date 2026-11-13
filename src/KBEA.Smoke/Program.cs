// 冒烟验证：启动 KBEA，检查检索视图相关 UI 元素是否可见、是否可交互
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

var exePath = args.Length > 0 ? args[0] : @"C:\Users\qizic\Desktop\Projects\rsiKB\artifacts\KBEA-Light\KBEA.exe";
var configPath = Path.Combine(Path.GetTempPath(), "kbea-smoke.json");

// 预置一个指向测试空间的配置，确保工具栏与检索视图可见
var sampleSpace = Path.Combine(Path.GetTempPath(), "kbea-smoke-space");
Directory.CreateDirectory(sampleSpace);
File.WriteAllText(Path.Combine(sampleSpace, "示例文档.md"), "# 示例文档\n这是一个用于冒烟验证的示例文件。");
var configJson = $$"""
{
  "machineId": "smoke",
  "machineName": "smoke",
  "authorName": "smoke",
  "authorEmail": "smoke@localhost",
  "theme": "Light",
  "spaces": [
    { "id": "11111111-1111-1111-1111-111111111111", "name": "冒烟空间", "path": {{System.Text.Json.JsonSerializer.Serialize(sampleSpace)}} }
  ],
  "directories": [],
  "organizations": [],
  "organizationShares": []
}
""";
File.WriteAllText(configPath, configJson);

Console.WriteLine($"启动: {exePath}");
var psi = new ProcessStartInfo
{
    FileName = exePath,
    Arguments = $"--config \"{configPath}\"",
    UseShellExecute = false,
    CreateNoWindow = false
};
var process = Process.Start(psi) ?? throw new InvalidOperationException("启动失败");

try
{
    var handle = WaitForMainWindow(process, TimeSpan.FromSeconds(30));
    if (handle == IntPtr.Zero) { Console.WriteLine("FAIL 未找到主窗口"); Environment.Exit(1); }
    var window = AutomationElement.FromHandle(handle);
    if (window is null) { Console.WriteLine("FAIL 无法获取窗口元素"); Environment.Exit(1); }

    Console.WriteLine($"PASS 主窗口已打开: {window.Current.Name}");

    // 点击左侧「冒烟空间」节点，激活空间标签页，工具栏才会出现
    var spaceNode = FindTreeItemByName(window, "冒烟空间");
    if (spaceNode is not null)
    {
        try
        {
            var select = spaceNode.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
            select?.Select();
        }
        catch (Exception ex) { Console.WriteLine("WARN 选择树节点失败: " + ex.Message); }
        Thread.Sleep(1800);
        Console.WriteLine("PASS 已激活空间标签页");
    }
    else Console.WriteLine("WARN 未找到「冒烟空间」树节点");

    // 查找语义检索入口（搜索框）、整理知识索引按钮
    var searchBox = FindSearchBox(window);
    Console.WriteLine(searchBox is not null ? $"PASS 找到搜索框: {searchBox.Current.HelpText}" : "WARN 未找到搜索框");

    var ingestButton = FindButtonByContent(window, "整理知识索引");
    Console.WriteLine(ingestButton is not null ? "PASS 找到「◈ 整理知识索引」按钮" : "FAIL 未找到整理知识索引按钮");

    var metadataButton = FindButtonByContent(window, "元数据面板");
    Console.WriteLine(metadataButton is not null ? "PASS 找到「◧ 元数据面板」按钮" : "FAIL 未找到元数据面板按钮");

    var spaceMetadataButton = FindButtonByToolTip(window, "查看该知识库的元数据构建情况");
    Console.WriteLine(spaceMetadataButton is not null ? "PASS 找到空间树「◈ 元数据」配置图标" : "FAIL 未找到空间树元数据配置图标");

    // 打开元数据面板，验证面板内各库各层进度渲染正常
    if (metadataButton is not null)
    {
        try
        {
            // 面板是模态窗口，Invoke 会同步阻塞，必须放到后台线程
            var invoke = metadataButton.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
            if (invoke is not null) ThreadPool.QueueUserWorkItem(_ => { try { invoke.Invoke(); } catch { } });

            AutomationElement? panel = null;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && panel is null)
            {
                Thread.Sleep(400);
                panel = FindWindowByName(window, "知识库元数据");
            }
            Console.WriteLine(panel is not null ? "PASS 元数据面板已打开" : "FAIL 未找到元数据面板窗口");
            if (panel is not null)
            {
                var panelTexts = panel.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                var found = new List<string>();
                foreach (AutomationElement t in panelTexts)
                {
                    var n = t.Current.Name;
                    if (!string.IsNullOrEmpty(n) && (n.Contains("基础层") || n.Contains("类型层") || n.Contains("专题层")))
                        found.Add(n);
                }
                Console.WriteLine(found.Count > 0 ? $"PASS 面板展示层级进度项 {found.Count} 个" : "FAIL 面板未展示层级进度");
                var errors = new List<string>();
                foreach (AutomationElement t in panelTexts)
                {
                    var n = t.Current.Name;
                    if (!string.IsNullOrEmpty(n) && (n.Contains("异常") || n.Contains("XamlParse") || n.Contains("无法解析")))
                        errors.Add(n);
                }
                Console.WriteLine(errors.Count == 0 ? "PASS 面板无 XAML 解析错误" : "FAIL 面板检测到错误: " + string.Join(" | ", errors));
                foreach (AutomationElement t in panelTexts)
                {
                    var n = t.Current.Name;
                    if (!string.IsNullOrEmpty(n) && (n.Contains("基础层") || n.Contains("类型层")))
                    {
                        Console.WriteLine("   · " + n);
                    }
                }
                CloseWindow(panel);
                Thread.Sleep(800);
            }
        }
        catch (Exception ex) { Console.WriteLine("WARN 打开元数据面板失败: " + ex.Message); }
    }

    // 列出窗口内所有按钮，确认工具栏按钮
    var buttons = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
    Console.WriteLine($"-- 窗口内按钮 {buttons.Count} 个:");
    var names = new List<string>();
    foreach (AutomationElement b in buttons)
    {
        var n = b.Current.Name;
        if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
    }
    foreach (var n in names.Distinct().Take(40)) Console.WriteLine("   · " + n);

    // 检查是否有渲染异常
    var texts = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
    var errorText = new List<string>();
    foreach (AutomationElement t in texts)
    {
        var n = t.Current.Name;
        if (!string.IsNullOrEmpty(n) && (n.Contains("异常") || n.Contains("XamlParse") || n.Contains("无法解析")))
            errorText.Add(n);
    }
    Console.WriteLine(errorText.Count == 0 ? "PASS 未检测到 XAML 解析错误文本" : "FAIL 检测到错误文本: " + string.Join(" | ", errorText));
}
finally
{
    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
}

static IntPtr WaitForMainWindow(Process process, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        process.Refresh();
        if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
        Thread.Sleep(300);
    }
    return IntPtr.Zero;
}

static AutomationElement? FindTreeItemByName(AutomationElement root, string name)
{
    var condition = new AndCondition(
        new PropertyCondition(AutomationElement.NameProperty, name),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));
    return root.FindFirst(TreeScope.Descendants, condition);
}

static AutomationElement? FindSearchBox(AutomationElement root)
{
    var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
    foreach (AutomationElement e in edits)
    {
        var help = e.Current.HelpText ?? "";
        if (help.Contains("语义检索")) return e;
    }
    return edits.Count > 0 ? edits[0] : null;
}

static AutomationElement? FindButtonByContent(AutomationElement root, string content)
{
    var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
    foreach (AutomationElement b in buttons)
    {
        var n = b.Current.Name;
        if (n.Contains(content, StringComparison.OrdinalIgnoreCase)) return b;
    }
    return null;
}

static AutomationElement? FindButtonByToolTip(AutomationElement root, string toolTip)
{
    var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
    foreach (AutomationElement b in buttons)
    {
        var help = b.Current.HelpText ?? "";
        if (help.Contains(toolTip, StringComparison.OrdinalIgnoreCase)) return b;
    }
    return null;
}

static AutomationElement? FindWindowByName(AutomationElement root, string name)
{
    // 模态面板在 UIA 树中是主窗口的后代，需要在 root 下递归查找
    var condition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
        new PropertyCondition(AutomationElement.NameProperty, name));
    var found = root.FindFirst(TreeScope.Descendants, condition);
    if (found is not null) return found;
    var rootCondition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
        new PropertyCondition(AutomationElement.NameProperty, name));
    return AutomationElement.RootElement.FindFirst(TreeScope.Children, rootCondition);
}

static void CloseWindow(AutomationElement window)
{
    try
    {
        var pattern = window.GetCurrentPattern(WindowPattern.Pattern) as WindowPattern;
        pattern?.Close();
    }
    catch { }
}
