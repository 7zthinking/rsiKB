using System.IO.Compression;
using System.Text;
using KBEA.Core.Knowledge;
using KBEA.Core.Plugins;

// P3：Ingest 引擎随 kbea-plugin-wiki 分发，测试进程内直接注册插件实现
// （等价于真实 Worker 启动时 PluginLoader 加载 metadata 能力插件的 Init 效果）
WikiOperationsProvider.Register(new KnowledgeIngestService());

// 供进程隔离验证使用：enqueue <空间路径> <任务库路径>
if (args.Length >= 2 && args[0] == "enqueue")
{
    var queue = new MetadataJobStore(args[2]);
    queue.Initialize();
    queue.Enqueue(new MetadataJob
    {
        SpacePath = args[1],
        SpaceName = "进程测试库",
        Kind = MetadataJobKind.Basic,
        Trigger = "test"
    });
    Console.WriteLine("已投递任务");
    return;
}

// 端到端验证：投递分层任务 → Worker 领取执行 → 进度可查 → 计划文件回写
var failures = new List<string>();
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "，" + detail : "")}");
    if (!ok) failures.Add(name);
}

var root = Path.Combine(Path.GetTempPath(), "kbea-worker-e2e");
if (Directory.Exists(root)) Directory.Delete(root, true);
Directory.CreateDirectory(root);

var docs = Path.Combine(root, "docs");
Directory.CreateDirectory(docs);
MakePptx(Path.Combine(docs, "项目方案汇报.pptx"), 30, "封面标题");
MakeDocx(Path.Combine(docs, "接口技术方案.docx"), 12);
File.WriteAllText(Path.Combine(docs, "会议纪要.md"), "# 会议纪要\n讨论了检索方案。", Encoding.UTF8);

var dbPath = Path.Combine(root, "jobs.db");
var jobs = new MetadataJobStore(dbPath);
jobs.Initialize();

Console.WriteLine("== 1. 投递分层任务");
jobs.Enqueue(new MetadataJob { SpacePath = root, SpaceName = "测试库", Kind = MetadataJobKind.Basic, Trigger = "test" });
jobs.Enqueue(new MetadataJob { SpacePath = root, SpaceName = "测试库", Kind = MetadataJobKind.Type, Trigger = "test" });
Check("队列应有 2 个任务", jobs.List(root).Count == 2, $"实际 {jobs.List(root).Count}");
Check("应存在排队中任务", jobs.HasActiveJob(root));

Console.WriteLine("== 2. Worker 领取并执行基础层");
var scheduler = new MetadataScheduler(jobs, () => [new SpaceDefinitionLite("测试库", root)]);
var basic = jobs.ClaimNext();
Check("首个任务应为基础层", basic?.Kind == MetadataJobKind.Basic, basic?.Kind.ToString() ?? "null");
Check("领取后状态应为执行中", basic?.State == MetadataJobState.Running);
var okBasic = scheduler.RunJob(basic!);
Check("基础层执行成功", okBasic);

var basicDone = jobs.List(root).First(j => j.Kind == MetadataJobKind.Basic);
Check("基础层状态应为已完成", basicDone.State == MetadataJobState.Succeeded, basicDone.State.ToString());
Check("基础层应有结果摘要", basicDone.Message.Contains("扫描"), basicDone.Message);

Console.WriteLine("== 3. Worker 领取并执行类型层");
var type = jobs.ClaimNext();
Check("第二个任务应为类型层", type?.Kind == MetadataJobKind.Type, type?.Kind.ToString() ?? "null");
var okType = scheduler.RunJob(type!);
Check("类型层执行成功", okType);

var typeDone = jobs.List(root).First(j => j.Kind == MetadataJobKind.Type);
Check("类型层状态应为已完成", typeDone.State == MetadataJobState.Succeeded, typeDone.State.ToString());

Console.WriteLine("== 4. 校验 catalog 与 describe");
var store = new CatalogStore(Path.Combine(root, ".7z"), root);
var entries = store.LoadAll();
Check("catalog 应有 3 条记录", entries.Count == 3, $"实际 {entries.Count}");

var ppt = entries.FirstOrDefault(e => e.Format == "pptx");
Check("pptx 应被识别", ppt is not null);
Check("pptx 页数应为 30", ppt is not null && ppt.Facts.TryGetValue("page_count", out var pc) && Math.Abs(pc - 30) < 0.5);
Check("pptx 应有类型层产物", ppt is not null && ppt.Properties.ContainsKey("type_title"), ppt?.Properties.GetValueOrDefault("type_title") ?? "无");
Check("pptx 应有类型层标签", ppt is not null && ppt.Tags.Any(t => t.StartsWith("type/")));

