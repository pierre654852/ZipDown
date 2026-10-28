namespace ZipDown.Core.Tests
{
    public class NameRulesTests
    {
        static readonly string[] Junk = { "删", "刪" };

        [Theory]
        [InlineData("abc.zip密码123", "abc.zip", "123")]
        [InlineData("abc.zippw123", "abc.zip", "123")]
        [InlineData("abc.7z.jpgpw:123", "abc.7z", "123")]
        [InlineData("abc.rar解压码：xyz", "abc.rar", "xyz")]
        [InlineData("abc.7z.jpg", "abc.7z", null)]
        [InlineData("abc.7z.001.TXT", "abc.7z.001", null)]
        [InlineData("abc.zip", "abc.zip", null)]
        public void CleanAndPassword(string name, string clean, string pw)
        {
            var i = NameRules.Analyze(name, Junk);
            Assert.True(i.HasToken);
            Assert.Equal(clean, i.Clean);
            Assert.Equal(pw, i.Password);
        }

        [Fact]
        public void JunkAnywhere()
        {
            var i = NameRules.Analyze("x.z删i删p删", Junk);
            Assert.Equal("x.zip", i.Clean);
            Assert.Equal(".zip", i.TokenExt);
        }

        [Theory]
        [InlineData("a.part1.rar", 1, false, false)]
        [InlineData("a.part02.rar", 2, false, true)]
        [InlineData("a.7z.001", 1, false, false)]
        [InlineData("a.zip.003", 3, false, true)]
        [InlineData("a.z01", 0, true, true)]
        [InlineData("a.zip", 0, false, false)]
        public void Volumes(string name, int no, bool zipPart, bool secondary)
        {
            var i = NameRules.Analyze(name, null);
            Assert.Equal(no, i.VolumeNo);
            Assert.Equal(zipPart, i.IsZipOrRarPart);
            Assert.Equal(secondary, i.IsSecondary);
        }

        [Theory]
        [InlineData("资源 解压密码：abc", "abc")]
        [InlineData("資源【密碼 hello】", "hello")]
        [InlineData("pw=1234", "1234")]
        [InlineData("普通資料夾", null)]
        public void KeywordPassword(string folder, string pw)
        {
            Assert.Equal(pw, NameRules.KeywordPassword(folder));
        }

        [Theory]
        [InlineData("video.mp4", ArcKind.SevenZip, "video.7z")]
        [InlineData("26.10.05", ArcKind.Zip, "26.10.05.zip")]
        [InlineData("noext", ArcKind.Rar, "noext.rar")]
        public void WithArchiveExt(string name, ArcKind k, string expect)
        {
            Assert.Equal(expect, NameRules.WithArchiveExt(name, k));
        }

        [Theory]
        [InlineData("abc.part1.rar", "abc")]
        [InlineData("abc.7z.001", "abc")]
        [InlineData("abc.zip", "abc")]
        [InlineData("abc.7z", "abc")]
        public void OutFolder(string name, string expect)
        {
            Assert.Equal(expect, NameRules.OutFolderName(name));
        }
    }
}
