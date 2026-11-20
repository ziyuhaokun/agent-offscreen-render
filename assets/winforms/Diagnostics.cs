using System.Collections;
using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

// 一条绑定问题：目标控件与属性、数据成员、数据源的列表项类型、原因。
internal sealed class BindingIssue
{
    public BindingIssue(Control control, Binding binding, string reason)
    {
        TargetType = LayoutReport.TypeName(control);
        TargetName = string.IsNullOrEmpty(control.Name) ? null : control.Name;
        TargetProperty = binding.PropertyName;
        Member = binding.BindingMemberInfo.BindingMember;
        DataItem = BindingCheck.ItemTypeName(binding.DataSource);
        Reason = reason;
    }

    public string TargetType { get; }
    public string? TargetName { get; }
    public string TargetProperty { get; }
    public string Member { get; }
    public string? DataItem { get; }
    public string Reason { get; }

    public string Target => TargetType + (TargetName is null ? "" : "#" + TargetName);

    // 与 WPF / Avalonia 的单行结论同一形状：目标.属性 <- 路径（数据源）：原因。
    public string Summary => $"{Target}.{TargetProperty} <- DataMember={Member}{(DataItem is null ? "" : $"（数据源 {DataItem}）")}：{Reason}";

    public List<KeyValuePair<string, object?>> ToJson() =>
        BindingIssueJson.Create(Summary, Reason, Member, DataItem, TargetType, TargetName, TargetProperty, Summary);
}

// WinForms 没有 WPF 的绑定跟踪日志。绑定成员写错时，WinForms 在控件挂上 BindingContext 那一刻直接抛
// ArgumentException（Cannot bind to the property or column ...），整次渲染随之失败。
// 所以分两步：挂上之前逐个预检并摘掉有问题的绑定；挂上之后再读一遍值，记下格式化 / 类型转换失败。
internal static class BindingCheck
{
    // 自检：探针绑定到一个不存在的成员，必须被同一个预检函数检出。
    internal static bool Probe(string marker)
    {
        var panel = new Panel();
        var probe = new TextBox();
        probe.DataBindings.Add("Text", new ProbeSource(), marker);
        panel.Controls.Add(probe);
        return Precheck(panel).Any(issue => issue.Member == marker);
    }

    internal static List<BindingIssue> Precheck(Control root)
    {
        var issues = new List<BindingIssue>();
        foreach (var control in All(root))
        {
            foreach (var binding in control.DataBindings.Cast<Binding>().ToList())
            {
                // 数据源还没装上（设计器把 BindingSource.DataSource 留到运行时赋值）：无法判断成员对不对，
                // 但 WinForms 在挂上 BindingContext 时会直接抛「Cannot bind to the property or column」把整次渲染带崩，
                // 所以先摘掉——预览时这条绑定就是未解析状态（与 WPF 没有 DataContext 时一致），不算问题。
                if (UnderlyingDataSource(binding.DataSource) is null)
                {
                    control.DataBindings.Remove(binding);
                    continue;
                }
                if (Validate(control, binding) is not { } reason) continue;
                issues.Add(new BindingIssue(control, binding, reason));
                control.DataBindings.Remove(binding);
            }
        }
        return issues;
    }

