namespace SyntaxCircus.Blazor.Auth.Tests;

public class ApiOptionsTests
{
    [Fact]
    public void Defaults_MatchExpectedValues()
    {
        var options = new ApiOptions();

        options.BaseUrl.ShouldBe(string.Empty);
        options.TimeoutSeconds.ShouldBe(30);
    }

    [Fact]
    public void SectionName_IsApi()
        => ApiOptions.SectionName.ShouldBe("Api");
}
