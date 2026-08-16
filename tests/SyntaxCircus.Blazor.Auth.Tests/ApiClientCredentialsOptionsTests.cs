namespace SyntaxCircus.Blazor.Auth.Tests;

public class ApiClientCredentialsOptionsTests
{
    [Fact]
    public void SectionName_IsApiClientCredentials()
        => ApiClientCredentialsOptions.SectionName.ShouldBe("Api:ClientCredentials");

    [Fact]
    public void IsConfigured_AllRequiredFieldsSet_ReturnsTrue()
    {
        var options = new ApiClientCredentialsOptions
        {
            TokenEndpoint = "https://auth.example.com/token",
            ClientId = "client1",
            ClientSecret = "secret1",
        };

        options.IsConfigured.ShouldBeTrue();
    }

    [Theory]
    [InlineData("", "client1", "secret1")]
    [InlineData("https://auth.example.com/token", "", "secret1")]
    [InlineData("https://auth.example.com/token", "client1", "")]
    [InlineData("   ", "client1", "secret1")]
    public void IsConfigured_MissingRequiredField_ReturnsFalse(string tokenEndpoint, string clientId, string clientSecret)
    {
        var options = new ApiClientCredentialsOptions
        {
            TokenEndpoint = tokenEndpoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
        };

        options.IsConfigured.ShouldBeFalse();
    }

    [Fact]
    public void IsConfigured_DefaultOptions_ReturnsFalse()
        => new ApiClientCredentialsOptions().IsConfigured.ShouldBeFalse();
}
