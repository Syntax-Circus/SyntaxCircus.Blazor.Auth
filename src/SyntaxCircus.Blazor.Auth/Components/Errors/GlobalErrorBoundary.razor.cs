using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace SyntaxCircus.Blazor.Auth.Components.Errors;

public partial class GlobalErrorBoundary : IDisposable
{
    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Parameter, EditorRequired]
    public RenderFragment? ChildContent { get; set; }

    [Parameter]
    public string BoundaryName { get; set; } = "application UI";

    [Parameter]
    public string AppName { get; set; } = "the application";

    [Parameter]
    public string Title { get; set; } = "We hit an unexpected snag.";

    [Parameter]
    public string Description { get; set; } = "Something went wrong rendering this screen. Try again to recover without reloading.";

    [Parameter]
    public string RetryLabel { get; set; } = "Try again";

    [Parameter]
    public string? HomeHref { get; set; } = "/";

    [Parameter]
    public string HomeLabel { get; set; } = "Go home";

    private LoggingErrorBoundary? _errorBoundary;
    private int _renderVersion;

    protected override void OnInitialized()
    {
        Navigation.LocationChanged += HandleLocationChanged;
    }

    private Task RecoverAsync()
    {
        _renderVersion++;
        _errorBoundary?.Recover();
        return Task.CompletedTask;
    }

    private void HandleLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        _renderVersion++;
        _errorBoundary?.Recover();
    }

    public void Dispose()
    {
        Navigation.LocationChanged -= HandleLocationChanged;
        GC.SuppressFinalize(this);
    }
}
