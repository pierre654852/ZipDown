using static ZipDown.Core.Tests.FakeFiles;

namespace ZipDown.Core.Tests
{
    public class ScannerTests
    {
        [Fact]
        public void FindsDisguisedArchives()
        {
            using var t = new TempDir();
            t.Write("a.7z.jpg", Fake7z());
            t.Write("b.zip说明.txt", System.Text.Encoding.UTF8.GetBytes("只是說明文字"));
            t.Write("c.7z.001.TXT", Fake7z());
            t.Write("c.7z.002.TXT", Pad(100));
            t.Write("d.part2.rar", Pad(100));
            t.Write("v.mp4", Cat(Mp4(), Fake7z()));
            t.Write("real.mp4", Mp4());
            t.Write("e删.zip", FakeZip());

            var items = new Scanner(new[] { "删" }).ScanFolder(t.Path, t.Path, inner: false);
            var names = items.Select(i => i.OriginalName + "→" + i.NewName).OrderBy(x => x).ToList();

            Assert.Equal(new[]
            {
                "a.7z.jpg→a.7z",
                "c.7z.001.TXT→c.7z.001",
                "e删.zip→e.zip",
                "v.mp4→v.7z",
            }, names);

            var c = items.Single(i => i.OriginalName == "c.7z.001.TXT");
            Assert.Equal(2, c.Renames.Count);
            Assert.EndsWith("c.7z.002", c.Renames[1].To);
        }

        [Fact]
        public void InnerLevel_ImagesAndNoExt()
        {
            using var t = new TempDir();
            t.Write("pic.jpg", Cat(new byte[] { 0xFF, 0xD8 }, Pad(100), FakeZip()));
            t.Write("data", Fake7z());
            t.Write("plain.jpg", Cat(new byte[] { 0xFF, 0xD8 }, Pad(100)));

            var top = new Scanner(null).ScanFolder(t.Path, t.Path, inner: false);
            Assert.Empty(top);

            var inner = new Scanner(null).ScanFolder(t.Path, t.Path, inner: true);
            Assert.Equal(new[] { "data.7z", "pic.zip" }, inner.Select(i => i.NewName).OrderBy(x => x));
        }

        [Fact]
        public void FolderPassword()
        {
            using var t = new TempDir();
            t.Write(Path.Combine("资源 解压密码：xyz", "sub", "e.zip"), FakeZip());
            var items = new Scanner(null).ScanFolder(t.Path, t.Path, inner: false);
            var e = Assert.Single(items);
            Assert.Equal("xyz", e.FolderPassword);
            Assert.Equal("xyz", e.Password);
        }

        [Fact]
        public void NamePasswordWins()
        {
            using var t = new TempDir();
            t.Write(Path.Combine("密码 aaa", "f.zippw bbb"), FakeZip());
            var e = Assert.Single(new Scanner(null).ScanFolder(t.Path, t.Path, inner: false));
            Assert.Equal("bbb", e.Password);
        }
    }

    public class PasswordBookTests
    {
        [Fact]
        public void MostSuccessfulFirst_AndPersists()
        {
            using var t = new TempDir();
            var b = new PasswordBook(t.Path);
            b.SetPasswords(new[] { "a", "b", "c" });
            b.RecordSuccess("c");
            b.RecordSuccess("c");
            b.RecordSuccess("b");
            Assert.Equal(new[] { "c", "b", "a" }, b.Ordered());

            var again = new PasswordBook(t.Path);
            Assert.Equal(new[] { "c", "b", "a" }, again.Ordered());
            Assert.True(again.AddIfMissing("d"));
            Assert.False(again.AddIfMissing("a"));
        }
    }

    public class UnpackerTests
    {
        static Settings S(string dir) => new Settings { FilePath = Path.Combine(dir, "settings.ini") };