    // 挂上之后重读每个绑定：值的格式化、类型转换在这一步失败。
    internal static List<BindingIssue> Verify(Control root)
    {
        var issues = new List<BindingIssue>();
        foreach (var control in All(root))
        {
            foreach (var binding in control.DataBindings.Cast<Binding>().ToList())
            {
                string? failure = null;
                void OnComplete(object? sender, BindingCompleteEventArgs e)
                {
                    if (e.BindingCompleteState != BindingCompleteState.Success)
                        failure ??= e.Exception?.Message ?? (string.IsNullOrEmpty(e.ErrorText) ? e.BindingCompleteState.ToString() : e.ErrorText);
                }
                binding.BindingComplete += OnComplete;
                try { binding.ReadValue(); }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException or InvalidOperationException or TargetInvocationException)
                {
                    failure ??= (ex.InnerException ?? ex).Message;
                }
                finally { binding.BindingComplete -= OnComplete; }
                if (failure is not null) issues.Add(new BindingIssue(control, binding, "读取值失败：" + failure));
            }
        }
        return issues;
    }

    private static string? Validate(Control control, Binding binding)
    {
        if (TypeDescriptor.GetProperties(control).Find(binding.PropertyName, true) is null)
            return $"控件上没有属性 {binding.PropertyName}";
        // 数据源还没装上（如设计器里 BindingSource.DataSource 在运行时才赋值）：Precheck 已经跳过，这里不会走到。
        if (UnderlyingDataSource(binding.DataSource) is null) return null;
        var info = binding.BindingMemberInfo;
        try
        {
            var properties = ListBindingHelper.GetListItemProperties(binding.DataSource);
            if (properties is null) return null;
            // 嵌套路径（如 Customer.Address.City）逐段向下找。
            foreach (var segment in info.BindingPath.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var property = properties.Find(segment, true);
                if (property is null) return $"数据源上找不到成员 {segment}";
                properties = ListBindingHelper.GetListItemProperties(property.PropertyType);
                if (properties is null) return null;
            }
            if (info.BindingField.Length == 0) return null;
            return properties.Find(info.BindingField, true) is null ? $"数据源上找不到成员 {info.BindingField}" : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return "无法解析数据成员：" + ex.Message;
        }
    }

    // 剥掉 BindingSource 包装后的真实数据源；未赋值时为 null。
    internal static object? UnderlyingDataSource(object? dataSource)
    {
        var source = dataSource;
        while (source is BindingSource bindingSource) source = bindingSource.DataSource;
        // 设计器里常把 DataSource 设成类型（运行时才换实例），这类不算「没装上」。
        return source;
    }

    // 数据源的列表项类型名（BindingSource 指向的类型、列表 / DataTable 的元素类型或对象本身的类型）。
    internal static string? ItemTypeName(object? dataSource)
    {
        var source = UnderlyingDataSource(dataSource);
        if (source is null) return null;
        if (source is Type type) return type.Name;
        // DataTable / DataView / 集合：取列表项类型（DataRowView 等），而不是容器类型名。
        var item = ListBindingHelper.GetListItemType(source) ?? source.GetType();
        return item.Name;
    }

    private static IEnumerable<Control> All(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (var item in All(child))
                yield return item;
    }

    private sealed class ProbeSource
    {
        public string Value { get; set; } = "";
    }
}

// 遍历控件树，输出每个控件的位置尺寸，并检查文本截断、被裁切、尺寸为 0、超出视口与重叠。
// 矩形都是相对根视图（即截图）的像素坐标（宿主按 96 DPI 布局，等于 DIP）；节点深度优先并带 depth，与 WPF 的 layout.json 契约一致。
internal static class LayoutReport
{
    private const double Tolerance = LayoutReportBuilder.Tolerance;

