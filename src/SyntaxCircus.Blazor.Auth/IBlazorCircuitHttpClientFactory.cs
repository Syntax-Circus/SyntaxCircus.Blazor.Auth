namespace SyntaxCircus.Blazor.Auth;

public interface IBlazorCircuitHttpClientFactory
{
    HttpClient CreateClient(string name);
}
