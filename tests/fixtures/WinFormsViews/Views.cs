using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace WinFormsViews
{
    // 无参构造，可由 -DataContextType 直接创建；BindingSource 以它的类型作 DataSource，宿主渲染前换成实例。
    public sealed class OrderPreviewVm
    {
        public string CustomerName { get; set; } = "Contoso 有限公司";
        public string OrderNumber { get; set; } = "#1024";
        public decimal Total { get; set; } = 1024.5m;
    }

    // 只能经 dataContextFactory 静态工厂方法创建（演示 -DataContextFactory 走的是反射调用）。
    public static class PreviewData
    {
        public static OrderPreviewVm CreateRemoteOrder() =>
            new OrderPreviewVm { CustomerName = "Fabrikam 股份有限公司", OrderNumber = "#2048" };
    }

    // 正常视图：宿主把四个控件都塞进 1280×800 的容器里，640×360 下也不得出现任何布局问题，
    // 所以全部用绝对坐标叠在左上角，控件之间留出大于一行的间距。
    public class OrderView : UserControl
    {
        // 注入点：字段名与类型固定（宿主按 BindingSource 字段找注入目标），DataSource 先指向 VM 类型。
        private readonly BindingSource orderSource;
        // 设计器风格的 components 容器：宿主会扫描其中的 BindingSource。
        private readonly IContainer components = new Container();

        private readonly Label TitleLabel;
        private readonly TextBox CustomerBox;
        private readonly Label TotalLabel;
        private readonly Button SubmitButton;

        public OrderView()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;

            orderSource = new BindingSource(components) { DataSource = typeof(OrderPreviewVm) };

            TitleLabel = new Label { Name = "TitleLabel", AutoSize = true, Location = new Point(24, 20), Text = "订单" };
            CustomerBox = new TextBox { Name = "CustomerBox", Location = new Point(24, 56), Size = new Size(320, 25) };
            CustomerBox.DataBindings.Add(nameof(TextBox.Text), orderSource, nameof(OrderPreviewVm.CustomerName), true);
            TotalLabel = new Label { Name = "TotalLabel", AutoSize = true, Location = new Point(24, 96) };
            TotalLabel.DataBindings.Add(nameof(Label.Text), orderSource, nameof(OrderPreviewVm.Total), true);
            SubmitButton = new Button { Name = "SubmitButton", Location = new Point(24, 130), Size = new Size(120, 36), Text = "提交" };

            Controls.AddRange(new Control[] { TitleLabel, CustomerBox, TotalLabel, SubmitButton });
        }
    }

    // 故意把 CustomerName 拼成 CustomerNmae：宿主应在挂上 BindingContext 之前发现，渲染以退出码 2 报告绑定问题。
    public sealed class BrokenBindingView : UserControl
    {
        private readonly BindingSource orderSource;
        private readonly IContainer components = new Container();

        private readonly TextBox CustomerBox;
        private readonly TextBox BrokenBox;

        public BrokenBindingView()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;

            orderSource = new BindingSource(components) { DataSource = typeof(OrderPreviewVm) };

            CustomerBox = new TextBox { Name = "CustomerBox", Location = new Point(24, 20), Size = new Size(320, 25) };
            CustomerBox.DataBindings.Add(nameof(TextBox.Text), orderSource, nameof(OrderPreviewVm.CustomerName), true);
            BrokenBox = new TextBox { Name = "BrokenBox", Location = new Point(24, 60), Size = new Size(320, 25) };
            BrokenBox.DataBindings.Add(nameof(TextBox.Text), orderSource, "CustomerNmae", true);

            Controls.AddRange(new Control[] { CustomerBox, BrokenBox });
        }
    }

    // 五类布局问题各一处（不绑定，纯布局）：text-truncated / clipped / overlap / zero-size / out-of-viewport。
    // 彼此留出足够间距，避免一类问题带出另一类：问题区域集中在 640×360 以内，默认 1280×800 视口下也一致。
    public sealed class LayoutProblemsView : UserControl
    {
        public LayoutProblemsView()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;

            // text-truncated：AutoSize=false 的 180×20 标签装不下这句中英文混排标题（换行后高度超出）。
            var truncated = new Label
            {
                Name = "TruncatedTitle", AutoSize = false, Location = new Point(16, 16), Size = new Size(180, 20),
                Text = "这是一个非常长的中英文混排标题 This title is far too long for its box"
            };

            // clipped：按钮一半伸出 AutoScroll=false 的 Panel（Panel 客户区右边界在 200），可见部分仍放得下 "OK"。
            var clipPanel = new Panel { Name = "ClipPanel", AutoScroll = false, Location = new Point(16, 60), Size = new Size(200, 40) };
            var clipped = new Button
            {
                Name = "ClippedButton", AutoSize = false, Location = new Point(150, 4), Size = new Size(100, 32), Text = "OK"
            };
            clipPanel.Controls.Add(clipped);

            // overlap：两个 AutoSize 标签几乎同位，文字区域互相盖住。
            var overlapA = new Label { Name = "OverlapA", AutoSize = true, Location = new Point(16, 140), Text = "Overlapping text A" };
            var overlapB = new Label { Name = "OverlapB", AutoSize = true, Location = new Point(20, 144), Text = "Overlapping text B" };

            // zero-size：AutoSize=false 且尺寸为 0×0，文字完全不可见。
            var zeroSize = new Label
            {
                Name = "ZeroSizeText", AutoSize = false, Location = new Point(400, 140), Size = new Size(0, 0), Text = "Invisible"
            };

            // out-of-viewport：X 远超 1280 的视口宽度，落在画面之外。
            var offscreen = new Label { Name = "OffscreenNote", AutoSize = true, Location = new Point(2000, 300), Text = "Offscreen note" };

            Controls.AddRange(new Control[] { truncated, clipPanel, overlapA, overlapB, zeroSize, offscreen });
        }
    }

    // 布局检查的反例：AutoScroll 面板里的 60 行总高度远超视口，属于有意的滚动区域，不应报告任何问题。
    public sealed class ScrollListView : UserControl
    {
        public ScrollListView()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;

            var rows = new Panel { Name = "ScrollPanel", Dock = DockStyle.Fill, AutoScroll = true };
            for (var i = 1; i <= 60; i++)
            {
                rows.Controls.Add(new Label
                {
                    Name = "Row" + i, AutoSize = true, Location = new Point(16, (i - 1) * 24 + 8), Text = "Row " + i
                });
            }
            Controls.Add(rows);
        }
    }

    // Form 作为视图（宿主设 TopLevel=false、FormBorderStyle=None 嵌入）：Load 里改文字，证明 Form.Load 会触发。
    public sealed class OrderForm : Form
    {
        private readonly Label StatusLabel;

        public OrderForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            Text = "Order";

            StatusLabel = new Label { Name = "StatusLabel", AutoSize = true, Location = new Point(24, 24), Text = "not loaded" };
            Controls.Add(StatusLabel);
            Load += (sender, e) => StatusLabel.Text = "Loaded by Form.Load";
        }
    }

    // 按钮不换行：文字比按钮宽时必须报 text-truncated（水平裁切），不能当成「换行后高度不够」。
    public sealed class TextFitView : UserControl
    {
        public TextFitView()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            var wide = new Button
            {
                Name = "WideButton",
                AutoSize = false,
                Location = new Point(24, 24),
                Size = new Size(120, 32),
                Text = "这个按钮文字明显超出按钮宽度",
            };
            // 同一行放一个放得下的按钮，确认不会连它一起报。
            var fits = new Button { Name = "FitButton", AutoSize = false, Location = new Point(160, 24), Size = new Size(160, 32), Text = "OK" };
            Controls.AddRange(new Control[] { wide, fits });
        }
    }

    // 设计器形态：BindingSource 的 DataSource 在构造时还是 null，绑定成员故意写错。
    // 宿主无法判断「尚未赋值」还是「写错了」，不得报成绑定问题（否则外部注入数据的视图全会被误报）。
    public sealed class EmptySourceView : UserControl
    {
        private readonly BindingSource orderSource = new BindingSource();

        public EmptySourceView()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            var box = new TextBox
            {
                Name = "PendingBox",
                Location = new Point(24, 24),
                Size = new Size(320, 25),
                Text = "waiting for data",
            };
            box.DataBindings.Add(nameof(TextBox.Text), orderSource, "CustomerNmae");
            Controls.Add(box);
        }
    }
}
