using System.IO.Compression;
using System.Text;
using KBEA.Core.Knowledge;

// 端到端验证：构造真实文件 → 跑 Ingest → 跑检索 → 校验分面/版本簇/排序
var root = Path.Combine(Path.GetTempPath(), "kbea-k1-e2e");
if (Directory.Exists(root)) Directory.Delete(root, true);
Directory.CreateDirectory(root);

var docs = Path.Combine(root, "docs");
Directory.CreateDirectory(docs);
Directory.CreateDirectory(Path.Combine(docs, "2026"));

// 造 3 个 pptx：30页(8月, 简化版名)、45页(7月)、60页(5月)
MakePptx(Path.Combine(docs, "2026", "项目方案汇报.pptx"), 30, "方案要点" + new string('甲', 200));
MakePptx(Path.Combine(docs, "2026", "项目方案汇报v2.pptx"), 45, "方案要点" + new string('乙', 300));
MakePptx(Path.Combine(docs, "项目方案汇报.pptx"), 60, "方案要点" + new string('丙', 400));

// 造 2 个 docx：一个 12 页(8月, 偏技术)，一个 8 页
MakeDocx(Path.Combine(docs, "2026", "接口技术方案.docx"), 12);
MakeDocx(Path.Combine(docs, "2026", "接口技术方案简化版.docx"), 6);

// 造 1 个 pdf：20 页，8 月
MakePdf(Path.Combine(docs, "2026", "合同扫描件.pdf"), 20);

// 造 1 个 md
File.WriteAllText(Path.Combine(docs, "2026", "会议纪要.md"), "# 会议纪要\n讨论了知识库检索方案与标签体系。", Encoding.UTF8);

// 设置不同修改时间以验证时间分面
var now = DateTime.Now;
SetTime(Path.Combine(docs, "2026", "项目方案汇报.pptx"), new DateTime(now.Year, 8, 10));
SetTime(Path.Combine(docs, "2026", "项目方案汇报v2.pptx"), new DateTime(now.Year, 7, 15));
SetTime(Path.Combine(docs, "项目方案汇报.pptx"), new DateTime(now.Year, 5, 20));
SetTime(Path.Combine(docs, "2026", "接口技术方案.docx"), new DateTime(now.Year, 8, 3));
SetTime(Path.Combine(docs, "2026", "接口技术方案简化版.docx"), new DateTime(now.Year, 8, 20));
SetTime(Path.Combine(docs, "2026", "合同扫描件.pdf"), new DateTime(now.Year, 8, 25));
SetTime(Path.Combine(docs, "2026", "会议纪要.md"), new DateTime(now.Year, 6, 8));

Console.WriteLine($"== 测试空间: {root}");

var ingest = new KnowledgeIngestService();
var report = ingest.Ingest(root, new Progress<string>(p => Console.WriteLine("  " + p)));
Console.WriteLine($"== Ingest: {report.Summary}  耗时 {report.Elapsed.TotalMilliseconds:F0}ms");

// 校验 catalog 内容
var store = new CatalogStore(Path.Combine(root, ".7z"));
var entries = store.LoadAll();
Console.WriteLine($"== catalog 条目数: {entries.Count}");
foreach (var e in entries.OrderBy(e => e.PrimaryPath))
{
    var pages = e.Facts.TryGetValue("page_count", out var p) ? p : 0;
    var role = e.Tags.FirstOrDefault(t => t.StartsWith("version/")) ?? "-";
    Console.WriteLine($"   {e.PrimaryPath,-45} fmt={e.Format,-8} pages={pages,-5} {role,-18} cluster={e.ClusterId ?? "-"}");
}

// 校验 describe 生成
var describeDir = Path.Combine(root, ".7z", "describe");
var describeCount = Directory.Exists(describeDir) ? Directory.GetFiles(describeDir, "*.md").Length : 0;
Console.WriteLine($"== describe 文件数: {describeCount}");
if (describeCount > 0)
{
    var sample = Directory.GetFiles(describeDir, "*.md")[0];
    Console.WriteLine("--- describe 样本 ---");
    Console.WriteLine(File.ReadAllText(sample).Split('\n').Take(22).Aggregate("", (a, b) => a + b + "\n"));
}

