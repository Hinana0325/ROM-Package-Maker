using System.Text;
using System.Text.RegularExpressions;

namespace RomPackageMaker.Application;

/// <summary>
/// hosts 广告过滤：在工作区 system/etc/hosts 中维护带标记的屏蔽块。
/// 块以 "# BEGIN ROM TOOL AD BLOCK" / "# END ROM TOOL AD BLOCK" 包裹，可整体移除、可重复应用（幂等并集）。
/// </summary>
public static class HostsService
{
    public const string BeginMarker = "# BEGIN ROM TOOL AD BLOCK";
    public const string EndMarker = "# END ROM TOOL AD BLOCK";

    private static readonly Regex DomainRegex = new(
        @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>内置精简广告/追踪域名列表（国内外常见的广告投放与统计上报域名，保守挑选）。</summary>
    public static readonly string[] BuiltInDomains =
    {
        // Google / DoubleClick 广告投放与统计
        "ad.doubleclick.net", "googleads.g.doubleclick.net", "stats.g.doubleclick.net",
        "partnerad.l.doubleclick.net", "s0.2mdn.net", "s1.2mdn.net",
        "pagead2.googlesyndication.com", "tpc.googlesyndication.com",
        "googleadservices.com", "www.googleadservices.com", "adservice.google.com",
        "analytics.google.com", "app-measurement.com",
        // 腾讯广点通 / 腾讯广告
        "gdt.qq.com", "mi.gdt.qq.com", "win.gdt.qq.com", "ads.qq.com", "ad.qq.com",
        "tui.qq.com", "adsmind.ugdtimg.com", "pgdt.ugdtimg.com", "sdk.e.qq.com",
        // 百度推广 / 百度统计
        "pos.baidu.com", "cpro.baidu.com", "als.baidu.com", "union.baidu.com",
        "baidumobads.baidu.com", "mobads.baidu.com", "hm.baidu.com",
        // 小米广告 / 小米统计
        "ad.xiaomi.com", "api.ad.xiaomi.com", "adv.sec.miui.com", "adv.sec.intl.miui.com",
        "sdkconfig.ad.xiaomi.com", "data.mistat.xiaomi.com", "applog.mi.com",
        // 友盟 / 网易 / CNZZ 等统计
        "alog.umeng.com", "alogs.umeng.com", "analytics.163.com",
        "c.cnzz.com", "v.cnzz.com",
        // 国际广告联盟 / 再营销
        "ib.adnxs.com", "cdn.adnxs.net", "track.adform.net", "cas.criteo.com",
        "b.scorecardresearch.com", "sb.scorecardresearch.com", "pixel.quantserve.com",
        "cdn.taboola.com", "trc.taboola.com", "widgets.outbrain.com", "tr.outbrain.com",
        "api.mixpanel.com", "cdn.mxpnl.com",
        // 其他
        "ads.msn.com", "ads1.msn.com", "ad.yieldmanager.com", "adriver.ru", "an.yandex.ru",
        "metric.igexin.com",
    };

    /// <summary>hosts 文件路径（system 根或 super 嵌套下）。</summary>
    public static string GetHostsPath(string workspaceDir) =>
        Path.Combine(
            RootService.FindSystemDir(workspaceDir) ?? Path.Combine(workspaceDir, "system"),
            "etc", "hosts");

    /// <summary>读取 hosts 文本；文件不存在返回 null。</summary>
    public static string? ReadHosts(string workspaceDir)
    {
        string path = GetHostsPath(workspaceDir);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>解析 hosts 文本中屏蔽块内的域名列表；无块返回空列表。</summary>
    public static List<string> GetBlockedDomains(string hostsText)
    {
        var list = new List<string>();
        int begin = hostsText.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0) return list;
        int end = hostsText.IndexOf(EndMarker, StringComparison.Ordinal);
        if (end < 0) end = hostsText.Length;

        foreach (var rawLine in hostsText[begin..end].Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (!line.StartsWith("0.0.0.0 ", StringComparison.OrdinalIgnoreCase)) continue;
            string domain = line["0.0.0.0 ".Length..].Trim();
            if (domain.Length > 0) list.Add(domain);
        }
        return list;
    }

    /// <summary>写入屏蔽块（与块内已有域名取并集，幂等）。返回写入后的域名总数。</summary>
    public static int Apply(string workspaceDir, IEnumerable<string> domains)
    {
        string? systemDir = RootService.FindSystemDir(workspaceDir)
            ?? throw new InvalidOperationException("工作目录中没有 system 分区目录（需先解包 system.img 或刷机包）。");

        string path = Path.Combine(systemDir, "etc", "hosts");
        string existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

        var merged = new HashSet<string>(GetBlockedDomains(existing), StringComparer.OrdinalIgnoreCase);
        foreach (var domain in domains)
        {
            if (IsValidDomain(domain)) merged.Add(domain);
        }
        if (merged.Count == 0) throw new ArgumentException("没有可写入的有效域名。");

        var sb = new StringBuilder(StripBlock(existing));
        // 保证块前内容以换行结尾
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
        sb.Append(BeginMarker).Append('\n');
        foreach (var domain in merged.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("0.0.0.0 ").Append(domain).Append('\n');
        }
        sb.Append(EndMarker).Append('\n');

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return merged.Count;
    }

    /// <summary>移除屏蔽块（保留用户原有内容）。返回是否发生了修改。</summary>
    public static bool Remove(string workspaceDir)
    {
        string path = GetHostsPath(workspaceDir);
        if (!File.Exists(path)) return false;
        string text = File.ReadAllText(path);
        string stripped = StripBlock(text);
        if (stripped == text) return false;
        File.WriteAllText(path, stripped, new UTF8Encoding(false));
        return true;
    }

    /// <summary>解析用户自定义域名输入（逗号 / 分号 / 空白 / 换行分隔），过滤非法项并去重。</summary>
    public static List<string> ParseCustomDomains(string text)
    {
        var result = new List<string>();
        foreach (var token in text.Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsValidDomain(token) && !result.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(token);
            }
        }
        return result;
    }

    /// <summary>简单域名格式校验。</summary>
    public static bool IsValidDomain(string domain) =>
        domain.Length is >= 4 and <= 253 && DomainRegex.IsMatch(domain);

    /// <summary>移除文本中的整个屏蔽块（含标记行及其行尾换行）。</summary>
    private static string StripBlock(string text)
    {
        int begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0) return text;
        int end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        int stop = end >= 0 ? end + EndMarker.Length : begin + BeginMarker.Length;
        // 吃掉块末尾的换行，避免留下空行堆积
        while (stop < text.Length && (text[stop] == '\n' || text[stop] == '\r')) stop++;
        return text[..begin] + text[stop..];
    }
}