    public static (List<KeyValuePair<string, object?>> Report, int IssueCount) Inspect(Control root, int width, int height)
    {
        var report = new LayoutReportBuilder();

        // overflowClip：祖先客户区的交集（WinForms 总把子控件裁到父控件客户区）。
        // 根视图自己的客户区不算（超出它的报 out-of-viewport），开了 AutoScroll 的祖先是有意的滚动视口，
        // 它内部的内容既不报 clipped（滚一下就能看到）也不报 out-of-viewport（不是布局错误）。
        void Walk(Control control, int depth, Rectangle? overflowClip, bool insideScroller, bool placementReported)
        {
            if (!Hosting.IsShown(control)) return;
            var rect = Hosting.RootRect(control, root);
            var label = LabelOf(control);
            report.AddNode(depth, TypeName(control), control.Name, rect.X, rect.Y, rect.Width, rect.Height, TextOf(control));

            if (IsContent(control, root))
            {
                if (rect.Width < 1 || rect.Height < 1)
                {
                    report.ZeroSize(false, label, rect.Width, rect.Height, rect.X, rect.Y, rect.Width, rect.Height);
                }
                else
                {
                    // 位置类问题（clipped / out-of-viewport）不看滚动视口里的内容：滚一下就能看到，不是布局错误。
                    // 祖先已经报过位置问题后，后代不再重复报一条同类（与 WPF / Avalonia 一致）。
                    if (!insideScroller && !placementReported)
                    {
                        if (overflowClip is { } clip && !clip.Contains(rect))
                        {
                            var shown = Rectangle.Intersect(clip, rect);
                            report.Clipped(label, rect.Width, rect.Height, shown.Width, shown.Height, rect.X, rect.Y, rect.Width, rect.Height);
                            placementReported = true;
                        }
                        else if (rect.X < -Tolerance || rect.Y < -Tolerance || rect.Right > width + Tolerance || rect.Bottom > height + Tolerance)
                        {
                            report.OutOfViewport(label, width, height, rect.X, rect.Y, rect.Width, rect.Height);
                            placementReported = true;
                        }
                    }
                    // 文本截断与重叠与是否滚动无关：仍然要报。
                    if (Ink(control, rect, label, report) is { } ink) report.AddInk(label, ink.X, ink.Y, ink.Width, ink.Height);
                }
            }

            var scrolling = control is ScrollableControl { AutoScroll: true };
            if (!ReferenceEquals(control, root) && !scrolling)
            {
                var client = Hosting.RootClientRect(control, root);
                overflowClip = overflowClip is { } current ? Rectangle.Intersect(current, client) : client;
            }
            foreach (Control child in control.Controls)
                Walk(child, depth + 1, overflowClip, insideScroller || scrolling, placementReported);
        }

        Walk(root, 0, null, false, false);
        return report.Build(width, height);
    }

    // 内容控件：没有子控件，且不是纯容器。
    private static bool IsContent(Control control, Control root) =>
        control.Controls.Count == 0 && control is not Panel and not Splitter && (control is not Form || ReferenceEquals(control, root));

    // 重叠检查用的区域：只拿真正显示文字的控件算墨迹（与 WPF / Avalonia 只比较文本墨迹一致），
    // 其他控件整个矩形参与比较会把「有意叠放」误报成重叠。
    private static Rectangle? Ink(Control control, Rectangle rect, string label, LayoutReportBuilder report)
    {
        if (control is Label or ButtonBase) return CheckText(control, rect, label, report);
        // 输入框 / 下拉框 / 分组框：文字放不下很常见（可滚动或自带省略），只拿来判重叠，不报截断。
        if (control is TextBoxBase or ComboBox or GroupBox && !string.IsNullOrEmpty(control.Text))
            return MeasureInk(control, rect);
        return null;
    }

    private static Rectangle? MeasureInk(Control control, Rectangle rect)
    {
        var (area, alignment) = TextArea(control, rect);
        if (area.Width <= 0 || area.Height <= 0) return null;
        var needed = Measure(control, control.Text, int.MaxValue, wrap: false);
        var inkWidth = Math.Min(needed.Width, area.Width);
        var inkHeight = Math.Min(needed.Height, area.Height);
        return new Rectangle(area.X + Offset(alignment, horizontal: true, area.Width - inkWidth),
            area.Y + Offset(alignment, horizontal: false, area.Height - inkHeight), inkWidth, inkHeight);
    }

