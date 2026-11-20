using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

// 三个宿主共用的外围骨架：命令行取值、layout.json 落盘、截图哈希。
// 以前这三段在每个 Program.cs 里各写一遍，改一处忘一处不会报错，只会让三档行为悄悄分叉。
// 只用 .NET Framework 4.7.2 与 .NET 5+ 共有的 API（net472 没有 Convert.ToHexString）。
internal sealed class CommandLine
{
    private readonly string[] args;

    internal CommandLine(string[] args) => this.args = args;

    internal string? Optional(string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    internal string Value(string key) => Optional(key) ?? throw new ArgumentException($"缺少参数 {key}");

    internal bool Has(string key) => Array.IndexOf(args, key) >= 0;
}

internal static class LayoutFile
{
    // 布局检查自己失败（遍历抛异常、文本测量出错）不能让整次渲染失败：
    // 返回错误串，由 manifest 的 layoutReportError 带出去，截图与绑定结论照常可用。
    internal static (int? Count, string? Error) Write(
        string outDir, Func<(List<KeyValuePair<string, object?>> Report, int Count)> inspect)
    {
        try
        {
            var (report, count) = inspect();
            File.WriteAllText(Path.Combine(outDir, "layout.json"), Json.Write(report));
            return (count, null);
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }
}

// binding-errors.json 的单条形状。三档各自拼过一次同样的 8 个字段，字段名或顺序改一处就漂移；
// 下游（summary 的 bindingIssues、MCP、hook）只认这条形状。
internal static class BindingIssueJson
{
    internal static List<KeyValuePair<string, object?>> Create(string summary, string? reason, string? path, string? dataItem,
        string? targetElement, string? targetName, string? targetProperty, string message) =>
        Json.Object(("summary", summary), ("reason", reason), ("path", path), ("dataItem", dataItem),
            ("targetElement", targetElement), ("targetName", string.IsNullOrEmpty(targetName) ? null : targetName),
            ("targetProperty", targetProperty), ("message", message));
}
internal static class SnapshotHash
{
    // manifest.sha256 供基线比对用：三档必须是同一算法与同一写法，否则同一张图在三档下哈希不同。
    internal static string OfFile(string path)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
    }
}