var describeDir = Path.Combine(root, ".7z", "describe");
var describeFiles = Directory.Exists(describeDir) ? Directory.GetFiles(describeDir, "*.md") : [];
Check("describe 应有 3 个文件", describeFiles.Length == 3, $"实际 {describeFiles.Length}");

var pptDescribe = describeFiles.Select(File.ReadAllText).FirstOrDefault(t => t.Contains("项目方案汇报"));
Check("describe 应含类型要点段", pptDescribe is not null && pptDescribe.Contains("## 类型要点"));
Check("describe 应含元数据生成记录", pptDescribe is not null && pptDescribe.Contains("## 元数据生成记录"));

Console.WriteLine("== 5. 校验计划文件回写");
var plan = new MetadataPlanStore(root).Load();
var basicPlan = plan.Find(MetadataLevel.Basic);
Check("基础层计划应记录上次执行时间", basicPlan?.LastRunAt is not null);
Check("基础层计划应记录结果", !string.IsNullOrWhiteSpace(basicPlan?.LastResult));
Check("类型层计划应记录上次执行时间", plan.Find(MetadataLevel.Type)?.LastRunAt is not null);

Console.WriteLine("== 6. 校验定时计划触发");
var planStore = new MetadataPlanStore(root);
var schedulePlan = planStore.Load();
schedulePlan.GetOrAdd(MetadataLevel.Basic).Enabled = true;
schedulePlan.Schedules.Add(new MetadataSchedule { Level = MetadataLevel.Basic, IntervalHours = 24, Enabled = true });
planStore.Save(schedulePlan);

var triggered = scheduler.TriggerDueSchedules();
Check("到期的计划应触发 1 个任务", triggered == 1, $"实际 {triggered}");
Check("触发后应有新的排队任务", jobs.List(root).Count == 3, $"实际 {jobs.List(root).Count}");

var reloaded = planStore.Load();
Check("计划应记录下次执行时间", reloaded.Schedules[0].NextRunAt is not null);

Console.WriteLine("== 7. 校验中断任务恢复");
var stale = jobs.Enqueue(new MetadataJob { SpacePath = root, SpaceName = "测试库", Kind = MetadataJobKind.Basic, Trigger = "test" });
jobs.ClaimNext(); // 领取后不执行，模拟崩溃
var recovered = scheduler.RecoverStaleJobs();
Check("应恢复 1 个中断任务", recovered == 1, $"实际 {recovered}");
var recoveredJob = jobs.List(root).First(j => j.Id == stale.Id);
Check("恢复后应回到排队状态", recoveredJob.State == MetadataJobState.Queued, recoveredJob.State.ToString());

Console.WriteLine();
Console.WriteLine($"== 断言结果: {(failures.Count == 0 ? "全部通过" : $"失败 {failures.Count} 项: {string.Join("、", failures)}")}");
if (failures.Count > 0) Environment.Exit(1);
Console.WriteLine("== Worker E2E 完成 ==");

static void MakePptx(string path, int slides, string title)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    var presentation = archive.CreateEntry("ppt/presentation.xml");
    using (var writer = new StreamWriter(presentation.Open(), Encoding.UTF8))
        writer.Write("<?xml version=\"1.0\"?><p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"/>");
    var app = archive.CreateEntry("docProps/app.xml");
    using (var writer = new StreamWriter(app.Open(), Encoding.UTF8))
        writer.Write($"<?xml version=\"1.0\"?><Properties><Slides>{slides}</Slides></Properties>");
    for (var i = 1; i <= slides; i++)
    {
        var slide = archive.CreateEntry($"ppt/slides/slide{i}.xml");
        using var writer = new StreamWriter(slide.Open(), Encoding.UTF8);
        writer.Write($"<?xml version=\"1.0\"?><p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:t>{title} {i}</a:t></p:sld>");
    }
}

static void MakeDocx(string path, int pages)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    var app = archive.CreateEntry("docProps/app.xml");
    using (var writer = new StreamWriter(app.Open(), Encoding.UTF8))
        writer.Write($"<?xml version=\"1.0\"?><Properties><Pages>{pages}</Pages><Paragraphs>10</Paragraphs></Properties>");
    var document = archive.CreateEntry("word/document.xml");
    using (var writer = new StreamWriter(document.Open(), Encoding.UTF8))
        writer.Write("<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p/><w:p/></w:body></w:document>");
}
