using System.Net;
using System.Text;
using System.Text.Json;
using TypeSafeSdk;

namespace TypeSafeSdk.Tests;

/// <summary>
/// Kontrak yang ditambahkan Python SDK 0.7.2–0.7.5: pertanyaan box, aset gambar lewat multipart,
/// <c>attach_assets</c>, kompresi zstd, angka non-finite, dan validasi sebelum jaringan.
/// </summary>
public sealed class UpstreamSyncTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [Fact]
    public void BoxSchema_EncodesWireShapeAndOmitsMissingInstructions()
    {
        Assert.Equal("""{"type":"box","instructions":"The dog"}""", JsonSerializer.Serialize(new BoxSchema("The dog").ToApiQuestion()));
        Assert.Equal("""{"type":"box"}""", JsonSerializer.Serialize(new BoxSchema().ToApiQuestion()));
    }

    [Fact]
    public void NoulSchema_MayDescribeAnOutcomeInsteadOfInstructions()
    {
        Assert.Equal("""{"type":"noul","criteria":{"true":"spam","false":null}}""", JsonSerializer.Serialize(new NoulSchema(null, "spam").ToApiQuestion()));
        Assert.Equal("""{"type":"noul","instructions":"Spam?"}""", JsonSerializer.Serialize(new NoulSchema("Spam?").ToApiQuestion()));
    }

    [Fact]
    public void BoxAnswer_IsParsedAndUnknownFieldsAreTolerated()
    {
        var response = SystemOneResponse.Parse("""
            {"model":"jev-preview","usage":{"input_tokens":1,"output_tokens":1},"assets_used":{"<photo>":{"times_used":1}},
             "answers":{"dog":{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.25,"right":0.75},"confidence":0.9,"extra":{"k":[1.0]}},
                        "cat":{"type":"box","box":{"top":0,"bottom":1,"left":0,"right":1},"confidence":null}}}
            """);
        var dog = response.Boxes["dog"];
        Assert.Same(dog, response.Answers["dog"]);
        Assert.Equal(new BoxCoordinates(0.1, 0.6, 0.25, 0.75), dog.Box);
        Assert.Equal(0.9, dog.Confidence);
        Assert.Null(response.Boxes["cat"].Confidence);
        Assert.Empty(response.Nouls);
        Assert.Equal((50, 20, 100, 100), dog.Box.ToPixels(200, 200));
    }

    public static TheoryData<object?, object, string> InvalidRequests => new()
    {
        { "x", new Dictionary<string, object>(), "At least one question" },
        { "x", new Dictionary<string, object> { ["rating"] = new { type = "score", instructions = "?", criteria = Array.Empty<string>() } }, "\"rating\" has no criteria" },
        { "x", new Dictionary<string, object> { ["label"] = new { type = "choice", criteria = new Dictionary<string, string?>() } }, "\"label\" has no criteria" },
        { "x", new Dictionary<string, object> { ["label"] = new { type = "choice" } }, "requires \"criteria\"" },
        { "x", new Dictionary<string, object> { ["spam"] = new NoulSchema(null) }, "\"spam\" has neither instructions nor criteria" },
        { "x", new Dictionary<string, object> { ["spam"] = new { type = "noul", criteria = new { @true = (string?)null, @false = (string?)null } } }, "\"spam\" has neither instructions nor criteria" },
        { "x", new Dictionary<string, object> { ["spam"] = new { type = "noul", criteria = "spam" } }, "\"spam\" criteria must be an object" },
        { "x", new Dictionary<string, object> { ["q"] = new { instructions = "?" } }, "nonempty string \"type\"" },
        { null, new Dictionary<string, object> { ["spam"] = new NoulSchema("Spam?") }, "State is required" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidRequest_IsRejectedBeforeTheNetwork(object? state, object questions, string message)
    {
        var handler = new RecordingHandler();
        await using var client = Client(handler);
        var error = await Assert.ThrowsAsync<TypeSafeException>(() => client.SystemOneAsync(state!, questions));
        Assert.Contains(message, error.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RawQuestions_WithFutureFieldsStillPassThrough()
    {
        var handler = new RecordingHandler();
        await using var client = Client(handler);
        await client.SystemOneAsync("hi", new Dictionary<string, object>
        {
            ["future"] = new { type = "aurora", nested = new { k = (string?)null } },
            ["box"] = new { type = "box", instructions = new { find = "the dog" } }
        });
        Assert.Contains("\"aurora\"", handler.Text);
        Assert.Contains("\"find\":\"the dog\"", handler.Text);
    }

    [Fact]
    public async Task NonFiniteNumbers_AreSentAsStrings()
    {
        var handler = new RecordingHandler();
        await using var client = Client(handler);
        await client.SystemOneAsync(new { value = double.NaN, arr = new[] { 1.0, double.PositiveInfinity, double.NegativeInfinity } },
            new Dictionary<string, object> { ["q"] = new NoulSchema("?") }, extraBody: new Dictionary<string, object?> { ["temperature"] = double.NaN });
        using var body = JsonDocument.Parse(handler.Text);
        Assert.Equal("NaN", body.RootElement.GetProperty("state").GetProperty("value").GetString());
        Assert.Equal("""[1,"Infinity","-Infinity"]""", body.RootElement.GetProperty("state").GetProperty("arr").GetRawText());
        Assert.Equal("NaN", body.RootElement.GetProperty("temperature").GetString());
    }

    [Theory]
    [InlineData(RequestCompression.Zstd, "zstd")]
    [InlineData(RequestCompression.None, null)]
    public async Task LargeBodies_AreCompressedOnlyWhenEnabled(RequestCompression compression, string? encoding)
    {
        var state = new string('x', TypeSafeConstants.MinCompressedBodyLength);
        var handler = new RecordingHandler();
        await using var client = Client(handler, o => o.Compression = compression);
        await client.SystemOneAsync(state, new Dictionary<string, object> { ["spam"] = new NoulSchema("Spam?") });
        Assert.Equal(encoding, handler.ContentEncoding);
        Assert.Equal(state, JsonDocument.Parse(handler.Text).RootElement.GetProperty("state").GetString());

        await client.Models.ListAsync();
        Assert.Null(handler.ContentEncoding);
    }

    [Fact]
    public async Task SmallBodies_AreNeverCompressed()
    {
        var handler = new RecordingHandler();
        await using var client = Client(handler);
        await client.SystemOneAsync("x", new Dictionary<string, object> { ["spam"] = new NoulSchema("Spam?") });
        Assert.Null(handler.ContentEncoding);
    }

    [Fact]
    public async Task RetriedAttempts_ResendTheSameCompressedBody()
    {
        var handler = new RecordingHandler { FailFirst = HttpStatusCode.ServiceUnavailable };
        await using var client = Client(handler, o => o.Retry = new RetryPolicy(1, TimeSpan.FromMilliseconds(1)));
        await client.SystemOneAsync(new string('x', 4096), new Dictionary<string, object> { ["spam"] = new NoulSchema("Spam?") });
        Assert.Equal(2, handler.Calls);
        Assert.Equal("zstd", handler.ContentEncoding);
        Assert.Equal(handler.RawBodies[0], handler.RawBodies[1]);
    }

    [Fact]
    public async Task AliasedAsset_IsUploadedAsMultipartPart()
    {
        var handler = new RecordingHandler();
        await using var client = Client(handler, o => o.Compression = RequestCompression.None);
        var photo = ImageAsset.FromBytes(Png);
        await client.SystemOneAsync("<photo>", new Dictionary<string, object> { ["dog"] = new BoxSchema("The dog") }, model: "jev-preview",
            assets: new Dictionary<string, Asset> { ["<photo>"] = photo });

        var parts = handler.Parts();
        Assert.Equal(["request", "asset-0"], parts.Select(p => p.Name));
        Assert.Equal("application/json", parts[0].ContentType);
        Assert.Equal("image/png", parts[1].ContentType);
        Assert.Equal("asset-0", parts[1].FileName);
        Assert.Equal(Png, parts[1].Content);
        using var request = JsonDocument.Parse(parts[0].Content);
        var entry = Assert.Single(request.RootElement.GetProperty("assets").EnumerateObject());
        Assert.Equal("<photo>", entry.Name);
        Assert.Equal("""{"type":"image","part":"asset-0"}""", entry.Value.GetRawText());
        Assert.Equal("<photo>", request.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task PlacedAsset_IsSentByItsReference()
    {
        var handler = new RecordingHandler();
        await using var client = Client(handler);
        var photo = ImageAsset.FromBytes(Png);
        await client.SystemOneAsync(new { photo, caption = "a dog" }, new Dictionary<string, object> { ["dog"] = new BoxSchema("The dog") });

        var parts = handler.Parts();
        using var request = JsonDocument.Parse(parts[0].Content);
        Assert.Equal(photo.Reference, request.RootElement.GetProperty("state").GetProperty("photo").GetString());
        Assert.True(request.RootElement.GetProperty("assets").TryGetProperty(photo.Reference, out _));
        Assert.Equal(2, parts.Count);
    }

    [Fact]
    public async Task AttachedAsset_IsSentOnlyWhileAttachedAndReferenced()
    {
        var handler = new RecordingHandler();
        await using var client = Client(handler);
        var photo = ImageAsset.FromBytes(Png);
        var unused = ImageAsset.FromBytes(Png);
        var questions = new Dictionary<string, object> { ["damaged"] = new NoulSchema("Is the product damaged?") };

        using (client.AttachAssets(photo, unused))
            await client.SystemOneAsync($"Product: {photo}", questions);
        var parts = handler.Parts();
        Assert.Equal(2, parts.Count);
        Assert.Contains(photo.Reference, Encoding.UTF8.GetString(parts[0].Content).Replace("\\u003C", "<").Replace("\\u003E", ">"));

        await client.SystemOneAsync($"Product: {photo}", questions);
        Assert.StartsWith("application/json", handler.ContentType);
    }

    [Fact]
    public async Task OverlappingAttachments_KeepAnAssetUntilTheLastOneEnds()
    {
        var handler = new RecordingHandler();
        await using var client = Client(handler);
        var photo = ImageAsset.FromBytes(Png);
        var outer = client.AttachAssets(photo);
        using (client.AttachAssets(photo)) { }
        await client.SystemOneAsync($"{photo}", new Dictionary<string, object> { ["q"] = new NoulSchema("?") });
        Assert.StartsWith("multipart/form-data", handler.ContentType);
        outer.Dispose();
        outer.Dispose();
        await client.SystemOneAsync($"{photo}", new Dictionary<string, object> { ["q"] = new NoulSchema("?") });
        Assert.StartsWith("application/json", handler.ContentType);
    }

    [Fact]
    public async Task OneImageUnderTwoNames_IsSentOncePerName()
    {
        var handler = new RecordingHandler();
        await using var client = Client(handler);
        var photo = ImageAsset.FromBytes(Png);
        await client.SystemOneAsync($"<a> and {photo}", new Dictionary<string, object> { ["q"] = new NoulSchema("?") },
            assets: new Dictionary<string, Asset> { ["<a>"] = photo });
        Assert.Equal(["request", "asset-0", "asset-1"], handler.Parts().Select(p => p.Name));
    }

    [Fact]
    public async Task ExtraBodyAssets_ReplaceSdkAssets()
    {
        var handler = new RecordingHandler();
        await using var client = Client(handler);
        await client.SystemOneAsync("<x>", new Dictionary<string, object> { ["q"] = new NoulSchema("?") },
            extraBody: new Dictionary<string, object?> { ["assets"] = new { x = new { type = "image", data_base64 = "AAAA", format = "png" } } },
            assets: new Dictionary<string, Asset> { ["<x>"] = ImageAsset.FromFile("does-not-exist.png") });
        Assert.StartsWith("application/json", handler.ContentType);
        Assert.Contains("\"data_base64\":\"AAAA\"", handler.Text);
    }

    [Fact]
    public async Task UnreadableAsset_FailsBeforeTheNetworkNamingItsAlias()
    {
        var handler = new RecordingHandler();
        await using var client = Client(handler);
        var error = await Assert.ThrowsAsync<TypeSafeException>(() => client.SystemOneAsync("<photo>",
            new Dictionary<string, object> { ["q"] = new NoulSchema("?") },
            assets: new Dictionary<string, Asset> { ["<photo>"] = ImageAsset.FromFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png")) }));
        Assert.StartsWith("Asset '<photo>': Image file", error.Message);
        Assert.Equal(0, handler.Calls);

        error = await Assert.ThrowsAsync<TypeSafeException>(() => client.SystemOneAsync("<e>",
            new Dictionary<string, object> { ["q"] = new NoulSchema("?") },
            assets: new Dictionary<string, Asset> { ["<e>"] = ImageAsset.FromBytes(Array.Empty<byte>()) }));
        Assert.Equal("Asset '<e>': Image data is empty.", error.Message);
    }

    [Fact]
    public async Task ImageAsset_FactoriesValidateAndPartsAreLabelledBySignature()
    {
        Assert.Throws<TypeSafeException>(() => ImageAsset.FromBase64("  "));
        Assert.Throws<TypeSafeException>(() => ImageAsset.FromBase64("not base64!"));
        Assert.Matches("^<image:[0-9a-f]{32}>$", ImageAsset.FromBase64(Convert.ToBase64String(Png) + "\n").ToString());
        Assert.NotEqual(ImageAsset.FromBytes(Png).Reference, ImageAsset.FromBytes(Png).Reference);

        var handler = new RecordingHandler();
        await using var client = Client(handler);
        await client.SystemOneAsync("<png> <jpg> <gif>", new Dictionary<string, object> { ["q"] = new NoulSchema("?") }, assets: new Dictionary<string, Asset>
        {
            ["<png>"] = ImageAsset.FromBase64(Convert.ToBase64String(Png)),
            ["<jpg>"] = ImageAsset.FromStream(new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0])),
            ["<gif>"] = ImageAsset.FromBytes("GIF89a"u8.ToArray())
        });
        Assert.Equal(["image/png", "image/jpeg", "application/octet-stream"], handler.Parts().Skip(1).Select(p => p.ContentType));
    }

    [Fact]
    public async Task Simulator_AnswersBoxWithoutPretendingToSee()
    {
        await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
        var response = await client.SystemOneAsync(ImageAsset.FromBytes(Png), new Dictionary<string, object> { ["dog"] = new BoxSchema("The dog") });
        Assert.Equal(new BoxCoordinates(0, 1, 0, 1), response.Boxes["dog"].Box);
        Assert.Equal(0, response.Boxes["dog"].Confidence);
    }

    private static TypeSafeClient Client(RecordingHandler handler, Action<TypeSafeOptions>? configure = null)
    {
        var options = new TypeSafeOptions { ApiKey = "secret", Endpoint = "https://example.test", Retry = new RetryPolicy(0) };
        configure?.Invoke(options);
        return new TypeSafeClient(options, new HttpClient(handler));
    }

    private sealed record Part(string Name, string? FileName, string ContentType, byte[] Content);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode? FailFirst { get; init; }
        public List<byte[]> RawBodies { get; } = [];
        public string? ContentType { get; private set; }
        public string? ContentEncoding { get; private set; }
        public byte[] Body { get; private set; } = [];
        public string Text => Encoding.UTF8.GetString(Body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var raw = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            RawBodies.Add(raw);
            ContentType = request.Content?.Headers.ContentType?.ToString();
            ContentEncoding = request.Content?.Headers.ContentEncoding.SingleOrDefault();
            Body = ContentEncoding == "zstd" ? new ZstdSharp.Decompressor().Unwrap(raw).ToArray() : raw;
            if (FailFirst is { } status && Calls == 1) return new HttpResponseMessage(status) { Content = new StringContent("busy") };
            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"models":[{"name":"jev-latest","description":"default"}]}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"model":"jev-latest","answers":{},"usage":{"input_tokens":1,"output_tokens":1}}""") };
        }

        /// <summary>Memecah body multipart menjadi part-nya; cukup untuk body yang ditulis SDK sendiri.</summary>
        public List<Part> Parts()
        {
            Assert.StartsWith("multipart/form-data; boundary=", ContentType);
            var delimiter = Encoding.ASCII.GetBytes("--" + ContentType!["multipart/form-data; boundary=".Length..]);
            var parts = new List<Part>();
            var position = IndexOf(Body, delimiter, 0);
            while (true)
            {
                var start = position + delimiter.Length;
                if (Body[start] == '-' && Body[start + 1] == '-') return parts;
                start += 2;
                var headerEnd = IndexOf(Body, "\r\n\r\n"u8.ToArray(), start);
                var headers = Encoding.ASCII.GetString(Body, start, headerEnd - start).Split("\r\n");
                var next = IndexOf(Body, delimiter, headerEnd);
                var content = Body[(headerEnd + 4)..(next - 2)];
                var disposition = headers.Single(h => h.StartsWith("Content-Disposition: "));
                string? Attribute(string key) => disposition.Split("; ").Where(a => a.StartsWith(key + "=")).Select(a => a[(key.Length + 2)..^1]).SingleOrDefault();
                parts.Add(new Part(Attribute("name")!, Attribute("filename"), headers.Single(h => h.StartsWith("Content-Type: "))["Content-Type: ".Length..], content));
                position = next;
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int from)
        {
            var index = haystack.AsSpan(from).IndexOf(needle);
            return index < 0 ? throw new InvalidOperationException("Malformed multipart body") : from + index;
        }
    }
}