    // 文字是否放得下：Label 与输入框按可用宽度换行；按钮不换行，放不下就是水平裁切。
    private static Rectangle? CheckText(Control control, Rectangle rect, string label, LayoutReportBuilder report)
    {
        var text = control.Text;
        if (string.IsNullOrEmpty(text)) return null;
        var (area, alignment) = TextArea(control, rect);
        if (area.Width <= 0 || area.Height <= 0) return null;
        // 按钮、下拉框不换行：单行宽度超出可用宽度即裁切。Label 不自动大小时会换行，量高度。
        var wraps = control is Label;
        var needed = Measure(control, text, wraps ? area.Width : int.MaxValue, wraps);
        if (!control.AutoSize)
        {
            var truncated = wraps ? needed.Height > area.Height + Tolerance : needed.Width > area.Width + Tolerance;
            if (truncated)
            {
                var trimmed = control is Label { AutoEllipsis: true } || control is ButtonBase { AutoEllipsis: true };
                if (wraps) report.TextTruncated(label, wrapped: true, needed.Height, area.Height, trimmed, rect.X, rect.Y, rect.Width, rect.Height);
                else report.TextTruncated(label, wrapped: false, needed.Width, area.Width, trimmed, rect.X, rect.Y, rect.Width, rect.Height);
            }
        }
        var inkWidth = Math.Min(needed.Width, area.Width);
        var inkHeight = Math.Min(needed.Height, area.Height);
        var x = area.X + Offset(alignment, horizontal: true, area.Width - inkWidth);
        var y = area.Y + Offset(alignment, horizontal: false, area.Height - inkHeight);
        return new Rectangle(x, y, inkWidth, inkHeight);
    }

    // 文字可用区域（截图坐标）：客户区减去 Padding；按钮再减去边框，复选框 / 单选框再减去勾选框。
    private static (Rectangle Area, ContentAlignment Alignment) TextArea(Control control, Rectangle rect)
    {
        var padding = control.Padding;
        var area = new Rectangle(rect.X + padding.Left, rect.Y + padding.Top,
            rect.Width - padding.Horizontal, rect.Height - padding.Vertical);
        var alignment = control switch { Label l => l.TextAlign, ButtonBase b => b.TextAlign, _ => ContentAlignment.TopLeft };
        if (control is CheckBox or RadioButton)
        {
            const int glyph = 17; // 勾选框 13 px + 间距
            area = new Rectangle(area.X + glyph, area.Y, area.Width - glyph, area.Height);
        }
        else if (control is ButtonBase)
        {
            area.Inflate(-4, -4);
        }
        return (area, alignment);
    }

    private static Size Measure(Control control, string text, int width, bool wrap)
    {
        var useMnemonic = control switch { Label l => l.UseMnemonic, ButtonBase b => b.UseMnemonic, _ => true };
        var compatible = control switch { Label l => l.UseCompatibleTextRendering, ButtonBase b => b.UseCompatibleTextRendering, _ => false };
        if (compatible)
        {
            using var bitmap = new Bitmap(1, 1);
            using var graphics = Graphics.FromImage(bitmap);
            var size = wrap ? graphics.MeasureString(text, control.Font, width) : graphics.MeasureString(text, control.Font);
            return new Size((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
        }
        var flags = wrap ? TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl : TextFormatFlags.SingleLine | TextFormatFlags.TextBoxControl;
        if (!useMnemonic) flags |= TextFormatFlags.NoPrefix;
        return wrap
            ? TextRenderer.MeasureText(text, control.Font, new Size(width, int.MaxValue), flags)
            : TextRenderer.MeasureText(text, control.Font, new Size(int.MaxValue, int.MaxValue), flags);
    }

    private static int Offset(ContentAlignment alignment, bool horizontal, int spare)
    {
        if (spare <= 0) return 0;
        var middle = horizontal
            ? alignment is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter
            : alignment is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight;
        var end = horizontal
            ? alignment is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight
            : alignment is ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight;
        return end ? spare : middle ? spare / 2 : 0;
    }

    private static string? TextOf(Control control) =>
        control is Label or ButtonBase or TextBoxBase or GroupBox or ComboBox ? control.Text : null;

    private static string LabelOf(Control control) => LayoutReportBuilder.Label(TypeName(control), control.Name, TextOf(control));

    internal static string TypeName(Control control) => LayoutReportBuilder.TypeName(control.GetType(), "System.Windows.Forms");
}