// 检索场景 1：今年8月左右的、30页左右的、简化版 ppt
var search = new KnowledgeSearchService(Path.Combine(root, ".7z"));
foreach (var q in new[]
{
    "今年8月左右的30页左右的ppt",
    "8月的ppt",
    "偏技术性的方案参考",
    "简化版的技术方案",
    "8月 30页 ppt"
})
{
    var outcome = search.Search(q);
    Console.WriteLine($"\n>>> 查询: {q}");
    Console.WriteLine($"    理解: {string.Join(" / ", outcome.Query.Notes)}");
    Console.WriteLine($"    剩余文本: '{outcome.Query.RemainingText}'  候选={outcome.TotalCandidates} 命中={outcome.Hits.Count} fts={outcome.UsedFts}");
    foreach (var hit in outcome.Hits.Take(5))
    {
        Console.WriteLine($"      [{hit.Score:F2}] {Path.GetFileName(hit.RelativePath),-28} {hit.Summary}");
        if (hit.Siblings.Count > 0)
            Console.WriteLine($"            同簇其他版本: {string.Join(", ", hit.Siblings.Select(s => Path.GetFileName(s.RelativePath) + "(" + s.VersionRole + ")"))}");
    }
}

// 校验版本簇
var clustered = entries.Where(e => !string.IsNullOrEmpty(e.ClusterId)).ToList();
Console.WriteLine($"\n== 版本簇成员数: {clustered.Count}, 簇数: {clustered.Select(e => e.ClusterId).Distinct().Count()}");

// 校验 SQLite 投影
var dbPath = Path.Combine(root, ".7z", "meta.db");
Console.WriteLine($"== meta.db 存在: {File.Exists(dbPath)}, 大小 {(File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0)}");

// ============ 断言校验 ============
Console.WriteLine("\n== 断言校验 ==");
var failures = new List<string>();
void Check(bool condition, string message)
{
    Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}  {message}");
    if (!condition) failures.Add(message);
}

var e2e = search.Search("今年8月左右的30页左右的ppt");
Check(e2e.TotalCandidates == 1, $"分面过滤后候选应为 1，实际 {e2e.TotalCandidates}");
Check(e2e.Hits.Count == 1, $"命中应为 1，实际 {e2e.Hits.Count}");
Check(e2e.Hits.Count > 0 && e2e.Hits[0].Facts.GetValueOrDefault("page_count") == 30, "命中项页数应为 30");
Check(e2e.Hits.Count > 0 && e2e.Hits[0].VersionRole == "simplified", $"命中项版本角色应为 simplified，实际 {e2e.Hits.FirstOrDefault()?.VersionRole}");
Check(e2e.Hits.Count > 0 && e2e.Hits[0].Siblings.Count == 2, $"应折叠展示 2 个同簇版本，实际 {e2e.Hits.FirstOrDefault()?.Siblings.Count}");

var monthOnly = search.Search("8月的ppt");
Check(monthOnly.TotalCandidates == 1, $"仅时间+类型过滤候选应为 1，实际 {monthOnly.TotalCandidates}");

var techQuery = search.Search("简化版的技术方案");
Check(techQuery.UsedFts, "应走 FTS5 检索");
Check(techQuery.Hits.Count > 0 && techQuery.Hits[0].RelativePath.Contains("简化版"), "简化版应排第一");

// 短词（2 字中文）应能被 LIKE 兜底命中
var shortQuery = search.Search("纪要");
Check(shortQuery.Hits.Count > 0 && shortQuery.Hits[0].RelativePath.Contains("纪要"), "2 字中文短词应能被 LIKE 兜底命中");

// 版本簇数量
var clusterCount = entries.Select(e => e.ClusterId).Where(c => !string.IsNullOrEmpty(c)).Distinct().Count();
Check(clusterCount == 2, $"应形成 2 个版本簇，实际 {clusterCount}");

// describe 文件齐备
var describeFiles = Directory.Exists(Path.Combine(root, ".7z", "describe"))
    ? Directory.GetFiles(Path.Combine(root, ".7z", "describe"), "*.md").Length : 0;
Check(describeFiles == entries.Count, $"describe 文件数应等于 catalog 条目数，实际 {describeFiles}");

// catalog JSON 含新字段
var sampleJson = File.ReadAllText(Directory.GetFiles(Path.Combine(root, ".7z", "catalog"), "*.json").First());
Check(sampleJson.Contains("\"facts\""), "catalog JSON 应含 facts 字段");
Check(sampleJson.Contains("\"paths\""), "catalog JSON 应含 paths 字段");
Check(sampleJson.Contains("\"tags\""), "catalog JSON 应含 tags 字段");

