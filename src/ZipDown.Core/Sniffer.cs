using System;
using System.IO;
using System.Text;

namespace ZipDown.Core
{
    public enum ArcKind { None, Zip, Rar, SevenZip }

    /// <summary>
    /// 讀檔頭／檔尾判斷檔案「實際上」是什麼，不看副檔名。
    /// </summary>
    public static class Sniffer
    {
        static readonly byte[] SigZip = { 0x50, 0x4B, 0x03, 0x04 };
        static readonly byte[] SigZipEnd = { 0x50, 0x4B, 0x05, 0x06 };
        static readonly byte[] SigRar = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 };
        static readonly byte[] Sig7z = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

        public static string ExtOf(ArcKind k)
        {
            switch (k)
            {
                case ArcKind.Zip: return ".zip";
                case ArcKind.Rar: return ".rar";
                case ArcKind.SevenZip: return ".7z";
                default: return "";
            }
        }

        public static ArcKind KindFromExt(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".zip": return ArcKind.Zip;
                case ".rar": return ArcKind.Rar;
                case ".7z": return ArcKind.SevenZip;
                default: return ArcKind.None;
            }
        }

        static byte[] ReadAt(FileStream fs, long pos, int n)
        {
            if (pos < 0) pos = 0;
            fs.Position = pos;
            var b = new byte[n];
            int total = 0;
            while (total < n)
            {
                int r = fs.Read(b, total, n - total);
                if (r <= 0) break;
                total += r;
            }
            if (total < n) Array.Resize(ref b, total);
            return b;
        }

        static int IndexOf(byte[] hay, byte[] needle, int start = 0)
        {
            for (int i = start; i <= hay.Length - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        static bool StartsWith(byte[] hay, byte[] needle)
        {
            if (hay.Length < needle.Length) return false;
            for (int i = 0; i < needle.Length; i++) if (hay[i] != needle[i]) return false;
            return true;
        }

        static FileStream Open(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        /// <summary>檔頭是不是壓縮檔（給沒副檔名的檔用）。</summary>
        public static ArcKind Head(string path)
        {
            try
            {
                using (var fs = Open(path))
                {
                    var h = ReadAt(fs, 0, 8);
                    if (StartsWith(h, Sig7z)) return ArcKind.SevenZip;
                    if (StartsWith(h, SigRar)) return ArcKind.Rar;
                    if (StartsWith(h, SigZip)) return ArcKind.Zip;
                }
            }
            catch { }
            return ArcKind.None;
        }

        static bool HasZipEnd(FileStream fs, long from)
        {
            long n = Math.Min(fs.Length - from, 262144);
            if (n <= 0) return false;
            var tail = ReadAt(fs, fs.Length - n, (int)n);
            return IndexOf(tail, SigZipEnd) >= 0;
        }

        /// <summary>圖片／文字等檔案後面接了 ZIP（檔尾有 ZIP 結尾標記）。</summary>
        public static bool ZipTail(string path)
        {
            try { using (var fs = Open(path)) return HasZipEnd(fs, 0); }
            catch { return false; }
        }

        public enum Mp4Result { NotMp4, Video, Unknown, Archive }

        /// <summary>
        /// 依序走過 MP4 頂層 box 找出影片真正結束的位置，再看後面接的是什麼。
        /// 檔尾常有偽造的 7z／RAR 標頭垃圾資料，所以不能只看檔尾。
        /// </summary>
        public static Mp4Result Mp4(string path, out ArcKind kind)
        {
            kind = ArcKind.None;
            try
            {
                using (var fs = Open(path))
                {
                    long len = fs.Length;
                    var h = ReadAt(fs, 0, 12);
                    if (h.Length < 12 || Encoding.ASCII.GetString(h, 4, 4) != "ftyp") return Mp4Result.NotMp4;
                    long pos = 0;
                    while (pos + 8 <= len)
                    {
                        var b = ReadAt(fs, pos, 16);
                        if (b.Length < 8) break;
                        long size = ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3];
                        bool typeOk = true;
                        for (int i = 4; i < 8; i++)
                        {
                            char c = (char)b[i];
                            if (!(char.IsLetterOrDigit(c) && c < 128) && c != ' ') { typeOk = false; break; }
                        }
                        if (!typeOk) break;
                        if (size == 1)
                        {
                            if (b.Length < 16) break;
                            size = 0;
                            for (int k = 8; k < 16; k++) size = (size << 8) | b[k];
                        }
                        else if (size == 0) size = len - pos;
                        if (size < 8 || pos + size > len) break;
                        pos += size;
                    }
                    if (pos >= len) return Mp4Result.Video;

                    var win = ReadAt(fs, pos, 4096);
                    int best = int.MaxValue;
                    int iz = IndexOf(win, SigZip), ir = IndexOf(win, SigRar), i7 = IndexOf(win, Sig7z);
                    if (iz >= 0 && iz < best) { best = iz; kind = ArcKind.Zip; }
                    if (ir >= 0 && ir < best) { best = ir; kind = ArcKind.Rar; }
                    if (i7 >= 0 && i7 < best) { best = i7; kind = ArcKind.SevenZip; }
                    if (kind == ArcKind.None) return Mp4Result.Unknown;
                    if (kind == ArcKind.Zip && !HasZipEnd(fs, pos)) { kind = ArcKind.None; return Mp4Result.Unknown; }
                    return Mp4Result.Archive;
                }
            }
            catch { return Mp4Result.Unknown; }
        }

        /// <summary>任何檔案：用內容判斷是不是壓縮檔（檔頭、MP4 後接、檔尾 ZIP）。</summary>
        public static ArcKind Detect(string path)
        {
            var k = Head(path);
            if (k != ArcKind.None) return k;
            if (Mp4(path, out k) == Mp4Result.Archive) return k;
            if (ZipTail(path)) return ArcKind.Zip;
            return ArcKind.None;
        }
    }
}
