using System.Net;
using System.Text.Json;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Tests;

public sealed class WikipediaLookupServiceTests
{
    [Fact]
    public async Task UsesPrimaryWikipediaLanguageAndReturnsSourceMetadata()
    {
        var handler = new WikipediaHandler(language => language == "zh"
            ? ArticleJson("量子纠缠", "量子力学中的一种现象。", "zh")
            : throw new InvalidOperationException("Fallback should not be requested."));
        using var http = new HttpClient(handler);
        var article = await new WikipediaLookupService(http).LookupAsync("  量子纠缠！ ", "zh-CN");

        Assert.NotNull(article);
        Assert.Equal("量子纠缠", article.Title);
        Assert.Equal("量子力学中的一种现象。", article.Extract);
        Assert.Equal("zh", article.Language);
        Assert.Equal("https://zh.wikipedia.org/wiki/%E9%87%8F%E5%AD%90%E7%BA%A0%E7%BC%A0", article.Url.AbsoluteUri);
        Assert.Equal(["zh"], handler.Languages);
        Assert.Contains("titles=%E9%87%8F%E5%AD%90%E7%BA%A0%E7%BC%A0", handler.RequestUris[0].Query);
    }

    [Fact]
    public async Task FallsBackToEnglishWhenChineseHasNoArticle()
    {
        var handler = new WikipediaHandler(language => language == "zh"
            ? MissingJson("量子纠缠")
            : ArticleJson("Quantum entanglement", "A physical phenomenon.", "en"));
        using var http = new HttpClient(handler);
        var article = await new WikipediaLookupService(http).LookupAsync("quantum entanglement", "zh-CN");

        Assert.NotNull(article);
        Assert.Equal("Quantum entanglement", article.Title);
        Assert.Equal("en", article.Language);
        Assert.Equal(["zh", "en"], handler.Languages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptySelectionsDoNotMakeNetworkRequests(string term)
    {
        var handler = new WikipediaHandler(_ => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler);
        Assert.Null(await new WikipediaLookupService(http).LookupAsync(term, "zh-CN"));
        Assert.Empty(handler.RequestUris);
    }

    private static string MissingJson(string title) => JsonSerializer.Serialize(new
    {
        query = new { pages = new[] { new { ns = 0, title, missing = true } } }
    });

    private static string ArticleJson(string title, string extract, string language) => JsonSerializer.Serialize(new
    {
        query = new
        {
            pages = new[]
            {
                new
                {
                    ns = 0,
                    title,
                    extract,
                    fullurl = $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(title)}"
                }
            }
        }
    });

    private sealed class WikipediaHandler(Func<string, string> response) : HttpMessageHandler
    {
        public List<string> Languages { get; } = [];
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = Assert.IsType<Uri>(request.RequestUri);
            RequestUris.Add(uri);
            var language = uri.Host.Split('.')[0];
            Languages.Add(language);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response(language))
            });
        }
    }
}
