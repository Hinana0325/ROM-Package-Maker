namespace RomPackageMaker.Application;

/// <summary>预装应用删除风险等级。</summary>
public enum DebloatRisk
{
    /// <summary>未知（不在知识库中）。</summary>
    Unknown = 0,
    /// <summary>可安全删除（普通应用，删除不影响系统）。</summary>
    Safe = 1,
    /// <summary>需谨慎（部分功能依赖，视个人使用情况）。</summary>
    Caution = 2,
    /// <summary>禁删（系统核心，删除会 bootloop 或核心功能崩溃）。</summary>
    Keep = 3,
}

/// <summary>
/// 预装应用删除安全知识库：按包名标注风险等级与原因。
/// 数据来源：社区通用 debloat 清单（AOSP / GMS / 常见 OEM 预装）整理，
/// 覆盖常见条目；未知包名返回 Unknown，由 UI 按无标注处理。
/// </summary>
public static class DebloatSafetyService
{
    private sealed record Entry(DebloatRisk Risk, string Reason);

    private static readonly Dictionary<string, Entry> Db = Build();

    private static Dictionary<string, Entry> Build()
    {
        var db = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        // ---- AOSP 核心（禁删）----
        Keep(db, "com.android.systemui", "系统 UI：状态栏 / 导航栏 / 锁屏，删除直接 bootloop");
        Keep(db, "com.android.settings", "系统设置");
        Keep(db, "com.android.phone", "电话与移动网络服务");
        Keep(db, "com.android.server.telecom", "通话服务（拨号依赖）");
        Keep(db, "com.android.incallui", "通话界面");
        Keep(db, "com.android.providers.contacts", "联系人数据存储");
        Keep(db, "com.android.providers.media", "媒体文件存储（相册 / 文件管理依赖）");
        Keep(db, "com.android.providers.downloads", "下载服务（应用商店下载依赖）");
        Keep(db, "com.android.providers.settings", "系统设置数据存储");
        Keep(db, "com.android.providers.telephony", "短信 / 彩信数据存储");
        Keep(db, "com.android.providers.calendar", "日历数据存储");
        Keep(db, "com.android.providers.userdictionary", "用户词典");
        Keep(db, "com.android.shell", "ADB shell 支持");
        Keep(db, "com.android.se", "安全元件服务（NFC 支付 / 门禁卡依赖）");
        Keep(db, "com.android.keychain", "系统证书存储");
        Keep(db, "com.android.inputdevices", "输入设备支持");
        Keep(db, "com.android.location.fused", "融合定位服务（地图 / 打车 / 天气依赖）");
        Keep(db, "com.android.webview", "应用内网页浏览器内核（大量应用依赖）");
        Keep(db, "com.google.android.webview", "WebView 内核（GMS 版，同上）");
        Keep(db, "com.android.wallpaperbackup", "壁纸备份与还原");
        Keep(db, "com.android.networkstack", "网络基础组件");
        Keep(db, "com.android.networkstack.tethering", "热点与网络共享");
        Keep(db, "com.android.packageinstaller", "应用安装器（装 / 卸载依赖）");
        Keep(db, "com.android.proxyhandler", "系统代理（部分网络功能依赖）");
        Keep(db, "com.android.vpndialogs", "VPN 连接对话框");
        Keep(db, "com.android.cellbroadcastreceiver", "小区广播（运营商紧急警报）");
        Keep(db, "com.android.emergency", "紧急信息（紧急联系 / 医疗信息）");
        Keep(db, "com.android.mtp", "电脑文件传输（MTP）");

        // ---- Google 服务框架（禁删 / 谨慎）----
        Keep(db, "com.google.android.gms", "Google Play 服务：应用商店 / 推送 / 地图 SDK 都依赖");
        Keep(db, "com.google.android.gsf", "Google 服务框架（GCM 推送基础）");
        Keep(db, "com.google.android.gsf.login", "Google 账号登录服务");
        Caution(db, "com.google.android.googlequicksearchbox", "搜索与助手（桌面搜索栏 / 语音唤醒依赖）");
        Caution(db, "com.google.android.tts", "文字转语音（导航播报 / 无障碍读屏依赖）");
        Caution(db, "com.google.android.syncadapters.calendar", "Google 日历同步");
        Caution(db, "com.google.android.syncadapters.contacts", "Google 联系人同步");
        Caution(db, "com.google.android.backuptransport", "Google 云备份通道");
        Caution(db, "com.google.android.configupdater", "Google 配置更新（部分网络功能依赖）");
        Caution(db, "com.google.android.partnersetup", "Play 商店初始化依赖");
        Caution(db, "com.google.android.onetimeinitializer", "Google 服务首次初始化");
        Caution(db, "com.google.android.setupwizard", "开机设置向导（已开机可删，恢复出厂前删会卡向导）");
        Caution(db, "com.google.android.apps.restore", "换机迁移（Android Switch）");
        Caution(db, "com.google.android.marvin.talkback", "TalkBack 无障碍读屏");
        Caution(db, "com.google.android.gsa", "Google 搜索服务（同 quicksearchbox）");
        Caution(db, "com.google.android.apps.wellbeing", "数字健康（屏幕时间 / 专注模式）");

        // ---- Google 普通应用（可删）----
        Safe(db, "com.google.android.youtube", "YouTube");
        Safe(db, "com.google.android.apps.youtube.music", "YouTube Music");
        Safe(db, "com.google.android.gm", "Gmail");
        Safe(db, "com.google.android.apps.photos", "Google 相册（注意保留其它看图应用）");
        Safe(db, "com.google.android.videos", "Google 影视");
        Safe(db, "com.google.android.music", "Play 音乐");
        Safe(db, "com.google.android.apps.maps", "Google 地图");
        Safe(db, "com.google.android.apps.docs", "Google 文档");
        Safe(db, "com.google.android.apps.docs.drive", "Google 云端硬盘");
        Safe(db, "com.google.android.tachyon", "Google Duo / Meet 视频通话");
        Safe(db, "com.google.android.apps.tachyon", "Google Duo / Meet 视频通话");
        Safe(db, "com.google.android.apps.googleassistant", "Google 助手");
        Safe(db, "com.google.android.feedback", "Google 反馈");
        Safe(db, "com.google.android.printservice.recommendation", "打印服务推荐");
        Safe(db, "com.google.android.apps.subscriptions.red", "Google One 订阅");
        Safe(db, "com.google.android.gms.location.history", "位置历史记录");
        Safe(db, "com.google.android.apps.giant", "Google Apps 巨型占位包");
        Safe(db, "com.google.android.apps.chromesync", "Chrome 同步组件");

        // ---- AOSP 普通应用（可删）----
        Safe(db, "com.android.browser", "AOSP 浏览器");
        Safe(db, "com.android.email", "AOSP 电子邮件");
        Safe(db, "com.android.exchange", "Exchange 邮件服务");
        Safe(db, "com.android.music", "AOSP 音乐");
        Safe(db, "com.android.soundrecorder", "录音机");
        Safe(db, "com.android.calculator2", "计算器");
        Safe(db, "com.android.fmradio", "收音机");
        Safe(db, "com.android.dreams.basic", "屏幕保护程序（基础屏保）");
        Safe(db, "com.android.terminal", "本地终端");
        Safe(db, "com.android.bookmarkprovider", "书签提供器");
        Safe(db, "com.android.wallpaper.halloween", "万圣节壁纸");
        Caution(db, "com.android.wallpaper.live", "动态壁纸选择器（用动态壁纸需保留）");
        Caution(db, "com.android.printspooler", "打印服务（需打印保留）");
        Caution(db, "com.android.stk", "SIM 卡工具箱（运营商 SIM 菜单）");
        Caution(db, "com.android.nfc", "NFC 服务（碰一碰 / 门禁卡依赖）");
        Caution(db, "com.android.calendar", "AOSP 日历（无其它日历应用时保留）");
        Caution(db, "com.android.mms", "AOSP 短信（无其它短信应用时保留）");
        Caution(db, "com.android.deskclock", "AOSP 时钟（闹钟）");
        Caution(db, "com.android.gallery2", "AOSP 图库（无其它相册应用时保留）");

        // ---- 小米 MIUI / HyperOS ----
        Safe(db, "com.miui.player", "小米音乐");
        Safe(db, "com.miui.video", "小米视频");
        Safe(db, "com.miui.yellowpage", "黄页");
        Safe(db, "com.miui.compass", "指南针");
        Safe(db, "com.miui.notes", "小米便签");
        Safe(db, "com.miui.bugreport", "用户反馈");
        Safe(db, "com.miui.miservice", "服务与反馈");
        Safe(db, "com.miui.weather2", "天气");
        Safe(db, "com.miui.cleaner", "垃圾清理");
        Safe(db, "com.miui.screenrecorder", "屏幕录制（需要时保留）");
        Caution(db, "com.miui.browser", "小米浏览器（注意保留其它浏览器）");
        Caution(db, "com.xiaomi.mipicks", "小米应用商店（系统应用更新通道）");
        Caution(db, "com.miui.cloudservice", "小米云服务（查找手机 / 云同步依赖）");
        Caution(db, "com.miui.securitycenter", "手机管家（权限管理 / 流量统计在这）");
        Caution(db, "com.miui.home", "MIUI 桌面（注意保留其它桌面）");
        Caution(db, "com.xiaomi.finddevice", "查找设备");
        Caution(db, "com.mipay.wallet", "小米钱包");
        Keep(db, "com.miui.securitycore", "手机管家核心（删除会 bootloop）");
        Keep(db, "com.miui.system", "MIUI 系统组件");
        Keep(db, "com.xiaomi.xmsf", "小米服务框架（账号 / 推送依赖）");

        // ---- 三星 One UI ----
        Safe(db, "com.sec.android.app.samsungapps", "三星应用商店");
        Safe(db, "com.samsung.android.game.gamehome", "游戏中心");
        Safe(db, "com.samsung.android.game.gos", "游戏优化服务");
        Safe(db, "com.sec.android.daemonapp", "三星天气");
        Safe(db, "com.samsung.android.app.sbrowseredge", "三星浏览器扩展");
        Caution(db, "com.sec.android.app.sbrowser", "三星浏览器（注意保留其它浏览器）");
        Caution(db, "com.samsung.android.messaging", "三星信息（无其它短信应用时保留）");
        Caution(db, "com.samsung.android.smartsuggestions", "智能建议");
        Keep(db, "com.sec.android.desktopsystemui", "桌面模式系统 UI");
        Keep(db, "com.samsung.android.lool", "设备维护核心");

        // ---- 华为 / 荣耀 ----
        Safe(db, "com.huawei.himovieoverseas", "华为视频");
        Safe(db, "com.huawei.himusicoverseas", "华为音乐");
        Safe(db, "com.huawei.hwread.alive", "华为阅读");
        Caution(db, "com.huawei.browser", "华为浏览器（注意保留其它浏览器）");
        Caution(db, "com.huawei.appmarket", "华为应用市场（系统应用更新通道）");
        Caution(db, "com.huawei.hwid", "华为账号服务");
        Keep(db, "com.huawei.systemmanager", "系统管家核心");

        return db;
    }

    private static void Keep(Dictionary<string, Entry> db, string pkg, string reason) =>
        db[pkg] = new Entry(DebloatRisk.Keep, reason);

    private static void Caution(Dictionary<string, Entry> db, string pkg, string reason) =>
        db[pkg] = new Entry(DebloatRisk.Caution, reason);

    private static void Safe(Dictionary<string, Entry> db, string pkg, string reason) =>
        db[pkg] = new Entry(DebloatRisk.Safe, reason);

    /// <summary>查询包名的删除风险等级；未知包返回 Unknown。</summary>
    public static DebloatRisk GetRisk(string? packageName) =>
        packageName is not null && Db.TryGetValue(packageName, out var e) ? e.Risk : DebloatRisk.Unknown;

    /// <summary>查询包名的风险说明；未知包返回 null。</summary>
    public static string? GetReason(string? packageName) =>
        packageName is not null && Db.TryGetValue(packageName, out var e) ? e.Reason : null;

    /// <summary>风险等级显示文本。</summary>
    public static string RiskText(DebloatRisk risk) => risk switch
    {
        DebloatRisk.Safe => "可删",
        DebloatRisk.Caution => "谨慎",
        DebloatRisk.Keep => "禁删",
        _ => "",
    };
}
