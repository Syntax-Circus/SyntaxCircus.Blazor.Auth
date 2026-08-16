namespace SyntaxCircus.Blazor.Auth.Tests.Infrastructure;

/// <summary>Builds a minimal in-memory <see cref="TestServer"/> to exercise middleware/endpoints for real.</summary>
internal static class TestServerFactory
{
    public static TestServer Create(
        Action<IServiceCollection>? configureServices,
        Action<WebApplication> configureApp)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        configureApp(app);
        app.StartAsync().GetAwaiter().GetResult();

        return (TestServer)app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
    }
}
