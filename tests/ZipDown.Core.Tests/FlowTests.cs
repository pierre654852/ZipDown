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
        public async Task NestedArchive_FlattensToTop_AndRemembers()
        {
            using var t = new TempDir();
            var app = t.P("app"); Directory.CreateDirectory(app);
            var work = t.P("work");
            t.Write(Path.Combine("work", "outer.7z.jpg"), Fake7z());

            var eng = new FakeEngine();
            eng.Passwords["outer.7z"] = "right";
            eng.Contents["outer.7z"] = new() { ["readme.txt"] = Pad(5), ["inner"] = Fake7z() };
            eng.Contents["inner.7z"] = new() { ["readme.txt"] = Pad(7), ["pic.png"] = Pad(9) };

            var book = new PasswordBook(app);
            book.SetPasswords(new[] { "wrong1", "right" });
            var done = new DoneList(app);
            var journal = new RenameJournal(app);
            var items = new Scanner(new List<string>(), journal, done).ScanPath(work);
            var item = Assert.Single(items);

            var u = new Unpacker(S(app), book, done, journal, eng, Path.Combine(app, "log.txt"));
            await u.RunAsync(items, CancellationToken.None);

            Assert.Equal(ItemStatus.Done, item.Status);
            var top = Path.Combine(work, "outer");
            Assert.True(File.Exists(Path.Combine(top, "inner.7z")));            // 中間層壓縮檔保留
            Assert.True(File.Exists(Path.Combine(top, "readme.txt")));
            Assert.True(File.Exists(Path.Combine(top, "readme (2).txt")));      // 撞名加 (2)
            Assert.True(File.Exists(Path.Combine(top, "pic.png")));             // 內層搬到第一層
            Assert.False(Directory.Exists(Path.Combine(top, "inner")));
            Assert.Equal(1, book.SuccessCount("right"));
            Assert.Equal(2, u.Ok);
            Assert.Equal(2, u.Renamed);                                          // outer.7z.jpg、inner

            // 第二次跑：清單上已解過
            Directory.Delete(top, true);
            var again = new Scanner(new List<string>(), journal, done).ScanPath(work);
            Assert.Equal(ItemStatus.AlreadyDone, Assert.Single(again).Status);
            var u2 = new Unpacker(S(app), book, done, journal, eng, null);
            await u2.RunAsync(again, CancellationToken.None);
            Assert.Equal(0, u2.Ok);
            Assert.Equal(1, u2.Skipped);

            // 復原改名
            var (ok, _) = journal.UndoAll(null);
            Assert.True(ok >= 1);
            Assert.True(File.Exists(Path.Combine(work, "outer.7z.jpg")));
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
            var u = new Unpacker(S(app), book, new DoneList(app), new RenameJournal(app), eng, null);
            await u.RunAsync(items, CancellationToken.None);
            Assert.Equal(ItemStatus.Failed, items[0].Status);
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
            var u = new Unpacker(S(app), book, new DoneList(app), new RenameJournal(app), eng, null);
            await u.RunAsync(items, CancellationToken.None);
            Assert.Equal(ItemStatus.Done, items[0].Status);
            Assert.Equal(new[] { "y.zip:" + Unpacker.NoPasswordProbe, "y.zip:abc" }, eng.Tested);
            Assert.Contains("abc", book.Passwords);
        }
    }
}
