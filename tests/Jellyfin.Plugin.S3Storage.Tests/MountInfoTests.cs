using Xunit;

namespace Jellyfin.Plugin.S3Storage.Tests;

public sealed class MountInfoTests
{
    // Excerpt of a real /proc/self/mountinfo; paths use the kernel's octal escapes (\040 space, \011 tab, \012 newline, \134 backslash).
    private static readonly string[] Table =
    [
        @"22 28 0:21 / /proc rw,nosuid,nodev,noexec,relatime shared:13 - proc proc rw",
        @"28 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw,errors=remount-ro",
        @"40 28 0:40 / /mnt/s3 rw,nosuid,nodev,relatime shared:70 - fuse.s3fs s3fs rw,user_id=1000,group_id=1000",
        @"41 28 0:41 / /mnt/my\040media rw,relatime - fuse.s3fs s3fs rw",
        @"42 28 0:42 / /mnt/tab\011name rw,relatime - fuse sshfs rw",
        @"43 28 0:43 / /mnt/line\012feed rw,relatime - nfs4 server:/export rw",
        @"44 28 0:44 / /mnt/back\134slash rw,relatime - ext4 /dev/sdb1 rw",
        @"45 28 0:45 / /mnt/a\134040b rw,relatime - xfs /dev/sdc1 rw",
        @"46 28 0:46 / /mnt/bare rw,relatime - tmpfs"
    ];

    [Fact]
    public void FindsTheFileSystemTypeOfAMountPoint()
    {
        Assert.Equal("fuse.s3fs", MountInfo.Parse(Table, "/mnt/s3"));
        Assert.Equal("proc", MountInfo.Parse(Table, "/proc"));
        Assert.Equal("ext4", MountInfo.Parse(Table, "/"));
        Assert.Equal("tmpfs", MountInfo.Parse(Table, "/mnt/bare"));
    }

    [Theory]
    [InlineData("/mnt/my media", "fuse.s3fs")]
    [InlineData("/mnt/tab\tname", "fuse")]
    [InlineData("/mnt/line\nfeed", "nfs4")]
    [InlineData("/mnt/back\\slash", "ext4")]
    public void DecodesEscapedCharactersOfTheMountPoint(string mountPoint, string expected)
    {
        Assert.Equal(expected, MountInfo.Parse(Table, mountPoint));
    }

    [Fact]
    public void EscapedMountPointsDoNotMatchTheirRawSpelling()
    {
        Assert.Null(MountInfo.Parse(Table, @"/mnt/my\040media"));
        Assert.Null(MountInfo.Parse(Table, @"/mnt/back\134slash"));
    }

    [Fact]
    public void EachEscapeIsDecodedOnce()
    {
        // \134 is a backslash; the digits after it are plain characters and must not form a second escape.
        Assert.Equal("xfs", MountInfo.Parse(Table, @"/mnt/a\040b"));
        Assert.Null(MountInfo.Parse(Table, "/mnt/a b"));
    }

    [Fact]
    public void OnlyTheExactMountPointMatches()
    {
        Assert.Null(MountInfo.Parse(Table, "/mnt"));
        Assert.Null(MountInfo.Parse(Table, "/mnt/s3/sub"));
        Assert.Null(MountInfo.Parse(Table, "/mnt/s"));
        Assert.Null(MountInfo.Parse(Table, "/mnt/s3/"));
        Assert.Null(MountInfo.Parse(Table, "/MNT/S3"));
    }

    [Fact]
    public void TheLastMountOnADirectoryIsTheOneInEffect()
    {
        string[] stacked =
        [
            @"30 28 8:1 / /srv rw - ext4 /dev/sda1 rw",
            @"31 28 0:50 / /srv rw - fuse.s3fs s3fs rw"
        ];

        Assert.Equal("fuse.s3fs", MountInfo.Parse(stacked, "/srv"));
    }

    [Fact]
    public void MalformedLinesAreSkipped()
    {
        string[] lines = ["", "garbage", "1 2 3 - fuse.s3fs s3fs", "40 28 0:40 / /mnt/s3 rw fuse.s3fs s3fs rw", @"40 28 0:40 / /mnt/s3 rw - fuse.s3fs s3fs rw"];

        Assert.Equal("fuse.s3fs", MountInfo.Parse(lines, "/mnt/s3"));
        Assert.Null(MountInfo.Parse(lines[..4], "/mnt/s3"));
    }

    [Fact]
    public void FileSystemTypeReadsTheLinuxMountTableOnly()
    {
        if (OperatingSystem.IsLinux()) Assert.NotNull(MountInfo.FileSystemType("/"));
        else Assert.Null(MountInfo.FileSystemType(Path.GetTempPath()));
    }
}
