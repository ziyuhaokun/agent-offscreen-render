using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

// WinForms 宿主：视图放进一个从不显示的容器窗口，强制创建全部句柄后用 DrawToBitmap 出图。
// 不 Show：实测屏外 Show（即使不激活）也会改变前台窗口，会抢焦点。
// 结果文件与 WPF / Avalonia 宿主同一套：snapshot.png、manifest.json、layout.json、binding-errors.json。
internal static class Program
{
    private const long MaxPixels = 60_000_000;

    [STAThread]
    private static int Main(string[] args)
    {
        var exitCode = Run(args);
        // 业务代码可能留下前台线程（轮询、设备监听），进程会因此无法退出而被判为超时；结果已写完，直接结束进程。
        Console.Out.Flush();
        Console.Error.Flush();
        Environment.Exit(exitCode);
        return exitCode;
    }

    private static int Run(string[] args)
    {
        string? Optional(string key)
        {
            var index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        string Value(string key) => Optional(key) ?? throw new ArgumentException($"缺少参数 {key}");

        var outDir = Path.GetFullPath(Value("--out"));
        ConfiguredView.ConfigPath = Optional("--config");
        Directory.CreateDirectory(outDir);
        int exitCode;
        // 已检出的绑定问题在后续失败（元素名写错、崩溃）时也要能写进 binding-errors，所以提到 try 外。
        var log = new BindingLog();
        try
        {
            if (args.Contains("--window-host"))
                return Reject(outDir, "WinForms 宿主本期不支持 windowHost；请使用默认 layout-only 模式。", RenderOutcome.ExitFailed, log);
            if (HarnessConfig.Load(Optional("--config")) is { HasSteps: true })
                return Reject(outDir, "WinForms 宿主本期不支持交互步骤（steps）。请去掉 steps 后再渲染，或改用 WPF 宿主。", RenderOutcome.ExitStepFailed, log);
            var theme = Value("--theme");
            if (theme != "light")
                return Reject(outDir, $"WinForms 宿主本期只支持 light 主题（收到 {theme}）：WinForms 没有通用的主题切换，.NET 9 的暗色模式仍是实验 API。", RenderOutcome.ExitFailed, log);

            var dpiUnaware = Hosting.InitializeProcess();
            var width = int.Parse(Value("--width"), CultureInfo.InvariantCulture);
            var height = int.Parse(Value("--height"), CultureInfo.InvariantCulture);
            var dpi = int.Parse(Value("--dpi"), CultureInfo.InvariantCulture);

            // 探针与业务视图走同一个检查函数：探针的错误成员被检出，才说明检查链路有效。
            var marker = "AgentRenderSelfCheck_" + Guid.NewGuid().ToString("N");
            log.SelfCheckHit = BindingCheck.Probe(marker);

            var view = ViewFactory.Create(theme);
            log.Add(BindingCheck.Precheck(view));
            var host = Hosting.Attach(view, width, height);
            ViewFactory.WaitUntilReady(view);
            Hosting.Settle(host);
            log.Add(BindingCheck.Verify(view));

            var (background, backgroundSource) = ResolveBackground(Optional("--background") ?? "auto");
            var pngPath = Path.Combine(outDir, "snapshot.png");
            using (var bitmap = RenderControl(view, width, height, dpi, background))
                bitmap.Save(pngPath, ImageFormat.Png);

            var (elementRect, elementError) = CaptureElement(view, Optional("--element"), dpi, background, outDir);
            var (layoutIssueCount, layoutReportError) = WriteLayoutReport(view, width, height, outDir);

            string sha256;
            using (var sha = SHA256.Create())
                sha256 = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(pngPath))).Replace("-", "").ToLowerInvariant();

            var warning = dpi == 96 && dpiUnaware ? null : $"WinForms 宿主按 96 DPI 布局：请求的 DPI 只把位图按比例放大（栅格缩放），不等同于业务程序在高 DPI 显示器上的真实布局与字体。{(dpiUnaware ? "" : "另外本次进程未能设为 DPI 不感知，实际布局可能已按系统缩放放大。")}";
            var manifest = RenderOutcome.BuildManifest(
                new[] { new KeyValuePair<string, object?>("stack", "winforms") },
                Optional("--tool-version"), "layout-only", view.GetType().FullName, theme, width, height, dpi,
                view.Width, view.Height, background is { } color ? Hex(color) : null, backgroundSource,
                log.SelfCheckHit, layoutIssueCount, layoutReportError, Optional("--element"), elementRect, elementError,
                null, sha256,
                new[]
                {
                    new KeyValuePair<string, object?>("warning", warning),
                    new KeyValuePair<string, object?>("note", "WinForms hidden-host snapshot via DrawToBitmap; RichTextBox, WebBrowser, ActiveX and other native-rendered content may be missing; input and performance require separate verification."),
                });
            exitCode = RenderOutcome.ResolveExitCode(log.SelfCheckHit, stepsFailed: false, log.Errors.Count);
            if (!log.SelfCheckHit)
                File.WriteAllText(Path.Combine(outDir, "error.txt"), "绑定检查自检未命中，不能宣称绑定检查通过。");
            try { ViewFactory.Cleanup(view); }
            catch (Exception ex) { File.AppendAllText(Path.Combine(outDir, "error.txt"), ex.ToString()); exitCode = RenderOutcome.ExitFailed; }
            // 不 Dispose / Close 容器：业务 Form 的 FormClosing 可能弹确认框；进程随后直接结束。
            WriteResults(outDir, manifest, log, exitCode);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outDir, "error.txt"), Unwrap(ex).ToString());
            exitCode = RenderOutcome.ExitFailed;
            WriteResults(outDir, null, log, exitCode);
        }
        return exitCode;
    }

    private static int Reject(string outDir, string message, int exitCode, BindingLog log)
    {
        File.WriteAllText(Path.Combine(outDir, "error.txt"), message);
        WriteResults(outDir, null, log, exitCode);
        return exitCode;
    }

    // 反射调用（CreateControl 等）抛出的异常包在 TargetInvocationException 里，报告里要的是业务代码的原始异常。
    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
        return ex;
    }

    // 先铺底色再画控件；DPI 不是 96 时整体按比例放大。
    private static Bitmap RenderControl(Control control, int width, int height, int dpi, Color? background)
    {
        var bitmap = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.Clear(background ?? Color.Transparent);
        control.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
        if (dpi == 96) return bitmap;
        using (bitmap)
            return Scale(bitmap, RenderOutcome.PixelSize(width, dpi), RenderOutcome.PixelSize(height, dpi));
    }

    private static Bitmap Scale(Bitmap source, int width, int height)
    {
        var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY); // 边缘不混入外部的透明像素
        graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return scaled;
    }

    // -Element：另存指定 Name 的控件截图，返回其在截图中的矩形（DIP）与错误说明。
    private static (double[]? Rect, string? Error) CaptureElement(Control root, string? name, int dpi, Color? background, string outDir)
    {
        if (name is null) return (null, null);
        var target = Hosting.Descendants(root).FirstOrDefault(control => control.Name == name);
        // 与 WPF 宿主一致：找不到元素是调用错误，整次渲染失败（退出码 1）。
        if (target is null)
        {
            var names = string.Join(", ", Hosting.Descendants(root).Select(control => control.Name).Where(item => item.Length > 0).Distinct().Take(40));
            throw new InvalidOperationException($"找不到 Name 为 {name} 的可见控件。可用名称：{names}");
        }
        var bounds = Hosting.RootRect(target, root);
        var rect = new double[] { bounds.X, bounds.Y, bounds.Width, bounds.Height };
        var pixelWidth = Math.Max(1, RenderOutcome.PixelSize(bounds.Width, dpi));
        var pixelHeight = Math.Max(1, RenderOutcome.PixelSize(bounds.Height, dpi));
        if ((long)pixelWidth * pixelHeight > MaxPixels)
            return (rect, $"控件 {name} 为 {pixelWidth}×{pixelHeight} 像素，超过 {MaxPixels / 1_000_000} 百万像素上限，未生成 element.png。");
        if (bounds.Width < 1 || bounds.Height < 1)
            return (rect, $"控件 {name} 的尺寸为 {bounds.Width}×{bounds.Height}，未生成 element.png。");
        using var bitmap = RenderControl(target, bounds.Width, bounds.Height, dpi, background);
        bitmap.Save(Path.Combine(outDir, "element.png"), ImageFormat.Png);
        return (rect, null);
    }

    private static (int? Count, string? Error) WriteLayoutReport(Control root, int width, int height, string outDir)
    {
        try
        {
            var (report, count) = LayoutReport.Inspect(root, width, height);
            File.WriteAllText(Path.Combine(outDir, "layout.json"), Json.Write(report));
            return (count, null);
        }
        catch (Exception ex) { return (null, ex.GetType().Name + ": " + ex.Message); }
    }

    private static void WriteResults(string outDir, List<KeyValuePair<string, object?>>? manifest, BindingLog log, int exitCode) =>
        RenderOutcome.WriteResults(outDir, manifest, log.Errors, log.Issues, exitCode);

    // auto：铺系统控件底色（与业务窗体默认背景一致）；transparent：不铺；其余为 #RRGGBB、#AARRGGBB 或颜色名。
    private static (Color? Color, string Source) ResolveBackground(string value)
    {
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return (null, "transparent");
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return (SystemColors.Control, "theme-fallback");
        if (value.StartsWith("#", StringComparison.Ordinal) && (value.Length == 7 || value.Length == 9))
        {
            var argb = uint.Parse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (value.Length == 7) argb |= 0xFF000000;
            return (Color.FromArgb(unchecked((int)argb)), "explicit");
        }
        var named = Color.FromName(value);
        if (!named.IsKnownColor) throw new ArgumentException($"无法识别的背景色：{value}");
        return (named, "explicit");
    }

    private static string Hex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private sealed class BindingLog
    {
        public List<string> Errors { get; } = new();
        public List<object?> Issues { get; } = new();
        public bool SelfCheckHit { get; set; }

        public void Add(IEnumerable<BindingIssue> issues)
        {
            foreach (var issue in issues)
            {
                Errors.Add(issue.Summary);
                Issues.Add(issue.ToJson());
            }
        }
    }
}
