namespace SyntaxCircus.Blazor.Auth;

public readonly record struct ServerRequestOidcTokenResolution(string? Token, string? Subject, string? CacheKey, bool IsExpired);
