namespace SyntaxCircus.Blazor.Auth.Tests;

public class SessionStateServiceTests
{
    [Fact]
    public void IsSessionExpired_InitiallyFalse()
        => new SessionStateService().IsSessionExpired.ShouldBeFalse();

    [Fact]
    public void MarkExpired_SetsIsSessionExpiredTrue()
    {
        var service = new SessionStateService();

        service.MarkExpired();

        service.IsSessionExpired.ShouldBeTrue();
    }

    [Fact]
    public void MarkExpired_RaisesOnSessionChanged()
    {
        var service = new SessionStateService();
        var raised = false;
        service.OnSessionChanged += () => raised = true;

        service.MarkExpired();

        raised.ShouldBeTrue();
    }

    [Fact]
    public void MarkExpired_AlreadyExpired_DoesNotRaiseAgain()
    {
        var service = new SessionStateService();
        service.MarkExpired();
        var raiseCount = 0;
        service.OnSessionChanged += () => raiseCount++;

        service.MarkExpired();

        raiseCount.ShouldBe(0);
    }

    [Fact]
    public void Clear_ResetsToNotExpired()
    {
        var service = new SessionStateService();
        service.MarkExpired();

        service.Clear();

        service.IsSessionExpired.ShouldBeFalse();
    }

    [Fact]
    public void Clear_RaisesOnSessionChangedWhenTransitioning()
    {
        var service = new SessionStateService();
        service.MarkExpired();
        var raised = false;
        service.OnSessionChanged += () => raised = true;

        service.Clear();

        raised.ShouldBeTrue();
    }

    [Fact]
    public void Clear_AlreadyNotExpired_DoesNotRaise()
    {
        var service = new SessionStateService();
        var raiseCount = 0;
        service.OnSessionChanged += () => raiseCount++;

        service.Clear();

        raiseCount.ShouldBe(0);
    }
}
