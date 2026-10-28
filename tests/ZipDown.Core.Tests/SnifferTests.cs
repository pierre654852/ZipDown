using static ZipDown.Core.Tests.FakeFiles;

namespace ZipDown.Core.Tests
{
    public class SnifferTests
    {
        [Fact]
        public void RealVideo_IsVideo()
        {
            using var t = new TempDir();
            var p = t.Write("a.mp4", Mp4());
            Assert.Equal(Sniffer.Mp4Result.Video, Sniffer.Mp4(p, out var k));
            Assert.Equal(ArcKind.None, k);
            Assert.Equal(ArcKind.None, Sniffer.Detect(p));
        }

        [Fact]
        public void Mp4Followed7z_Is7z()
        {
            using var t = new TempDir();
            var p = t.Write("a.mp4", Cat(Mp4(), Fake7z()));
            Assert.Equal(Sniffer.Mp4Result.Archive, Sniffer.Mp4(p, out var k));
            Assert.Equal(ArcKind.SevenZip, k);
        }

        [Fact]
        public void Mp4FollowedZip_NeedsEndRecord()
        {
            using var t = new TempDir();
            var good = t.Write("good.mp4", Cat(Mp4(), FakeZip()));
            var bad = t.Write("bad.mp4", Cat(Mp4(), Zip, Pad(300)));
            Assert.Equal(ArcKind.Zip, Sniffer.Detect(good));
            Assert.Equal(Sniffer.Mp4Result.Unknown, Sniffer.Mp4(bad, out _));
        }

        [Fact]
        public void FakeHeaderAtFileTail_IsNotArchive()
        {
            // 影片後面一大段垃圾，檔尾才有假的 RAR 標頭 → 不能當壓縮檔
            using var t = new TempDir();
            var p = t.Write("a.mp4", Cat(Mp4(), Pad(6000), Rar));
            Assert.Equal(Sniffer.Mp4Result.Unknown, Sniffer.Mp4(p, out var k));
            Assert.Equal(ArcKind.None, k);
        }

        [Fact]
        public void ArchiveRightAfterVideo_WinsOverLaterJunk()
        {
            // 影片後先接 RAR，後面又有 7z 簽名 → 取先出現的
            using var t = new TempDir();
            var p = t.Write("a.mp4", Cat(Mp4(), Rar, Pad(100), SevenZ));
            Sniffer.Mp4(p, out var k);
            Assert.Equal(ArcKind.Rar, k);
        }

        [Fact]
        public void Head_DetectsByContent()
        {
            using var t = new TempDir();
            Assert.Equal(ArcKind.SevenZip, Sniffer.Head(t.Write("x", Fake7z())));
            Assert.Equal(ArcKind.Rar, Sniffer.Head(t.Write("y", Cat(Rar, Pad(10)))));
            Assert.Equal(ArcKind.Zip, Sniffer.Head(t.Write("z", FakeZip())));
            Assert.Equal(ArcKind.None, Sniffer.Head(t.Write("w", Pad(50))));
        }

        [Fact]
        public void ImageWithZipTail()
        {
            using var t = new TempDir();
            var img = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
            Assert.True(Sniffer.ZipTail(t.Write("a.jpg", Cat(img, Pad(500), FakeZip()))));
            Assert.False(Sniffer.ZipTail(t.Write("b.jpg", Cat(img, Pad(500)))));
        }

        [Fact]
        public void NotMp4()
        {
            using var t = new TempDir();
            Assert.Equal(Sniffer.Mp4Result.NotMp4, Sniffer.Mp4(t.Write("a.mp4", Pad(100)), out _));
        }
    }
}
