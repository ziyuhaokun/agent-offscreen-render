using System.Drawing;
using System.Windows.Forms;

// 视图工厂：harness.json 指定了 viewType 时由 ConfiguredView 创建；否则渲染内置样例。
// 需要定制时用 factoryFile 替换本文件，保留这三个方法的签名。
internal static class ViewFactory
{
    public static Control Create(string theme)
    {
        if (ConfiguredView.TryCreate(theme) is { } configured) return configured;
        var panel = new Panel { BackColor = Color.White };
        panel.Controls.Add(new Label
        {
            Text = "Agent Render · WinForms",
            Font = new Font("Segoe UI", 20f),
            AutoSize = true,
            Location = new Point(32, 32),
        });
        panel.Controls.Add(new Label
        {
            Text = "Independent WinForms snapshot / 独立离屏渲染",
            ForeColor = Color.DimGray,
            AutoSize = true,
            Location = new Point(34, 84),
        });
        panel.Controls.Add(new Button
        {
            Text = "Sample button / 样例按钮",
            Size = new Size(240, 40),
            Location = new Point(32, 120),
        });
        return panel;
    }

    // 控件已创建句柄、Load 已触发；需要等异步数据时在这里同步等待（可循环 Application.DoEvents）。
    public static void WaitUntilReady(Control view) { }

    public static void Cleanup(Control view) { }
}