        [Fact]
        public async Task Nested_CollapsesSingleFolder_AndSkipsExistingOutput()
        {
            using var t = new TempDir();
            var app = t.P("app"); Directory.CreateDirectory(app);
            var work = t.P("work");
            t.Write(Path.Combine("work", "outer.7z.jpg"), Fake7z());

            var eng = new FakeEngine();
            eng.Passwords["outer.7z"] = "right";
            eng.Contents["outer.7z"] = new() { ["readme.txt"] = Pad(5), ["inner"] = Fake7z() };
            // inner.7z 裡本身包了一層 inner 資料夾 → 應該被省略
            eng.Contents["inner.7z"] = new() { [Path.Combine("inner", "pic.png")] = Pad(9), [Path.Combine("inner", "sub", "a.txt")] = Pad(3) };

            var book = new PasswordBook(app);
            book.SetPasswords(new[] { "wrong1", "right" });
            var items = new Scanner(new List<string>()).ScanPath(work);
            var item = Assert.Single(items);
            var rows = new List<PlanItem>(items);

            var u = new Unpacker(S(app), book, eng, Path.Combine(app, "log.txt")) { ChildAdded = (p, c) => rows.Add(c) };
            await u.RunAsync(items, CancellationToken.None);

            Assert.Equal(ItemStatus.Done, item.Status);
            Assert.Equal("right", item.Password);                                           // 顯示實際用到的密碼
            var top = Path.Combine(work, "outer");
            Assert.True(File.Exists(Path.Combine(top, "inner.7z")));                        // 中間層壓縮檔保留
            Assert.True(File.Exists(Path.Combine(top, "readme.txt")));
            Assert.True(File.Exists(Path.Combine(top, "inner", "pic.png")));                // inner\inner 省掉一層
            Assert.True(File.Exists(Path.Combine(top, "inner", "sub", "a.txt")));           // 只省一層
            Assert.False(Directory.Exists(Path.Combine(top, "inner", "inner")));
            Assert.Equal(1, book.SuccessCount("right"));
            Assert.Equal(2, u.Ok);
            Assert.Equal(2, u.Renamed);                                                      // outer.7z.jpg、inner

            // 刪除原檔：外層＋內層中間壓縮檔
            var del = Unpacker.FilesToDelete(rows).Select(Path.GetFileName).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "inner.7z", "outer.7z" }, del);

            // 輸出資料夾還在 → 跳過
            var again = new Scanner(null).ScanPath(work);
            var u2 = new Unpacker(S(app), book, eng, null);
            await u2.RunAsync(again, CancellationToken.None);
            Assert.Equal(ItemStatus.Skipped, again[0].Status);
            Assert.Equal(0, u2.Ok);

