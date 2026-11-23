namespace KBEA.Core.Plugins;

/// <summary>
/// 工具栏组的显示档位（P7）：由框架按可用宽度决定，插件不感知。
/// </summary>
public enum ToolbarGroupLayout
{
    /// <summary>图标 + 短文字（空间充足时的首选）。</summary>
    Full = 0,

    /// <summary>仅图标（宽度不够放文字，但组内按钮全部保留）。</summary>
    IconsOnly = 1,

    /// <summary>收起：只显示首按钮 + ▾ 下拉（其余按钮进下拉菜单）。</summary>
    Collapsed = 2,

    /// <summary>整体挪进「⋮ 更多」面板（组本身不显示）。</summary>
    Overflow = 3
}

/// <summary>
/// 工具栏溢出规划（框架层，docs/plugin-architecture.md §4、§7 P7）：
/// 决定每个工具组用哪一档显示（图标+文字 / 仅图标 / 收起 / 更多面板）。
///
/// 规则：
/// 1. 空间够 → 全部「图标+文字」；
/// 2. 放不下文字 → 全部降为「仅图标」；
/// 3. 仍放不下 → 从后往前把整组收起为「首按钮 + 下拉」，**每组至少保留一个按钮**；
/// 4. 再放不下 → 末尾的组整体进「⋮ 更多」面板；**最前的一组始终留在工具栏上**。
///
/// 纯逻辑（无 UI 依赖），便于单测覆盖。
/// </summary>
public static class ToolbarOverflowPlanner
{
    /// <summary>规划结果：每组一个档位。</summary>
    public readonly record struct Plan(IReadOnlyList<ToolbarGroupLayout> Layouts)
    {
        public ToolbarGroupLayout LayoutOf(int index) => Layouts[index];

        public bool IsFull(int index) => Layouts[index] == ToolbarGroupLayout.Full;

        public bool IsIconsOnly(int index) => Layouts[index] == ToolbarGroupLayout.IconsOnly;

        public bool IsCollapsed(int index) => Layouts[index] == ToolbarGroupLayout.Collapsed;

        public bool IsOverflow(int index) => Layouts[index] == ToolbarGroupLayout.Overflow;
    }

    /// <summary>单个组的宽度信息（由宿主按实际测量填入）。</summary>
    public readonly record struct GroupWidths(double FullWidth, double IconsOnlyWidth, double CollapsedWidth, int ButtonCount);

    /// <summary>
    /// 计算各组显示档位。
    /// </summary>
    /// <param name="availableWidth">工具栏可用宽度（px）。</param>
    /// <param name="groups">各组在各档位下的宽度。</param>
    /// <param name="overflowButtonWidth">「⋮ 更多」按钮宽度（px）。</param>
    public static Plan Compute(double availableWidth, IReadOnlyList<GroupWidths> groups,
        double overflowButtonWidth = 28)
    {
        var count = groups.Count;
        var layouts = new ToolbarGroupLayout[count];
        if (count == 0) return new Plan(layouts);
        if (availableWidth <= 0)
        {
            // 没有可用宽度：所有组都不显示
            for (var i = 0; i < count; i++) layouts[i] = ToolbarGroupLayout.Overflow;
            return new Plan(layouts);
        }

        // 档位 0：全部「图标+文字」
        for (var i = 0; i < count; i++) layouts[i] = ToolbarGroupLayout.Full;
        if (Total(layouts, groups) <= availableWidth)
            return new Plan(layouts);

        // 档位 1：全部降为「仅图标」
        for (var i = 0; i < count; i++) layouts[i] = ToolbarGroupLayout.IconsOnly;
        if (Total(layouts, groups) <= availableWidth)
            return new Plan(layouts);

        // 档位 2：从后往前收起（每组保留首按钮 + 下拉）
        var room = availableWidth - overflowButtonWidth - 2;
        var total = Total(layouts, groups);
        for (var i = count - 1; i >= 0 && total > room; i--)
        {
            if (groups[i].CollapsedWidth >= groups[i].IconsOnlyWidth) continue; // 收起并不更窄
            if (groups[i].ButtonCount <= 1) continue;                          // 单按钮组没有下拉可收
            layouts[i] = ToolbarGroupLayout.Collapsed;
            total = Total(layouts, groups);
        }
        if (total <= room) return new Plan(layouts);

        // 档位 3：末尾的组整体进「更多」面板；最前一组始终保留在工具栏上
        for (var i = count - 1; i >= 1 && total > room; i--)
        {
            total -= WidthOf(layouts[i], groups[i]);
            layouts[i] = ToolbarGroupLayout.Overflow;
        }

        return new Plan(layouts);
    }

    /// <summary>按当前档位累加所有组的宽度。</summary>
    private static double Total(ToolbarGroupLayout[] layouts, IReadOnlyList<GroupWidths> groups)
    {
        var sum = 0d;
        for (var i = 0; i < layouts.Length; i++)
            sum += WidthOf(layouts[i], groups[i]);
        return sum;
    }

    private static double WidthOf(ToolbarGroupLayout layout, GroupWidths widths) => layout switch
    {
        ToolbarGroupLayout.Full => widths.FullWidth,
        ToolbarGroupLayout.IconsOnly => widths.IconsOnlyWidth,
        ToolbarGroupLayout.Collapsed => widths.CollapsedWidth,
        _ => 0d
    };
}
