using System.ComponentModel;
using System.Reflection;
using System.Windows.Forms;

// 读取 harness.json，按类型名创建 WinForms 视图（UserControl、Form 或任意 Control），并注入预览数据。
internal static class ConfiguredView
{
    internal static string? ConfigPath { get; set; }

    public static Control? TryCreate(string theme)
    {
        var config = HarnessConfig.Load(ConfigPath);
        if (config is null) return null;
        config.EnsureExclusiveView();
        if (config.Xaml is not null)
            throw new InvalidOperationException("WinForms 宿主没有 XAML：请用 viewType 指定 UserControl / Form 类型。");
        if (config.ResourceDictionaries is { Length: > 0 })
            throw new InvalidOperationException("WinForms 宿主不支持 resourceDictionaries。");
        if (config.ViewType is null) return null;

        var view = Activator.CreateInstance(config.ResolveType(config.ViewType), nonPublic: true) as Control
            ?? throw new InvalidOperationException($"{config.ViewType} 不是 WinForms Control。");

        config.EnsureExclusiveDataContext();
        object? data = null;
        if (config.DataContextType is not null)
            data = Activator.CreateInstance(config.ResolveType(config.DataContextType), nonPublic: true);
        else if (config.DataContextFactory is not null)
            data = config.InvokeFactory(config.DataContextFactory);
        if (data is not null) Inject(view, data);
        return view;
    }

    // WinForms 没有统一的 DataContext。设计器生成的视图通常把 BindingSource.DataSource 设成类型（typeof(Vm)），
    // 运行时由业务代码换成实例：这里把预览实例放进所有「指向该类型或尚未设置」的 BindingSource；
    // .NET 7+ 的 Control.DataContext 也一并设上（.NET Framework 没有这个属性）。
    private static void Inject(Control view, object data)
    {
        var injected = 0;
        foreach (var source in BindingSources(view))
        {
            if (source.DataSource is null || source.DataSource is Type type && type.IsInstanceOfType(data))
            {
                source.DataSource = data;
                injected++;
            }
        }
        var dataContextProperty = typeof(Control).GetProperty("DataContext");
        var hasDataContext = dataContextProperty is { CanWrite: true };
        if (hasDataContext) dataContextProperty!.SetValue(view, data);
        // 只要一个 BindingSource 都没接上就报错：否则 net48 会失败而 net8 静默成功，同一份配置两边行为不同。
        if (injected == 0)
            throw new InvalidOperationException(
                $"{view.GetType().FullName} 上没有可以注入 {data.GetType().FullName} 的 BindingSource（DataSource 为空或为该类型）。" +
                (hasDataContext ? "已设置 Control.DataContext，但没有 BindingSource 接手。" : "") +
                "请改用 factoryFile 在 ViewFactory.Create 中自行设置数据。");
    }

    // 视图（含基类）字段里的 BindingSource，以及设计器 components 容器中的 BindingSource。
    private static IEnumerable<BindingSource> BindingSources(Control view)
    {
        var seen = new HashSet<BindingSource>();
        for (var type = view.GetType(); type is not null && type != typeof(Control); type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var value = field.GetValue(view);
                if (value is BindingSource source && seen.Add(source)) yield return source;
                if (value is IContainer container)
                {
                    foreach (var component in container.Components)
                        if (component is BindingSource nested && seen.Add(nested)) yield return nested;
                }
            }
        }
    }
}
