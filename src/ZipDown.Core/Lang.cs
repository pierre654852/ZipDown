using System.Globalization;

namespace ZipDown.Core
{
    /// <summary>介面與紀錄的三語字串：zh-TW / zh-CN / en。</summary>
    public static class L
    {
        public static readonly string[] Languages = { "zh-TW", "zh-CN", "en" };

        public static string Current { get; private set; } = "zh-TW";
        static int _idx;

        public static event Action Changed;

        /// <summary>空白 = 跟系統。</summary>
        public static void Set(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) lang = FromSystem();
            int i = Array.IndexOf(Languages, lang);
            if (i < 0) i = 2;
            _idx = i;
            Current = Languages[i];
            Changed?.Invoke();
        }

        static string FromSystem()
        {
            var c = CultureInfo.CurrentUICulture;
            var n = c.Name;
            if (n.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                if (n.Contains("Hant") || n.EndsWith("TW") || n.EndsWith("HK") || n.EndsWith("MO")) return "zh-TW";
                return "zh-CN";
            }
            return "en";
        }

        public static string T(string key)
        {
            if (S.TryGetValue(key, out var a)) return a[_idx];
            return key;
        }

        public static string F(string key, params object[] args) => string.Format(T(key), args);

        static readonly Dictionary<string, string[]> S = new Dictionary<string, string[]>
        {
            // ---- 程式 ----
            ["app.title"] = new[] { "褲鍊解壓 ZipDown", "裤链解压 ZipDown", "ZipDown" },

            // ---- 工具列 ----
            ["ui.engine"] = new[] { "引擎", "引擎", "Engine" },
            ["eng.auto"] = new[] { "自動", "自动", "Auto" },
            ["ui.addFiles"] = new[] { "加入檔案", "添加文件", "Add files" },
            ["ui.addFolder"] = new[] { "加入資料夾", "添加文件夹", "Add folder" },
            ["ui.remove"] = new[] { "移除選取", "移除选中", "Remove selected" },
            ["ui.clear"] = new[] { "清空", "清空", "Clear" },
            ["ui.undo"] = new[] { "復原改名", "还原改名", "Undo renames" },
            ["ui.start"] = new[] { "開始", "开始", "Start" },
            ["ui.stop"] = new[] { "停止", "停止", "Stop" },
            ["ui.forceStop"] = new[] { "強制停止", "强制停止", "Force stop" },
            ["ui.settings"] = new[] { "設定", "设置", "Settings" },
            ["ui.about"] = new[] { "關於", "关于", "About" },
            ["ui.dropHint"] = new[] { "把檔案或資料夾拖進來，或按「加入檔案／加入資料夾」", "把文件或文件夹拖进来，或点「添加文件／添加文件夹」", "Drop files or folders here, or use Add files / Add folder" },
            ["ui.scanning"] = new[] { "掃描中：{0}", "扫描中：{0}", "Scanning: {0}" },
            ["ui.scanned"] = new[] { "加入 {0} 個壓縮檔", "添加 {0} 个压缩文件", "Added {0} archive(s)" },

            // ---- 表格欄位 ----
            ["col.original"] = new[] { "原檔名", "原文件名", "Original name" },
            ["col.renamed"] = new[] { "改名後", "改名后", "Renamed to" },
            ["col.size"] = new[] { "大小", "大小", "Size" },
            ["col.password"] = new[] { "密碼", "密码", "Password" },
            ["col.status"] = new[] { "狀態", "状态", "Status" },
            ["col.elapsed"] = new[] { "耗時", "耗时", "Time" },
            ["col.folder"] = new[] { "位置", "位置", "Location" },

            // ---- 狀態 ----
            ["st.Pending"] = new[] { "等待中", "等待中", "Pending" },
            ["st.AlreadyDone"] = new[] { "已解過", "已解过", "Already done" },
            ["st.Testing"] = new[] { "測試密碼…", "测试密码…", "Testing passwords…" },
            ["st.Extracting"] = new[] { "解壓中…", "解压中…", "Extracting…" },
            ["st.Done"] = new[] { "完成", "完成", "Done" },
            ["st.Skipped"] = new[] { "跳過", "跳过", "Skipped" },
            ["st.Failed"] = new[] { "失敗", "失败", "Failed" },
            ["st.Stopped"] = new[] { "已停止", "已停止", "Stopped" },

            // ---- 紀錄 ----
            ["log.engine"] = new[] { "解壓工具：{0}（{1}）", "解压工具：{0}（{1}）", "Engine: {0} ({1})" },
            ["log.noEngine"] = new[] { "找不到解壓工具。請安裝 Bandizip 或 WinRAR，或在「設定」裡指定路徑。", "找不到解压工具。请安装 Bandizip 或 WinRAR，或在「设置」里指定路径。", "No extraction tool found. Install Bandizip or WinRAR, or set its path in Settings." },
            ["log.found"] = new[] { "共 {0} 個壓縮檔", "共 {0} 个压缩文件", "{0} archive(s) in total" },
            ["log.rename"] = new[] { "[改名] {0} → {1}", "[改名] {0} → {1}", "[Renamed] {0} → {1}" },
            ["log.renameSkip"] = new[] { "[改名跳過，{0} 已存在] {1}", "[改名跳过，{0} 已存在] {1}", "[Rename skipped, {0} exists] {1}" },
            ["log.renameFail"] = new[] { "[改名失敗] {0}：{1}", "[改名失败] {0}：{1}", "[Rename failed] {0}: {1}" },
            ["log.processing"] = new[] { "{0} 處理中：{1}（{2}）", "{0} 处理中：{1}（{2}）", "{0} Processing: {1} ({2})" },
            ["log.inner"] = new[] { "[內層]", "[内层]", "[Inner]" },
            ["log.skipExists"] = new[] { "[已有同名資料夾，當作解過，跳過] {0}", "[已有同名文件夹，当作解过，跳过] {0}", "[Output folder exists, skipped] {0}" },
            ["log.skipDone"] = new[] { "[清單上已解過，跳過] {0}", "[清单上已解过，跳过] {0}", "[Already in done list, skipped] {0}" },
            ["log.attempt"] = new[] { "    測試 {0}  密碼={1}  結束代碼={2}", "    测试 {0}  密码={1}  结束代码={2}", "    Test {0}  password={1}  exit code={2}" },
            ["log.noPw"] = new[] { "(無)", "(无)", "(none)" },
            ["log.badPw"] = new[] { "[密碼都不對或檔案損壞] {0}", "[密码都不对或文件损坏] {0}", "[No password worked, or file is damaged] {0}" },
            ["log.extractFail"] = new[] { "[解壓失敗，結束代碼={0}] {1}", "[解压失败，结束代码={0}] {1}", "[Extraction failed, exit code={0}] {1}" },
            ["log.okNoPw"] = new[] { "[解壓完成] {0}（耗時 {1}）", "[解压完成] {0}（耗时 {1}）", "[Done] {0} (took {1})" },
            ["log.okPw"] = new[] { "[解壓完成，密碼={0}] {1}（耗時 {2}）", "[解压完成，密码={0}] {1}（耗时 {2}）", "[Done, password={0}] {1} (took {2})" },
            ["log.pwAdded"] = new[] { "    [新密碼已加入密碼本] {0}", "    [新密码已加入密码本] {0}", "    [New password added to list] {0}" },
            ["log.moved"] = new[] { "    [已搬到第一層] {0}", "    [已移到第一层] {0}", "    [Moved to top folder] {0}" },
            ["log.moveConflict"] = new[] { "    [撞名，改成] {0}", "    [重名，改成] {0}", "    [Name conflict, renamed to] {0}" },
            ["log.moveFail"] = new[] { "    [搬移失敗，保持原樣] {0}：{1}", "    [移动失败，保持原样] {0}：{1}", "    [Move failed, left as is] {0}: {1}" },
            ["log.summary"] = new[] { "改名：{0}　解壓成功：{1}　跳過：{2}　失敗：{3}", "改名：{0}　解压成功：{1}　跳过：{2}　失败：{3}", "Renamed: {0}  Extracted: {1}  Skipped: {2}  Failed: {3}" },
            ["log.logFile"] = new[] { "紀錄檔：{0}", "日志文件：{0}", "Log file: {0}" },
            ["log.stopAfter"] = new[] { "收到停止：目前這個檔做完就停（再按一次＝強制停止）", "收到停止：当前这个文件做完就停（再按一次＝强制停止）", "Stopping after the current file (press again to force stop)" },
            ["log.forceStop"] = new[] { "強制停止", "强制停止", "Force stopped" },
            ["log.cleaned"] = new[] { "    [已清掉解一半的資料夾] {0}", "    [已清掉解到一半的文件夹] {0}", "    [Removed half-extracted folder] {0}" },
            ["log.stopped"] = new[] { "已停止", "已停止", "Stopped" },
            ["log.error"] = new[] { "[錯誤] {0}：{1}", "[错误] {0}：{1}", "[Error] {0}: {1}" },
            ["log.undo"] = new[] { "[復原改名] {0} → {1}", "[还原改名] {0} → {1}", "[Undo rename] {0} → {1}" },
            ["log.undoSkip"] = new[] { "[復原跳過] {0}", "[还原跳过] {0}", "[Undo skipped] {0}" },
            ["log.undoSummary"] = new[] { "復原改名：成功 {0}，跳過 {1}", "还原改名：成功 {0}，跳过 {1}", "Undo renames: {0} restored, {1} skipped" },
            ["log.undoNone"] = new[] { "沒有可以復原的改名紀錄", "没有可以还原的改名记录", "Nothing to undo" },

            // ---- 時間 ----
            ["time.h"] = new[] { "{0} 小時 {1} 分", "{0} 小时 {1} 分", "{0} h {1} min" },
            ["time.m"] = new[] { "{0} 分 {1} 秒", "{0} 分 {1} 秒", "{0} min {1} s" },
            ["time.s"] = new[] { "{0} 秒", "{0} 秒", "{0} s" },

            // ---- 設定視窗 ----
            ["set.title"] = new[] { "設定", "设置", "Settings" },
            ["set.engine"] = new[] { "解壓引擎", "解压引擎", "Engine" },
            ["set.bzPath"] = new[] { "Bandizip（bz.exe）路徑，空白＝自動尋找", "Bandizip（bz.exe）路径，留空＝自动查找", "Bandizip (bz.exe) path, blank = auto" },
            ["set.wrPath"] = new[] { "WinRAR（WinRAR.exe）路徑，空白＝自動尋找", "WinRAR（WinRAR.exe）路径，留空＝自动查找", "WinRAR (WinRAR.exe) path, blank = auto" },
            ["set.browse"] = new[] { "瀏覽…", "浏览…", "Browse…" },
            ["set.passwords"] = new[] { "密碼本（一行一個）", "密码本（一行一个）", "Passwords (one per line)" },
            ["set.maxDepth"] = new[] { "最多解幾層", "最多解几层", "Max nesting depth" },
            ["set.flatten"] = new[] { "內層解出的內容搬到第一層資料夾", "内层解出的内容移到第一层文件夹", "Move inner contents to the top output folder" },
            ["set.junk"] = new[] { "檔名垃圾字（用 | 分隔）", "文件名垃圾字（用 | 分隔）", "Junk characters in names (separate with |)" },
            ["set.language"] = new[] { "語言", "语言", "Language" },
            ["lang.system"] = new[] { "跟隨系統", "跟随系统", "System default" },
            ["set.ok"] = new[] { "儲存", "保存", "Save" },
            ["set.cancel"] = new[] { "取消", "取消", "Cancel" },

            // ---- 關於 ----
            ["about.title"] = new[] { "關於", "关于", "About" },
            ["about.version"] = new[] { "版本 {0}", "版本 {0}", "Version {0}" },
            ["about.credit"] = new[] { "功能構想參考 smart-unzip-workbench", "功能构想参考 smart-unzip-workbench", "Feature ideas inspired by smart-unzip-workbench" },
            ["about.changes"] = new[] { "更新內容", "更新内容", "Changelog" },
            ["about.close"] = new[] { "關閉", "关闭", "Close" },
            ["about.changelog"] = new[]
            {
                "v0.1.0（2026-10-28）\n- 初版，由 unpack V7 批次檔改寫成視窗程式\n- 偽裝 MP4／圖片／去掉偽裝副檔名、去垃圾字、分卷（含 .z01）\n- 多層解壓、內層搬到第一層、撞名加 (2)、完成清單\n- 從檔名／資料夾名抓密碼，成功次數多的密碼先試\n- 復原改名、兩段式停止、三語介面",
                "v0.1.0（2026-10-28）\n- 初版，由 unpack V7 批处理改写成窗口程序\n- 伪装 MP4／图片／去掉伪装扩展名、去垃圾字、分卷（含 .z01）\n- 多层解压、内层移到第一层、重名加 (2)、完成清单\n- 从文件名／文件夹名抓密码，成功次数多的密码先试\n- 还原改名、两段式停止、三语界面",
                "v0.1.0 (2026-10-28)\n- First release, rewritten from the unpack V7 batch script\n- Disguised MP4 / image detection, fake extension removal, junk characters, split volumes (incl. .z01)\n- Nested extraction, move inner contents to top folder, (2) on name conflict, done list\n- Passwords from file / folder names; most successful passwords tried first\n- Undo renames, two-stage stop, three UI languages",
            },
        };
    }

    public static class Fmt
    {
        public static string Time(TimeSpan t)
        {
            if (t.TotalHours >= 1) return L.F("time.h", (int)Math.Floor(t.TotalHours), t.Minutes);
            if (t.TotalMinutes >= 1) return L.F("time.m", t.Minutes, t.Seconds);
            return L.F("time.s", t.Seconds);
        }

        public static string Size(long b)
        {
            const double GB = 1024d * 1024 * 1024, MB = 1024d * 1024, KB = 1024d;
            if (b >= GB) return (b / GB).ToString("N1", CultureInfo.InvariantCulture) + " GB";
            if (b >= MB) return (b / MB).ToString("N0", CultureInfo.InvariantCulture) + " MB";
            return Math.Max(1, (long)Math.Ceiling(b / KB)).ToString(CultureInfo.InvariantCulture) + " KB";
        }
    }

    public static class AppInfo
    {
        public const string Version = "0.1.0";
        public const string RepoUrl = "https://github.com/pierre654852/ZipDown";
    }
}