            // 輸出資料夾刪掉 → 重新解
            Directory.Delete(top, true);
            var third = new Scanner(null).ScanPath(work);
            var u3 = new Unpacker(S(app), book, eng, null);
            await u3.RunAsync(third, CancellationToken.None);
            Assert.Equal(ItemStatus.Done, third[0].Status);
        }

        [Fact]
        public async Task Collapse_NotWhenFilesBesideFolder()
        {
            using var t = new TempDir();
            var app = t.P("app"); Directory.CreateDirectory(app);
            t.Write(Path.Combine("work", "x.zip"), FakeZip());
            var eng = new FakeEngine();
            eng.Contents["x.zip"] = new() { [Path.Combine("d", "a.txt")] = Pad(3), ["b.txt"] = Pad(2) };
            var items = new Scanner(null).ScanPath(t.P("work"));
            await new Unpacker(S(app), new PasswordBook(app), eng, null).RunAsync(items, CancellationToken.None);
            Assert.True(File.Exists(t.P("work", "x", "d", "a.txt")));
        }

        [Fact]
        public async Task WrongPasswords_Fail()
        {
            using var t = new TempDir();
            var app = t.P("app"); Directory.CreateDirectory(app);
            t.Write(Path.Combine("work", "x.zip"), FakeZip());
            var eng = new FakeEngine();
            eng.Passwords["x.zip"] = "secret";
            var book = new PasswordBook(app);
            book.SetPasswords(new[] { "a", "b" });
            var items = new Scanner(null).ScanPath(t.P("work"));
            var u = new Unpacker(S(app), book, eng, null);
            await u.RunAsync(items, CancellationToken.None);
            Assert.Equal(ItemStatus.WrongPassword, items[0].Status);
            Assert.False(Directory.Exists(t.P("work", "x")));
            Assert.Equal(3, eng.Tested.Count);   // 假密碼 + a + b
        }

        [Fact]
        public async Task PasswordFromName_TriedFirst_AndAdded()
        {
            using var t = new TempDir();
            var app = t.P("app"); Directory.CreateDirectory(app);
            t.Write(Path.Combine("work", "y.zip密码abc"), FakeZip());
            var eng = new FakeEngine();
            eng.Passwords["y.zip"] = "abc";
            var book = new PasswordBook(app);
            book.SetPasswords(new[] { "a" });
            var items = new Scanner(null).ScanPath(t.P("work"));
            var u = new Unpacker(S(app), book, eng, null);
            await u.RunAsync(items, CancellationToken.None);
            Assert.Equal(ItemStatus.Done, items[0].Status);
            Assert.Equal(new[] { "y.zip:" + Unpacker.NoPasswordProbe, "y.zip:abc" }, eng.Tested);
            Assert.Contains("abc", book.Passwords);
        }

        [Fact]
        public async Task Damaged_IsNotWrongPassword()
        {
            using var t = new TempDir();
            var app = t.P("app"); Directory.CreateDirectory(app);
            t.Write(Path.Combine("work", "z.zip"), FakeZip());
            var eng = new FakeEngine();
            eng.Damaged.Add("z.zip");
            var items = new Scanner(null).ScanPath(t.P("work"));
            var u = new Unpacker(S(app), new PasswordBook(app), eng, null);
            await u.RunAsync(items, CancellationToken.None);
            Assert.Equal(ItemStatus.Damaged, items[0].Status);
        }

        [Fact]
        public async Task InnerFailure_ParentPartlyFailed_ChildRowsReported()
        {
            using var t = new TempDir();
            var app = t.P("app"); Directory.CreateDirectory(app);
            t.Write(Path.Combine("work", "outer.7z"), Fake7z());
            var eng = new FakeEngine();
            eng.Contents["outer.7z"] = new() { ["a.7z"] = Fake7z(), ["b.zip"] = FakeZip() };
            eng.Passwords["b.zip"] = "nope";
            var items = new Scanner(null).ScanPath(t.P("work"));
            var children = new List<PlanItem>();
            var u = new Unpacker(S(app), new PasswordBook(app), eng, null)
            {
                ChildAdded = (parent, child) => children.Add(child),
            };
            await u.RunAsync(items, CancellationToken.None);

            Assert.Equal(ItemStatus.PartialFail, items[0].Status);
            Assert.Equal(2, children.Count);
            Assert.All(children, c => { Assert.Same(items[0], c.Parent); Assert.Equal(1, c.Depth); });
            Assert.Equal(ItemStatus.Done, children.Single(c => c.OriginalName == "a.7z").Status);
            Assert.Equal(ItemStatus.WrongPassword, children.Single(c => c.OriginalName == "b.zip").Status);
            Assert.Equal("\u3000└ a.7z", children[0].DisplayName);
            Assert.Empty(Unpacker.FilesToDelete(items.Concat(children).ToList()));          // 部分失敗整組不刪
        }

        [Fact]
        public async Task NothingToDo_WhenAllDone()
        {
            using var t = new TempDir();
            var app = t.P("app"); Directory.CreateDirectory(app);
            var item = new PlanItem { Dir = t.Path, RootDir = t.Path, OriginalName = "x.zip", NewName = "x.zip", Status = ItemStatus.Done };
            var eng = new FakeEngine();
            var u = new Unpacker(S(app), new PasswordBook(app), eng, null);
            await u.RunAsync(new[] { item }, CancellationToken.None);
            Assert.Empty(eng.Tested);
        }
    }
}
