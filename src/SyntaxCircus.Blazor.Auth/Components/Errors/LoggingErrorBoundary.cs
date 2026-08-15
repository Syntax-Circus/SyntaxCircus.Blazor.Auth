using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace SyntaxCircus.Blazor.Auth.Components.Errors;

public sealed class LoggingErrorBoundary : ErrorBoundary
{
    [Inject]
    private ILogger<LoggingErrorBoundary> Logger { get; set; } = default!;

    [Parameter]
    public string BoundaryName { get; set; } = "application UI";

    protected override Task OnErrorAsync(Exception exception)
    {
        Logger.LogError(exception, "Unhandled exception reached {BoundaryName}.", BoundaryName);
        return Task.CompletedTask;
    }
}
