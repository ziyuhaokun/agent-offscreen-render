#requires -Version 7.0
# 列出 wpf-render.json 中的视图（名称、合并 defaults 后的设置），供 MCP 的 list_views 调用。
# 合并规则来自 Preset.ps1，与 render.ps1 完全一致；输出 JSON 由工具原样转发。
param(
    [string]$Config
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Preset.ps1')
$path = Resolve-WpfRenderConfig $Config
$configDir = Split-Path -Parent $path
$file = Read-WpfRenderConfigFile $path

$views = @()
# views 缺失时 ``$file.views.Keys`` 是 None 上的属性访问，报「不能对 Null 值表达式调用方法」——对用户毫无指向性。
$viewTable = if ($file.views) { $file.views } else { [ordered]@{} }
foreach ($name in @($viewTable.Keys | Sort-Object)) {
    $merged = Merge-WpfRenderPreset $file $name
    $view = [ordered]@{
        name = $name
        stack = if ($merged.stack) { $merged.stack } else { 'auto' }
        viewType = $merged.viewType; xaml = $merged.xaml
        themes = $merged.themes; sizes = $merged.sizes; dpis = $merged.dpis
        watch = $merged.watch; hasBuild = $null -ne $merged.build
    }
    # xaml 的相对路径解析为相对配置文件所在目录（pack URI 等保持原样），与渲染时一致；viewType 是类型名，不解析。
    if ($view.xaml -is [string]) {
        $view.xaml = if ([IO.Path]::IsPathRooted($view.xaml) -or $view.xaml -like '*;component/*' -or $view.xaml -match '^(pack|avares|file|https?)://') {
            $view.xaml
        } else { [IO.Path]::GetFullPath((Join-Path $configDir $view.xaml)) }
    }
    $views += $view
}
[ordered]@{ config = $path; verifyOnStop = $file.verifyOnStop; views = $views } | ConvertTo-Json -Depth 6
