using System.Diagnostics;
using ZipDown.Core;

namespace ZipDown.Engines
{
    /// <summary>執行外部解壓程式，取消時連同子程序一起關掉。</summary>
    public static class ProcessRunner
    {
        public static async Task<int> RunAsync(string exe, IEnumerable<string> args, string workDir, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;

            using var p = new Process { StartInfo = psi };
            // 輸出要讀掉，否則緩衝區滿了程式會卡住
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.Start();
            try { p.StandardInput.Close(); } catch { }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                try { p.WaitForExit(10000); } catch { }
                throw;
            }
            return p.ExitCode;
        }
    }

    /// <summary>Bandizip 的命令列版 bz.exe。實測：密碼錯 14、成功 0。</summary>
    public sealed class BandizipEngine : IArchiveEngine
    {
        public string Name => "Bandizip";
        public string ExePath { get; }
        public BandizipEngine(string exe) { ExePath = exe; }

        public Task<int> TestAsync(string archive, string password, CancellationToken ct) =>
            ProcessRunner.RunAsync(ExePath, new[] { "t", "-y", "-p:" + password, archive }, null, ct);

        public Task<int> ExtractAsync(string archive, string password, string outDir, CancellationToken ct) =>
            ProcessRunner.RunAsync(ExePath, new[] { "x", "-y", "-aos", "-o:" + outDir, "-p:" + password, archive }, null, ct);
    }

    /// <summary>WinRAR.exe（背景模式）。官方文件：密碼錯 11、成功 0（尚未實測）。</summary>
    public sealed class WinRarEngine : IArchiveEngine
    {
        public string Name => "WinRAR";
        public string ExePath { get; }
        public WinRarEngine(string exe) { ExePath = exe; }

        public Task<int> TestAsync(string archive, string password, CancellationToken ct) =>
            ProcessRunner.RunAsync(ExePath, new[] { "t", "-ibck", "-inul", "-y", "-p" + password, archive }, null, ct);

        // 跟 V7 一樣：解到工作目錄
        public Task<int> ExtractAsync(string archive, string password, string outDir, CancellationToken ct) =>
            ProcessRunner.RunAsync(ExePath, new[] { "x", "-ibck", "-inul", "-y", "-o-", "-p" + password, archive }, outDir, ct);
    }

    public static class EngineLocator
    {
        public static string FindBandizip(Settings s) => Find(s.BandizipPath, "bz.exe", "Bandizip");
        public static string FindWinRAR(Settings s) => Find(s.WinRARPath, "WinRAR.exe", "WinRAR");

        /// <summary>依設定建立引擎：auto = 有 Bandizip 優先，否則 WinRAR。找不到回傳 null。</summary>
        public static IArchiveEngine Create(Settings s)
        {
            var mode = (s.Engine ?? "auto").ToLowerInvariant();
            if (mode != "winrar")
            {
                var bz = FindBandizip(s);
                if (bz != null) return new BandizipEngine(bz);
            }
            if (mode != "bandizip")
            {
                var wr = FindWinRAR(s);
                if (wr != null) return new WinRarEngine(wr);
            }
            return null;
        }

        static string Find(string configured, string exe, string sub)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return File.Exists(configured) ? configured : null;

            var cands = new List<string> { Path.Combine(Settings.AppDir, exe) };
            foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
            {
                var pf = Environment.GetEnvironmentVariable(env);
                if (!string.IsNullOrEmpty(pf)) cands.Add(Path.Combine(pf, sub, exe));
            }
            foreach (var c in cands) if (File.Exists(c)) return c;

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var d in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try { var c = Path.Combine(d.Trim('"'), exe); if (File.Exists(c)) return c; } catch { }
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                    var v = k?.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(v)) { v = v.Trim('"'); if (File.Exists(v)) return v; }
                }
                catch { }
            }
            return null;
        }
    }
}
