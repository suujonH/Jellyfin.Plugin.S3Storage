using System.Xml.Serialization;
using Jellyfin.Plugin.S3Storage.Configuration;
using Xunit;

namespace Jellyfin.Plugin.S3Storage.Tests;

public sealed class AllowedOriginsTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private StorageBucket ValidWorkerBucket() => new()
    {
        Endpoint = "https://s3.example", Bucket = "media", LocalPath = _root.FullName, AccessKey = "key", SecretKey = "secret",
        DeliveryMode = DeliveryMode.Worker, PublicBaseUrl = "https://worker.example", SigningSecret = new string('s', 32)
    };

    [Fact]
    public void NewBucketsAllowEveryOrigin()
    {
        Assert.Equal("*", new StorageBucket().AllowedOrigins);
        Assert.Equal("*", ValidWorkerBucket().AllowedOrigins);
        StorageMapping.Validate(ValidWorkerBucket());
    }

    [Fact]
    public void ConfigurationSavedByAnEarlierVersionStillAllowsEveryOrigin()
    {
        var serializer = new XmlSerializer(typeof(StorageBucket));

        var old = (StorageBucket)serializer.Deserialize(new StringReader("<StorageBucket><Name>Old</Name><Bucket>media</Bucket></StorageBucket>"))!;
        Assert.Equal("*", old.AllowedOrigins);

        var writer = new StringWriter();
        serializer.Serialize(writer, new StorageBucket { AllowedOrigins = "https://a.example,http://b.example:8096" });
        var saved = (StorageBucket)serializer.Deserialize(new StringReader(writer.ToString()))!;
        Assert.Equal("https://a.example,http://b.example:8096", saved.AllowedOrigins);
    }

    [Theory]
    [InlineData("*")]
    [InlineData(" * ")]
    [InlineData("https://jellyfin.example.com")]
    [InlineData("http://192.168.1.10:8096")]
    [InlineData("http://[::1]:8096")]
    [InlineData("https://a.example,http://b.example:8096")]
    [InlineData(" https://a.example , http://b.example:8096 ")]
    public void AcceptsAWildcardOrCanonicalOrigins(string value)
    {
        StorageMapping.ValidateAllowedOrigins(value);

        var bucket = ValidWorkerBucket();
        bucket.AllowedOrigins = value;
        StorageMapping.Validate(bucket);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("jellyfin.example.com")]
    [InlineData("https://a.example/")]
    [InlineData("https://a.example/path")]
    [InlineData("https://a.example?x=1")]
    [InlineData("https://a.example#fragment")]
    [InlineData("ftp://a.example")]
    [InlineData("*,https://a.example")]
    [InlineData("https://a.example,")]
    [InlineData("https://user@a.example")]
    [InlineData("HTTPS://A.EXAMPLE")]
    [InlineData("https://a.example:443")]
    public void RejectsEverythingElse(string value)
    {
        Assert.Throws<ArgumentException>(() => StorageMapping.ValidateAllowedOrigins(value));

        var bucket = ValidWorkerBucket();
        bucket.AllowedOrigins = value;
        Assert.Throws<ArgumentException>(() => StorageMapping.Validate(bucket));
    }

    [Fact]
    public void MissingValueIsRejected()
    {
        Assert.Throws<ArgumentException>(() => StorageMapping.ValidateAllowedOrigins(null));
    }
}
