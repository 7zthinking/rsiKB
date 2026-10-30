using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace KBEA.Core.Diff;

public sealed class TextDiffService
{
    public SideBySideDiffModel Compare(string oldText, string newText) =>
        new SideBySideDiffBuilder(new Differ()).BuildDiffModel(oldText, newText);
}
