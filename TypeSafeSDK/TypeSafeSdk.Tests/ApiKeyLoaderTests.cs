using TypeSafeSdk;

namespace TypeSafeSdk.Tests;
public sealed class ApiKeyLoaderTests
{
    [Theory]
    [InlineData("secret-raw-key", "secret-raw-key")]
    [InlineData("API_KEY=secret-key\n", "secret-key")]
    [InlineData("TYPESAFE_API_KEY: secret-key-2", "secret-key-2")]
    [InlineData("{\"api_key\":\"json-key\"}", "json-key")]
    public void LoadFromFile_ParsesSupportedFormats(string content, string expected)
    {
        var file = Path.GetTempFileName();
        try { File.WriteAllText(file, content); Assert.Equal(expected, TypeSafeApiKeyLoader.LoadFromFile(file)); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void LoadFromFile_RejectsUnknownFormat()
    {
        var file = Path.GetTempFileName();
        try { File.WriteAllText(file, "username=someone\npassword=secret"); Assert.Throws<InvalidDataException>(() => TypeSafeApiKeyLoader.LoadFromFile(file)); }
        finally { File.Delete(file); }
    }
}
