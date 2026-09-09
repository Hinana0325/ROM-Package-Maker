namespace RomPackageMaker.Application;

/// <summary>
/// 跨页面共享的会话状态（仅内存，不持久化）。
/// 解包完成后记录工作目录，供打包 / 精简 / ROOT / build.prop / 模板等页面自动跟随，
/// 避免每个页面各自指向「设置中的默认目录」而与当前工作脱节。
/// </summary>
public static class WorkspaceState
{
    /// <summary>本会话最近使用/解包的工作目录。</summary>
    public static string? CurrentWorkspace { get; set; }
}
