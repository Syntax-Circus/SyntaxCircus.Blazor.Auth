namespace SyntaxCircus.Blazor.Auth;

internal static class BlazorTokenRequestOptions
{
    internal static readonly HttpRequestOptionsKey<string> CacheKey = new("SyntaxCircus.Blazor.Auth.CacheKey");
}
