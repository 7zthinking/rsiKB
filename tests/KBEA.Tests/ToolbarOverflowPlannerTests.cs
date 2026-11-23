using KBEA.Core.Plugins;

namespace KBEA.Tests;

/// <summary>工具栏布局规划（P7）：框架级四档降级——图标+文字 → 仅图标 → 首按钮+下拉 → 更多面板。</summary>
public sealed class ToolbarOverflowPlannerTests
{
    private static ToolbarOverflowPlanner.GroupWidths Group(double full, double icons, double collapsed, int buttons = 2)
        => new(full, icons, collapsed, buttons);

    [Fact]
    public void Compute_UsesFullLayoutWhenSpaceIsEnough()
    {
        var plan = ToolbarOverflowPlanner.Compute(
            availableWidth: 600,
            groups: [Group(100, 60, 40), Group(200, 120, 40), Group(120, 70, 40)]);

        Assert.All(plan.Layouts, l => Assert.Equal(ToolbarGroupLayout.Full, l));
    }

    [Fact]
    public void Compute_FallsBackToIconsOnlyWhenLabelsDoNotFit()
    {
        // 图标+文字总宽 420 > 300；但仅图标总宽 250 <= 300
        var plan = ToolbarOverflowPlanner.Compute(
            availableWidth: 300,
            groups: [Group(100, 60, 40), Group(200, 120, 40), Group(120, 70, 40)]);

        Assert.All(plan.Layouts, l => Assert.Equal(ToolbarGroupLayout.IconsOnly, l));
    }

    [Fact]
    public void Compute_CollapsesGroupsFromTheEndWhenIconsOnlyStillDoesNotFit()
    {
        // 可用 200（room=170）：仅图标总宽 60+120+70=250 > 170
        // 收起最后一组 → 60+120+40=220 仍超 → 再收起中间组 → 60+40+40=140 放得下
        var plan = ToolbarOverflowPlanner.Compute(
            availableWidth: 200,
            groups: [Group(100, 60, 40), Group(200, 120, 40), Group(120, 70, 40)]);

        Assert.Equal(ToolbarGroupLayout.Collapsed, plan.LayoutOf(2));
        Assert.Equal(ToolbarGroupLayout.Collapsed, plan.LayoutOf(1));
        Assert.Equal(ToolbarGroupLayout.IconsOnly, plan.LayoutOf(0));
    }

    [Fact]
    public void Compute_MovesTrailingGroupsIntoOverflowPanel()
    {
        // room=170：收起全部后 40*3=120 <= 170，仍放得下 → 不需要更多面板
        // 这里把可用压到 100：收起后 120 > 70 → 末尾组进更多面板
        var plan = ToolbarOverflowPlanner.Compute(
            availableWidth: 100,
            groups: [Group(100, 60, 40), Group(200, 120, 40), Group(120, 70, 40)]);

        Assert.Contains(ToolbarGroupLayout.Overflow, plan.Layouts);
        // 最前一组永远保留在工具栏上
        Assert.NotEqual(ToolbarGroupLayout.Overflow, plan.LayoutOf(0));
    }

    [Fact]
    public void Compute_KeepsAtLeastOneButtonPerCollapsedGroup()
    {
        // 收起态宽度就是首按钮+下拉，故收起后总宽 <= room 即满足「每组至少一个按钮」
        var plan = ToolbarOverflowPlanner.Compute(
            availableWidth: 130,
            groups: [Group(100, 60, 40), Group(200, 120, 40), Group(120, 70, 40)]);

        var collapsedCount = plan.Layouts.Count(l => l == ToolbarGroupLayout.Collapsed);
        Assert.True(collapsedCount > 0);
        // 收起后的宽度合计 = 60(未收起) + 40*(收起数)
        var total = 0d;
        for (var i = 0; i < plan.Layouts.Count; i++)
        {
            total += plan.LayoutOf(i) switch
            {
                ToolbarGroupLayout.IconsOnly => 60,
                ToolbarGroupLayout.Collapsed => 40,
                ToolbarGroupLayout.Overflow => 0,
                _ => 100
            };
        }
        Assert.True(total <= 130);
    }

    [Fact]
    public void Compute_DoesNotCollapseSingleButtonGroups()
    {
        // 单按钮组没有「▾ 下拉」可收，收起也无意义
        var plan = ToolbarOverflowPlanner.Compute(
            availableWidth: 30,
            groups: [Group(100, 60, 40, buttons: 1)]);

        Assert.NotEqual(ToolbarGroupLayout.Collapsed, plan.LayoutOf(0));
    }

    [Fact]
    public void Compute_HandlesEmptyAndZeroWidth()
    {
        var empty = ToolbarOverflowPlanner.Compute(600, []);
        Assert.Empty(empty.Layouts);

        // 没有可用宽度 → 所有组都不显示
        var zero = ToolbarOverflowPlanner.Compute(0, [Group(100, 60, 40)]);
        Assert.All(zero.Layouts, l => Assert.Equal(ToolbarGroupLayout.Overflow, l));
    }

    [Fact]
    public void Compute_SkipsGroupsThatWouldNotGetNarrowerWhenCollapsed()
    {
        // 收起宽度 >= 仅图标宽度：收起没有收益
        var plan = ToolbarOverflowPlanner.Compute(
            availableWidth: 60,
            groups: [Group(100, 40, 40, buttons: 3)]);

        Assert.NotEqual(ToolbarGroupLayout.Collapsed, plan.LayoutOf(0));
    }
}
