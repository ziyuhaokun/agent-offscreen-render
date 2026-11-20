using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 隐藏容器：视图 Dock=Fill 放进一个从不显示的 Form，强制创建全部句柄。
// 实测（net48 / net8 / net10）：UserControl.Load 与嵌入的 Form.Load 都会触发，前台窗口不变，DrawToBitmap 画得出子控件。
internal static class Hosting
{
    private const int GwlStyle = -16;
    private const int WsVisible = 0x10000000;

    // 公开的 CreateControl() 只处理可见控件；容器从不显示，只能用内部的 CreateControl(bool ignoreVisible)。
    private static readonly MethodInfo? CreateControlIgnoringVisible = typeof(Control).GetMethod("CreateControl",
        BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    // 必须在创建任何窗口之前调用。返回 DPI 模式是否真的设成了不感知（false 时调用方要在 warning 里说明）。
    internal static bool InitializeProcess()
    {
        // 业务代码（Load、Paint 等）里的异常直接抛出，不弹 ThreadExceptionDialog 把进程挂到超时。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        // 进程不感知 DPI：系统按 96 DPI 报告给进程，布局与字体度量与机器的缩放设置无关，结果可复现。
        // .NET Framework 不写清单时本来就不感知；.NET Core 3.0+ 显式设置（API 不存在于 .NET Framework，用反射）。
        var dpiUnaware = true;
        var mode = Type.GetType("System.Windows.Forms.HighDpiMode, System.Windows.Forms");
        var setMode = typeof(Application).GetMethod("SetHighDpiMode", BindingFlags.Public | BindingFlags.Static);
        if (mode is not null && setMode is not null)
            dpiUnaware = setMode.Invoke(null, new[] { Enum.Parse(mode, "DpiUnaware") }) is true;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        return dpiUnaware;
    }

    internal static Form Attach(Control view, int width, int height)
    {
        if (CreateControlIgnoringVisible is null)
            throw new InvalidOperationException("当前 WinForms 版本没有 Control.CreateControl(bool)，无法在不显示窗口的情况下创建控件。");
        var host = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            AutoScaleMode = AutoScaleMode.None,
            ClientSize = new Size(width, height),
        };
        if (view is Form form)
        {
            // 业务 Form 作为子控件嵌入：只截客户区（与 WPF 截 Window.Content 一致），Load 照常触发。
            form.TopLevel = false;
            form.FormBorderStyle = FormBorderStyle.None;
        }
        view.Dock = DockStyle.Fill;
        host.Controls.Add(view);
        view.Visible = true;
        CreateControlIgnoringVisible.Invoke(host, new object[] { true });
        Settle(host);
        return host;
    }

    // 处理排队的消息（BeginInvoke、布局）并完成一轮布局。
    internal static void Settle(Form host)
    {
        Application.DoEvents();
        host.PerformLayout();
        Application.DoEvents();
    }

    // 控件自己是否可见。容器从不显示，Control.Visible 对所有子控件都返回 false，只能看窗口样式里的 WS_VISIBLE。
    internal static bool IsShown(Control control) =>
        !control.IsHandleCreated || (GetWindowLong(control.Handle, GwlStyle) & WsVisible) != 0;

    // 深度优先的可见控件（含 root）。
    internal static IEnumerable<Control> Descendants(Control root)
    {
        if (!IsShown(root)) yield break;
        yield return root;
        foreach (Control child in root.Controls)
            foreach (var item in Descendants(child))
                yield return item;
    }

    // 控件在截图（根视图窗口矩形）坐标系中的矩形；Bounds 相对父控件客户区，不能直接用。
    internal static Rectangle RootRect(Control control, Control root)
    {
        var origin = ScreenBounds(root).Location;
        var bounds = ScreenBounds(control);
        bounds.Offset(-origin.X, -origin.Y);
        return bounds;
    }

    // 控件客户区在截图坐标系中的矩形（子控件被裁切到这里）。
    internal static Rectangle RootClientRect(Control control, Control root)
    {
        var origin = ScreenBounds(root).Location;
        var client = control.RectangleToScreen(control.ClientRectangle);
        client.Offset(-origin.X, -origin.Y);
        return client;
    }

    private static Rectangle ScreenBounds(Control control) =>
        control.Parent is { } parent ? parent.RectangleToScreen(control.Bounds) : control.Bounds;
}