// describe 人工正文保留（reconcile 契约）
var reconcileRoot = Path.Combine(Path.GetTempPath(), "kbea-reconcile");
if (Directory.Exists(reconcileRoot)) Directory.Delete(reconcileRoot, true);
Directory.CreateDirectory(reconcileRoot);
var reconcileStore = new CatalogStore(Path.Combine(reconcileRoot, ".7z"), reconcileRoot);
var manualEntry = new CatalogEntry
{
    Id = "sha256:AAAA1111",
    Paths = ["docs/报告.md"],
    Format = "markdown",
    Size = 100,
    Mtime = DateTimeOffset.Now,
    Tags = ["format/markdown"],
    Facts = new Dictionary<string, double> { ["page_count"] = 3 }
};
var skeleton = $"{CatalogStore.AutoStartMarker}\n\n# 标题\n\n## 结构化事实\n\n| 项 | 值 |\n|---|---|\n| page_count | 3 |\n\n{CatalogStore.AutoEndMarker}\n";
reconcileStore.WriteDescribe(manualEntry, skeleton);
// 模拟人工直接在文件里追加正文
var describePath = Path.Combine(reconcileRoot, ".7z", "describe", $"{manualEntry.Hash}.md");
File.AppendAllText(describePath, "\n## 人工批注\n\n这段是人工写的。\n");
// 重新整理：只回写骨架
reconcileStore.WriteDescribe(manualEntry, skeleton);
var merged = reconcileStore.ReadDescribe(manualEntry.Hash)!;
Check(merged.Contains("人工批注"), "重新整理应保留人工正文");
Check(merged.Contains("结构化事实"), "重新整理应保留系统骨架段");
var manualOnly = CatalogStore.ExtractManualBody(merged);
Check(manualOnly.Contains("人工批注") && !manualOnly.Contains("结构化事实"), "ExtractManualBody 应只返回人工正文");
Directory.Delete(reconcileRoot, true);

Console.WriteLine($"\n== 断言结果: {(failures.Count == 0 ? "全部通过" : failures.Count + " 项失败")}");
foreach (var f in failures) Console.WriteLine("   失败: " + f);

Console.WriteLine("\n== E2E 完成 ==");
if (failures.Count > 0) Environment.Exit(1);

// ============ 辅助：构造测试文件 ============
static void MakePptx(string path, int slides, string text)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    WriteEntry(archive, "[Content_Types].xml", """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="xml" ContentType="application/xml"/>
        </Types>
        """);
    WriteEntry(archive, "docProps/app.xml", $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties">
          <Words>1200</Words>
          <TitlesOfParts>演示</TitlesOfParts>
        </Properties>
        """);
    for (var i = 1; i <= slides; i++)
        WriteEntry(archive, $"ppt/slides/slide{i}.xml", $"<p:sld xmlns:p=\"p\"><a:t>{text}</a:t></p:sld>");
}

static void MakeDocx(string path, int pages)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    WriteEntry(archive, "[Content_Types].xml", """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="xml" ContentType="application/xml"/>
        </Types>
        """);
    WriteEntry(archive, "docProps/app.xml", $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties">
          <Pages>{pages}</Pages>
          <Words>3400</Words>
          <Paragraphs>88</Paragraphs>
          <Lines>210</Lines>
        </Properties>
        """);
    WriteEntry(archive, "word/document.xml", "<w:document xmlns:w=\"w\"><w:body/></w:document>");
}

static void MakePdf(string path, int pages)
{
    var builder = new StringBuilder();
    builder.Append("%PDF-1.7\n");
    for (var i = 1; i <= pages; i++)
        builder.Append($"1 0 obj << /Type /Page /Parent 2 0 R >> endobj\n");
    builder.Append($"2 0 obj << /Type /Pages /Count {pages} /Kids [3 0 R] >> endobj\n");
    builder.Append("trailer << /Root 2 0 R >>\n%%EOF");
    File.WriteAllText(path, builder.ToString(), Encoding.Latin1);
}

static void WriteEntry(ZipArchive archive, string name, string content)
{
    var entry = archive.CreateEntry(name);
    using var stream = entry.Open();
    var bytes = Encoding.UTF8.GetBytes(content);
    stream.Write(bytes, 0, bytes.Length);
}

static void SetTime(string path, DateTime time)
{
    File.SetLastWriteTime(path, time);
    File.SetCreationTime(path, time);
}
