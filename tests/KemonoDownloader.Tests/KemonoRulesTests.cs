using KemonoDownloader.Core;

namespace KemonoDownloader.Tests;

public sealed class KemonoRulesTests
{
    [Fact]
    public void ParsePostUrl_ReturnsCanonicalParts()
    {
        var result = KemonoRules.ParsePostUrl("https://www.kemono.cr/fanbox/user/123/post/456/?ignored=1");
        Assert.Equal("fanbox", result.Service);
        Assert.Equal("123", result.UserId);
        Assert.Equal("456", result.PostId);
        Assert.Equal("https://kemono.cr/fanbox/user/123/post/456", result.CanonicalUri.ToString().TrimEnd('/'));
    }

    [Theory]
    [InlineData("https://example.com/fanbox/user/1/post/2")]
    [InlineData("https://kemono.cr/fanbox/user/1")]
    [InlineData("not-a-url")]
    public void ParsePostUrl_RejectsInvalidLinks(string value) => Assert.Throws<ArgumentException>(() => KemonoRules.ParsePostUrl(value));

    [Fact]
    public void Metadata_SanitizesTitleAndBuildsStableKey()
    {
        var metadata = KemonoRules.GetPostMetadata(new KemonoPost { Id = "99", Title = "A:B?", Published = "2026-08-21T10:00:00" });
        Assert.Equal("2026-08-21 A_B_", metadata.FolderName);
        Assert.Equal("2026-08-21_A_B__99", metadata.Key);
    }

    [Fact]
    public void DownloadTasks_DeduplicateMainAttachmentByName()
    {
        var post = new KemonoPost
        {
            File = new KemonoAttachment { Name = "one.jpg", Path = "/1.jpg" },
            Attachments =
            [
                new KemonoAttachment { Name = "one.jpg", Path = "/1-copy.jpg" },
                new KemonoAttachment { Name = "two.png", Path = "/2.png" }
            ]
        };
        var tasks = KemonoRules.GetDownloadTasks(post);
        Assert.Equal(2, tasks.Count);
        Assert.Equal("1.jpg", KemonoRules.GetSequentialFilename(tasks[0].Attachment, tasks[0].Index));
        Assert.Equal("2.png", KemonoRules.GetSequentialFilename(tasks[1].Attachment, tasks[1].Index));
    }

    [Fact]
    public void Validate_RejectsOutOfRangeConcurrency()
    {
        var config = new DownloadConfig { Mode = DownloadMode.Author, Service = "fanbox", Username = "1", SavePath = "C:\\data", Concurrent = 21 };
        Assert.Throws<ArgumentOutOfRangeException>(() => KemonoRules.Validate(config));
    }

    [Fact]
    public void ParsePostUrl_RewritesToSelectedCustomDomain()
    {
        var result = KemonoRules.ParsePostUrl("https://kemono.cr/fanbox/user/1/post/2", ["kemono.cr", "mirror.example.com"], "mirror.example.com");
        Assert.Equal("mirror.example.com", result.CanonicalUri.Host);
    }

    [Theory]
    [InlineData("https://www.kemono.su/", "kemono.su")]
    [InlineData(" mirror.example.com ", "mirror.example.com")]
    [InlineData("www.mirror.example.com", "www.mirror.example.com")]
    public void NormalizeDomain_RemovesSchemeAndDecorations(string input, string expected) => Assert.Equal(expected, KemonoRules.NormalizeDomain(input));

    [Fact]
    public void BuildDownloadUri_UsesSelectedDomainForRelativePath()
    {
        var uri = KemonoRules.BuildDownloadUri(new KemonoAttachment { Name = "a.jpg", Path = "/aa/a.jpg" }, "kemono.su");
        Assert.Equal("kemono.su", uri.Host);
    }

    [Fact]
    public void Pawchive_UsesDedicatedFileAndThumbnailHosts()
    {
        var uri = KemonoRules.BuildDownloadUri(new KemonoAttachment { Name = "a.jpeg", Path = "/aa/a.jpeg" }, "pawchive.pw");
        Assert.Equal("file.pawchive.pw", uri.Host);
        Assert.Equal("/data/aa/a.jpeg", uri.AbsolutePath);
        Assert.Equal("https://img.pawchive.pw", KemonoRules.GetThumbnailBaseUrl("pawchive.pw"));
    }

    [Fact]
    public void Pawchive_RealApiAttachmentPathBuildsExpectedUrl()
    {
        var attachment = new KemonoAttachment
        {
            Name = "gCgNgVd7wpUfeMMhJYpUTqOK.jpeg",
            Path = "/e5/a3/e5a3af1ff9bc07a0cee8637b8b6801b603bb805167dde9213ab65f0b3b1bf079.jpeg"
        };
        var uri = KemonoRules.BuildDownloadUri(attachment, "pawchive.pw");
        Assert.Equal("https://file.pawchive.pw/data/e5/a3/e5a3af1ff9bc07a0cee8637b8b6801b603bb805167dde9213ab65f0b3b1bf079.jpeg?f=gCgNgVd7wpUfeMMhJYpUTqOK.jpeg", uri.ToString());
    }

    [Fact]
    public void JpeFilename_IsRecognizedAsImageForThumbnailFallback()
    {
        Assert.True(KemonoRules.IsImageFile("16.jpe"));
        var uri = KemonoRules.BuildThumbnailUri(new KemonoAttachment
        {
            Name = "7fed017b-c372-415f-ba1f-e38d4e957f26.jpe",
            Path = "/f2/f4/f2f46bc9dcef902dfdef8a652955d9ecc109cd188e408ba369a168295d0b316c.jpg"
        }, "pawchive.pw");
        Assert.Equal("https://img.pawchive.pw/thumbnail/data/f2/f4/f2f46bc9dcef902dfdef8a652955d9ecc109cd188e408ba369a168295d0b316c.jpg", uri.ToString());
    }

    [Theory]
    [InlineData("pawchive.pw", "kemono.cr", "pawchive.pw")]
    [InlineData("kemono.cr", "kemono.su", "kemono.su")]
    public void ResolveTaskDomain_PreservesPawchiveAndSwitchesKemono(string input, string selected, string expected) =>
        Assert.Equal(expected, KemonoRules.ResolveTaskDomain(input, selected));
}
