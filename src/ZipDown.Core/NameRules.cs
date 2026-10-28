using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ZipDown.Core
{
    /// <summary>分析檔名：去垃圾字、去偽裝副檔名、抓檔名裡的密碼、判斷分卷。</summary>
    public sealed class NameInfo
    {
        public string Original;
        /// <summary>去掉垃圾字後的名字（還沒去偽裝）。</summary>
        public string NoJunk;
        /// <summary>整理後應該叫的名字（到壓縮副檔名為止）。沒有壓縮副檔名時等於 NoJunk。</summary>
        public string Clean;
        public bool HasToken;
        /// <summary>壓縮副檔名後面還有東西（偽裝副檔名或密碼）。</summary>
        public bool HasRemainder;
        public string TokenExt = "";
        public string Password;
        public int VolumeNo;          // 0 = 不是編號分卷
        public bool IsZipOrRarPart;   // .z01 / .r00 這類：主卷是 .zip / .rar
        public string FamilyKey = "";
        public string OutName = "";
        public bool IsSecondary => VolumeNo > 1 || IsZipOrRarPart;
        public bool IsVolume => VolumeNo > 0 || IsZipOrRarPart;
    }

    public static class NameRules
    {
        static readonly Regex TokenRx = new Regex(
            @"\.part(?<p>\d+)\.rar|\.(?<b>7z|zip|rar)\.(?<n>\d{3})|\.z(?<z>\d{2})|\.r(?<r>\d{2})|\.(?<e>7z|zip|rar)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static readonly Regex DisguiseRx = new Regex(
            @"^\.(jpe?g|png|gif|webp|bmp|mp4|mkv|avi|mov|wmv|flv|webm|mp3|txt|pdf|docx?|exe|apk|dat|bin)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static readonly Regex KeywordRx = new Regex(
            @"(?:解压密码|解壓密碼|解压码|解壓碼|解压|解壓|密码|密碼|password|passwd|pwd|pass|pw)\s*[:：=]?\s*(?<pw>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static readonly char[] TrimChars = { ' ', '\t', '_', '-', '.', '[', ']', '(', ')', '{', '}', '【', '】', '（', '）' };

        public static string RemoveJunk(string name, IEnumerable<string> junk)
        {
            if (junk == null) return name;
            foreach (var j in junk)
                if (!string.IsNullOrEmpty(j)) name = name.Replace(j, "");
            return name;
        }

        /// <summary>名稱裡有「密碼／解壓碼／pw」等關鍵字時，取出後面的密碼（資料夾名稱用）。</summary>
        public static string KeywordPassword(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = KeywordRx.Match(text);
            if (!m.Success) return null;
            var pw = m.Groups["pw"].Value.Trim(' ', '\t', '{', '}', '[', ']', '【', '】');
            return pw.Length > 0 ? pw : null;
        }

        public static NameInfo Analyze(string fileName, IEnumerable<string> junk)
        {
            var info = new NameInfo { Original = fileName };
            string n = RemoveJunk(fileName, junk);
            info.NoJunk = n;
            info.Clean = n;

            Match last = null;
            foreach (Match m in TokenRx.Matches(n)) last = m;
            if (last == null) return info;

            info.HasToken = true;
            string prefix = n.Substring(0, last.Index);
            string rest = n.Substring(last.Index + last.Length);
            info.Clean = n.Substring(0, last.Index + last.Length);
            info.OutName = prefix;

            if (last.Groups["p"].Success) { info.VolumeNo = int.Parse(last.Groups["p"].Value); info.TokenExt = ".rar"; }
            else if (last.Groups["n"].Success) { info.VolumeNo = int.Parse(last.Groups["n"].Value); info.TokenExt = "." + last.Groups["b"].Value.ToLowerInvariant(); }
            else if (last.Groups["z"].Success) { info.IsZipOrRarPart = true; info.TokenExt = ".zip"; }
            else if (last.Groups["r"].Success) { info.IsZipOrRarPart = true; info.TokenExt = ".rar"; }
            else info.TokenExt = "." + last.Groups["e"].Value.ToLowerInvariant();
            info.FamilyKey = prefix.ToLowerInvariant() + "|" + info.TokenExt;

            if (rest.Length > 0)
            {
                info.HasRemainder = true;
                var d = DisguiseRx.Match(rest);
                if (d.Success) rest = rest.Substring(d.Length);
                var pw = KeywordPassword(rest);
                if (pw == null)
                {
                    var t = rest.Trim(TrimChars);
                    if (t.Length > 0) pw = t;
                }
                info.Password = pw;
            }
            return info;
        }

        /// <summary>沒有壓縮副檔名的檔（.mp4 / .jpg / 無副檔名）改成正確格式後的名字。</summary>
        public static string WithArchiveExt(string noJunkName, ArcKind kind)
        {
            string ext = System.IO.Path.GetExtension(noJunkName);
            // 只有像 .mp4 / .jpg 這種英數副檔名（至少一個字母）才替換；「26.10.05」這種數字結尾視為沒有副檔名，直接補上
            bool realExt = IsRealExt(ext);
            string stem = realExt ? noJunkName.Substring(0, noJunkName.Length - ext.Length) : noJunkName;
            return stem + Sniffer.ExtOf(kind);
        }

        /// <summary>像 .mp4、.jpg、.txt 這種副檔名：2～5 個英數字，至少一個字母。</summary>
        public static bool IsRealExt(string ext) =>
            ext != null && ext.Length >= 2 && ext.Length <= 6 && ext[0] == '.'
            && ext.Skip(1).All(c => c < 128 && char.IsLetterOrDigit(c)) && ext.Skip(1).Any(char.IsLetter);

        /// <summary>解壓輸出的資料夾名稱。</summary>
        public static string OutFolderName(string archiveFileName)
        {
            var i = Analyze(archiveFileName, null);
            if (i.HasToken && i.OutName.Length > 0) return i.OutName;
            var ext = System.IO.Path.GetExtension(archiveFileName);
            var stem = archiveFileName.Substring(0, archiveFileName.Length - ext.Length);
            return stem.Length > 0 ? stem : archiveFileName + "_out";
        }
    }
}
