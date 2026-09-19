using TypeSafeSdk;

namespace TypeSafeSdk.Tests;
public class TypeSafeSdkTests
{
    [Theory]
    [InlineData("I was charged twice", "billing")]
    [InlineData("The application has a bug", "technical")]
    [InlineData("Please update my address", "other")]
    public void Simulator_ClassifiesTicket(string text, string expected) => Assert.Equal(expected, TypeSafeSimulator.Classify(text).Choices["category"].Choice);

    [Fact] public void Choice_RequiresValues() => Assert.Throws<ArgumentException>(() => Choice.Create());

    [Fact] public void EnvironmentOptions_ReadsValues()
    {
        Environment.SetEnvironmentVariable("TYPESAFE_API_KEY", "test-key"); Environment.SetEnvironmentVariable("TYPESAFE_SIMULATOR", "true");
        var options = TypeSafeOptions.FromEnvironment();
        Assert.Equal("test-key", options.ApiKey); Assert.True(options.Simulator);
    }

    [Fact] public async Task Client_Simulator_ReturnsResponse()
    {
        await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
        var result = await client.SystemOneAsync(new { document = "billing issue" }, Choice.Create("billing", "technical", "other"));
        Assert.Equal("billing", result.Choices["category"].Choice);
    }
}
