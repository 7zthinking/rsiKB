// 验证 describe 人工正文保留（reconcile 契约）
using KBEA.Core.Knowledge;

var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
Console.SetOut(stdout);

var root = Path.Combine(Path.GetTempPath(), "kbea-reconcile");
if (Directory.Exists(root)) Directory.Delete(root, true);
Directory.CreateDirectory(root);

var store = new CatalogStore(Path.Combine(root, ".7z"), root);
var entry = new CatalogEntry
{
    Id = "sha256:AAAA1111",
    Paths = ["docs/报告.md"],
    Format = "markdown",
    Size = 100,
    Mtime = DateTimeOffset.Now,
    Tags = ["format/markdown", "type/report"],
    Facts = new Dictionary<string, double> { ["page_count"] = 3 }
};

Console.WriteLine("[1] 首次生成 describe");
store.WriteDescribe(entry, $"{CatalogStore.AutoStartMarker}\n\n# 标题\n\n## 结构化事实\n\n| 项 | 值 |\n|---|---|\n| page_count | 3 |\n\n{CatalogStore.AutoEndMarker}\n");
Console.WriteLine(store.ReadDescribe(entry.Hash));

Console.WriteLine("[2] 人工追加正文后重新生成（应保留人工内容）");
var manual = store.ReadDescribe(entry.Hash)!;
store.WriteDescribe(entry, manual + "\n\n## 人工批注\n\n这段是人工写的，重新整理时应保留。\n");
var after = store.ReadDescribe(entry.Hash)!;
Console.WriteLine(after);
Console.WriteLine($"[3] 人工内容保留: {(after.Contains("人工批注") ? "PASS" : "FAIL")}");
Console.WriteLine($"[4] 骨架内容保留: {(after.Contains("结构化事实") ? "PASS" : "FAIL")}");

Console.WriteLine("[5] ExtractManualBody 结果:");
Console.WriteLine("---");
Console.WriteLine(CatalogStore.ExtractManualBody(after));
Console.WriteLine("---");

Directory.Delete(root, true);
Console.WriteLine("[6] 完成");
