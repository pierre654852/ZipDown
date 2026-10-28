using System.Text;
using ZipDown.Core;

namespace ZipDown.Core.Tests
{
    /// <summary>測試用的暫存資料夾，結束自動刪掉。</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zipdown_test_" + Guid.NewGuid().ToString("N"));
        public TempDir() { Directory.CreateDirectory(Path); }
        public string P(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
        public string Write(string rel, byte[] data)
        {
            var p = P(rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
            File.WriteAllBytes(p, data);
            return p;
        }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    /// <summary>做假檔案的工具：只要檔頭／檔尾簽名對就好，不是真的壓縮檔。</summary>
    public static class FakeFiles
    {
        public static readonly byte[] Zip = { 0x50, 0x4B, 0x03, 0x04 };
        public static readonly byte[] ZipEnd = { 0x50, 0x4B, 0x05, 0x06 };
        public static readonly byte[] Rar = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };
        public static readonly byte[] SevenZ = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 };

        public static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
        public static byte[] Pad(int n, byte v = 0x11) => Enumerable.Repeat(v, n).ToArray();

        public static byte[] Box(string type, int payload)
        {
            var size = 8 + payload;
            var b = new byte[size];
            b[0] = (byte)(size >> 24); b[1] = (byte)(size >> 16); b[2] = (byte)(size >> 8); b[3] = (byte)size;
            Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
            for (int i = 8; i < size; i++) b[i] = 0x22;
            return b;
        }

        /// <summary>只有 ftyp + moov + mdat 的「影片」。</summary>
        public static byte[] Mp4() => Cat(Box("ftyp", 16), Box("moov", 100), Box("mdat", 1000));

        public static byte[] FakeZip() => Cat(Zip, Pad(200), ZipEnd, Pad(18, 0));
        public static byte[] Fake7z() => Cat(SevenZ, Pad(200));
    }

    /// <summary>
    /// 假引擎：密碼對（或沒加密）才回 0；解壓時照 Contents 寫檔。
    /// </summary>
    public sealed class FakeEngine : IArchiveEngine
    {
        public string Name => "Fake";
        public string ExePath => "fake.exe";
        public Dictionary<string, string> Passwords = new(StringComparer.OrdinalIgnoreCase);   // 檔名 → 密碼（沒列 = 沒加密）
        public Dictionary<string, Dictionary<string, byte[]>> Contents = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Tested = new();

        bool Ok(string archive, string pw)
        {
            var n = Path.GetFileName(archive);
            return !Passwords.TryGetValue(n, out var right) || right == pw;
        }

        public Task<int> TestAsync(string archive, string password, CancellationToken ct)
        {
            Tested.Add(Path.GetFileName(archive) + ":" + password);
            return Task.FromResult(Ok(archive, password) ? 0 : 14);
        }

        public Task<int> ExtractAsync(string archive, string password, string outDir, CancellationToken ct)
        {
            if (!Ok(archive, password)) return Task.FromResult(14);
            Directory.CreateDirectory(outDir);
            if (Contents.TryGetValue(Path.GetFileName(archive), out var files))
                foreach (var kv in files)
                {
                    var p = Path.Combine(outDir, kv.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    File.WriteAllBytes(p, kv.Value);
                }
            return Task.FromResult(0);
        }
    }
}
