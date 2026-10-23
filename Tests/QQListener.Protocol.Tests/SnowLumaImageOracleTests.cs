using System.Text.Json;
using QQListener.Protocol;

namespace QQListener.Protocol.Tests;

public sealed class SnowLumaImageOracleTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "snowluma-image-oracle.json")));
        foreach (var fixture in document.RootElement.GetProperty("fixtures").EnumerateArray())
            yield return [fixture.GetProperty("name").GetString()!, fixture.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ImageDecodingMatchesOfficialEncoderAndDecoder(string name, string data)
    {
        using var document = JsonDocument.Parse(data);
        var fixture = document.RootElement;
        var images = RichText.ParseMessageBody(Convert.FromBase64String(
            fixture.GetProperty("bodyBase64").GetString()!), fixture.GetProperty("isGroup").GetBoolean())
            .Where(segment => segment.Type == "image").ToArray();
        var expected = fixture.GetProperty("images").EnumerateArray().ToArray();
        Assert.True(expected.Length == images.Length,
            $"{name}: official images={expected.Length}, QQListener images={images.Length}");
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].GetProperty("url").GetString(), images[index].Url);
            Assert.Equal(expected[index].GetProperty("md5").GetString(), images[index].Md5.ToLowerInvariant());
        }
    }
}
