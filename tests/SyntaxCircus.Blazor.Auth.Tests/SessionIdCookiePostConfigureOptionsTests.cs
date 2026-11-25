using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace SyntaxCircus.Blazor.Auth.Tests;

/// <summary>The session-id sign-in hook against the real cookie handler and the ways a host can configure it.</summary>
public class SessionIdCookiePostConfigureOptionsTests
{
    private const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    [Fact]
    public async Task AddCookieAfterAddBlazorTokenForwarding_StillAssignsASessionId_AndRunsTheHostsHandler()
    {
        var calls = new CallLog();
        using var host = CookieSessionHost.Create(
            addDefaultCookie: false,
            configureAfter: services => services.AddAuthentication(Scheme).AddCookie(options =>
                options.Events.OnSigningIn = context => calls.Add("host")));

        var view = await SignInAndViewAsync(host);

        view.SessionIds.ShouldHaveSingleItem();
        calls.ShouldBe(["host"]);
    }

    [Fact]
    public async Task HostOnSigningInConfiguredBeforeAddBlazorTokenForwarding_StillRuns()
    {
        var calls = new CallLog();
        using var host = CookieSessionHost.Create(
            configureBefore: services => services.Configure<CookieAuthenticationOptions>(Scheme, options =>
                options.Events.OnSigningIn = context =>
                {
                    ((ClaimsIdentity)context.Principal!.Identity!).AddClaim(new Claim("added-by-host", "1"));
                    return calls.Add("host");
                }));

        var view = await SignInAndViewAsync(host);

        view.SessionIds.ShouldHaveSingleItem();
        view.ClaimTypes.ShouldContain("added-by-host");
        calls.ShouldBe(["host"]);
    }

    [Fact]
    public async Task HostAssigningANewEventsInstance_StillGetsASessionId()
    {
        var calls = new CallLog();
        using var host = CookieSessionHost.Create(
            configureAfter: services => services.Configure<CookieAuthenticationOptions>(Scheme, options =>
                options.Events = new CookieAuthenticationEvents { OnSigningIn = _ => calls.Add("host") }));

        var view = await SignInAndViewAsync(host);

        view.SessionIds.ShouldHaveSingleItem();
        calls.ShouldBe(["host"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EventsSubclassOverridingSigningIn_StillGetsASessionId(bool callsBase)
    {
        var calls = new CallLog();
        var events = new OverridingEvents(calls) { CallBase = callsBase };
        using var host = CookieSessionHost.Create(
            configureAfter: services => services.Configure<CookieAuthenticationOptions>(Scheme, options => options.Events = events));

        var view = await SignInAndViewAsync(host);

        view.SessionIds.ShouldHaveSingleItem();
        calls.ShouldContain("override:SigningIn");
        calls.ShouldContain("override:ValidatePrincipal");
    }

    [Theory]
    [InlineData(BaseCall.Never)]
    [InlineData(BaseCall.BeforeReplacing)]
    [InlineData(BaseCall.AfterReplacing)]
    public async Task EventsSubclassOverridingOnlyValidatePrincipal_KeepsTheSessionIdWhenItReplacesThePrincipal(BaseCall baseCall)
    {
        var events = new ReplacingEvents { CallBase = baseCall };
        using var host = CookieSessionHost.Create(
            configureAfter: services => services.Configure<CookieAuthenticationOptions>(Scheme, options => options.Events = events));
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 3600);
        var before = await browser.MeAsync();
        events.Replace = true;

        var during = await browser.MeAsync();
        events.Replace = false;
        var after = await browser.MeAsync();

        during.CacheKey.ShouldBe(before.CacheKey);
        after.CacheKey.ShouldBe(before.CacheKey);
        after.ClaimTypes.ShouldContain("replaced");
        host.Logs.LibraryWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task PrincipalSetInCheckSlidingExpiration_IsNotWhatTheHandlerRenews()
    {
        // The one other event that exposes a settable principal. The handler renews the ticket it read, so
        // an override of it cannot drop the session id and needs no forwarding.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var events = new SlidingReplacingEvents();
        using var host = CookieSessionHost.Create(
            configureAfter: services => services.Configure<CookieAuthenticationOptions>(Scheme, options =>
            {
                options.TimeProvider = clock;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
                options.SlidingExpiration = true;
                options.Events = events;
            }));
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 3600);
        var before = await browser.MeAsync();
        var originalCookie = browser.Cookies.Values.Single();
        events.Replace = true;

        clock.Advance(TimeSpan.FromMinutes(6));
        await browser.GetAsync("/me");
        events.Replace = false;

        browser.Cookies.Values.Single().ShouldNotBe(originalCookie);
        var after = await browser.MeAsync();
        after.CacheKey.ShouldBe(before.CacheKey);
        after.ClaimTypes.ShouldNotContain("replaced");
    }

    [Fact]
    public async Task NullEvents_AreTreatedAsTheHandlerWouldTreatThem()
    {
        using var host = CookieSessionHost.Create(
            configureAfter: services => services.Configure<CookieAuthenticationOptions>(Scheme, options => options.Events = null!));

        var view = await SignInAndViewAsync(host);

        view.SessionIds.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task EventsType_StillGetsASessionId_AndIsResolvedFromDependencyInjection()
    {
        var calls = new CallLog();
        using var host = CookieSessionHost.Create(
            configureBefore: services =>
            {
                services.AddSingleton(calls);
                services.AddScoped<DependencyInjectedEvents>();
                services.Configure<CookieAuthenticationOptions>(Scheme, options => options.EventsType = typeof(DependencyInjectedEvents));
            });

        var view = await SignInAndViewAsync(host);

        view.SessionIds.ShouldHaveSingleItem();
        calls.ShouldContain("override:SigningIn");
        calls.ShouldContain("override:ValidatePrincipal");
    }

    [Fact]
    public void ForwardedEvents_PassEveryCookieEventThroughToTheOriginalInstance()
    {
        var inner = new OverridingEvents(new CallLog());

        var forwarded = SessionIdCookiePostConfigureOptions.Forward(inner);

        var delegateProperties = typeof(CookieAuthenticationEvents).GetProperties()
            .Where(property => property.Name.StartsWith("On", StringComparison.Ordinal))
            .ToList();
        var virtualMethods = typeof(CookieAuthenticationEvents).GetMethods()
            .Where(method => method.IsVirtual && method.DeclaringType == typeof(CookieAuthenticationEvents))
            .ToList();
        delegateProperties.Count.ShouldBe(virtualMethods.Count);
        foreach (var property in delegateProperties)
        {
            var forwarder = (Delegate)property.GetValue(forwarded)!;
            forwarder.Target.ShouldBeSameAs(inner, property.Name);
            forwarder.Method.Name.ShouldBe(property.Name[2..]);
        }
    }

    [Fact]
    public async Task NonDefaultCookieScheme_AlsoGetsASessionId()
    {
        using var host = CookieSessionHost.Create(
            configureAfter: services => services.AddAuthentication().AddCookie("AppCookie"));

        var principal = await SignInDirectlyAsync(host, "AppCookie", NewPrincipal());

        principal.FindAll(CookieSessionHost.SessionIdClaimType).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task EventsInstanceSharedBetweenSchemes_IsWrappedOnce()
    {
        var calls = new CallLog();
        var shared = new CookieAuthenticationEvents { OnSigningIn = _ => calls.Add("host") };
        using var host = CookieSessionHost.Create(
            configureAfter: services =>
            {
                services.AddAuthentication().AddCookie("AppCookie", options => options.Events = shared);
                services.Configure<CookieAuthenticationOptions>(Scheme, options => options.Events = shared);
            });
        var monitor = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();

        monitor.Get(Scheme);
        var afterFirst = shared.OnSigningIn;
        monitor.Get("AppCookie");

        shared.OnSigningIn.ShouldBeSameAs(afterFirst);
        await SignInDirectlyAsync(host, "AppCookie", NewPrincipal());
        calls.ShouldBe(["host"]);
    }

    [Fact]
    public async Task ReusedPrincipalInstance_GetsADifferentSessionIdOnEachSignIn_AndIsNotMutated()
    {
        using var host = CookieSessionHost.Create();
        var reused = NewPrincipal();

        var first = await SignInDirectlyAsync(host, Scheme, reused);
        var second = await SignInDirectlyAsync(host, Scheme, reused);

        reused.FindFirst(CookieSessionHost.SessionIdClaimType).ShouldBeNull();
        first.FindFirstValue(CookieSessionHost.SessionIdClaimType)
            .ShouldNotBe(second.FindFirstValue(CookieSessionHost.SessionIdClaimType));
    }

    [Fact]
    public async Task SessionIds_AreRandom256BitValues()
    {
        using var host = CookieSessionHost.Create();

        var ids = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            ids.Add((await SignInDirectlyAsync(host, Scheme, NewPrincipal())).FindFirstValue(CookieSessionHost.SessionIdClaimType)!);
        }

        ids.ShouldBeUnique();
        ids.ShouldAllBe(id => System.Buffers.Text.Base64Url.DecodeFromChars(id).Length == 32);
    }

    [Fact]
    public async Task HostHandlerRebuildingThePrincipal_KeepsTheSessionIdOnReSign()
    {
        using var host = CookieSessionHost.Create(
            configureBefore: services => services.Configure<CookieAuthenticationOptions>(Scheme, options =>
                options.Events.OnSigningIn = context =>
                {
                    // Rebuilds from selected claims, which drops everything else.
                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity([context.Principal!.FindFirst("sub")!], "rebuilt"));
                    return Task.CompletedTask;
                }));
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 30);

        var refreshing = await browser.MeAsync();
        await browser.GetAsync("/resign");
        var after = await browser.MeAsync();

        after.CacheKey.ShouldBe(refreshing.CacheKey);
        after.SessionIds.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SlidingRenewal_KeepsTheSessionId()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var host = CookieSessionHost.Create(
            configureAfter: services => services.Configure<CookieAuthenticationOptions>(Scheme, options =>
            {
                options.TimeProvider = clock;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
                options.SlidingExpiration = true;
            }));
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 3600);
        var before = await browser.MeAsync();
        var originalCookie = browser.Cookies.Values.Single();

        clock.Advance(TimeSpan.FromMinutes(6));
        await browser.GetAsync("/me");

        browser.Cookies.Values.Single().ShouldNotBe(originalCookie);
        (await browser.MeAsync()).CacheKey.ShouldBe(before.CacheKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidatePrincipalReplacingThePrincipal_KeepsTheSessionId(bool renewCookie)
    {
        // What ASP.NET Core Identity's security-stamp validator does. A renewal does not go through SigningIn.
        var replace = false;
        using var host = CookieSessionHost.Create(
            configureBefore: services => services.Configure<CookieAuthenticationOptions>(Scheme, options =>
                options.Events.OnValidatePrincipal = context =>
                {
                    if (replace)
                    {
                        context.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity([context.Principal!.FindFirst("sub")!], "rebuilt")));
                        context.ShouldRenew = renewCookie;
                    }

                    return Task.CompletedTask;
                }));
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 3600);
        var before = await browser.MeAsync();
        replace = true;

        var during = await browser.MeAsync();
        replace = false;
        var after = await browser.MeAsync();

        during.CacheKey.ShouldBe(before.CacheKey);
        after.CacheKey.ShouldBe(before.CacheKey);
        host.Logs.LibraryWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task ValidatePrincipalRejectingThePrincipal_StillRejects()
    {
        var reject = false;
        using var host = CookieSessionHost.Create(
            configureBefore: services => services.Configure<CookieAuthenticationOptions>(Scheme, options =>
                options.Events.OnValidatePrincipal = context =>
                {
                    if (reject)
                    {
                        context.RejectPrincipal();
                    }

                    return Task.CompletedTask;
                }));
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 3600);
        reject = true;

        (await browser.MeAsync()).CacheKey.ShouldBeNull();
    }

    [Fact]
    public async Task ServerSideTicketStore_KeepsTheSessionIdAcrossARefresh()
    {
        var store = new InMemoryTicketStore();
        using var host = CookieSessionHost.Create(
            configureAfter: services => services.Configure<CookieAuthenticationOptions>(Scheme, options => options.SessionStore = store));
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 30);

        var refreshing = await browser.MeAsync();
        var after = await browser.MeAsync();

        after.CacheKey.ShouldNotBeNull().ShouldStartWith("user-session:");
        after.SessionIds.ShouldHaveSingleItem().ShouldBe(refreshing.SessionIds.ShouldHaveSingleItem());
    }

    [Fact]
    public async Task LaterPostConfigureThatDiscardsTheHook_IsReportedWhenTheTokenIsRefreshed()
    {
        using var host = CookieSessionHost.Create(
            configureAfter: services => services.PostConfigure<CookieAuthenticationOptions>(Scheme, options =>
                options.Events = new CookieAuthenticationEvents()));
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 30);

        var view = await browser.MeAsync();

        view.CacheKey.ShouldBe("user:user-1");
        host.Logs.LibraryWarnings.ShouldContain(message => message.Contains("sign-in hook did not run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaterPostConfigureThatCallsThePreviousHandler_KeepsTheHook()
    {
        using var host = CookieSessionHost.Create(
            configureAfter: services => services.PostConfigure<CookieAuthenticationOptions>(Scheme, options =>
            {
                var previous = options.Events.OnSigningIn;
                options.Events.OnSigningIn = context => previous(context);
            }));
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 30);

        var view = await browser.MeAsync();

        view.SessionIds.ShouldHaveSingleItem();
        host.Logs.LibraryWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClaimsTransformationThatDropsTheSessionId_IsReported_AndTheCookieKeepsItsId()
    {
        var drop = new DroppingClaimsTransformation();
        using var host = CookieSessionHost.Create(
            configureBefore: services => services.AddSingleton<IClaimsTransformation>(drop));
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 30);
        drop.Enabled = true;

        await browser.MeAsync();

        host.Logs.LibraryWarnings.ShouldContain(message => message.Contains("is missing the session id", StringComparison.Ordinal));
        drop.Enabled = false;
        (await browser.MeAsync()).SessionIds.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task LostSessionIdWarning_IsLoggedOncePerHost_AndNamesBothCauses()
    {
        for (var hostNumber = 0; hostNumber < 2; hostNumber++)
        {
            var drop = new DroppingClaimsTransformation();
            using var host = CookieSessionHost.Create(
                configureBefore: services => services.AddSingleton<IClaimsTransformation>(drop));
            var browser = host.NewBrowser();
            await browser.LoginAsync("user-1", "A", expiresIn: 3600);
            drop.Enabled = true;

            await browser.MeAsync();
            await browser.MeAsync();
            await browser.MeAsync();

            var warning = host.Logs.LibraryWarnings
                .Where(message => message.Contains("is missing the session id", StringComparison.Ordinal))
                .ShouldHaveSingleItem();
            warning.ShouldContain("IClaimsTransformation");
            warning.ShouldContain("ValidatePrincipal");
        }
    }

    [Fact]
    public async Task AddBlazorTokenForwardingCalledTwice_RegistersTheHookOnce()
    {
        using var host = CookieSessionHost.Create(
            configureAfter: services => services.AddBlazorTokenForwarding(new ConfigurationBuilder().Build()));

        host.Services.GetServices<IPostConfigureOptions<CookieAuthenticationOptions>>()
            .Count(options => options is SessionIdCookiePostConfigureOptions)
            .ShouldBe(1);
        (await SignInAndViewAsync(host)).SessionIds.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task CustomCacheKeyProvider_IsUsedWithThePrincipalOnTheRequestAndCircuitPaths()
    {
        using var host = CookieSessionHost.Create(
            configureAfter: services => services.AddSingleton<IUserTokenCacheKeyProvider, TenantKeyProvider>());

        var view = await SignInAndViewAsync(host);

        view.CacheKey.ShouldBe("tenant:user-1");
        view.PrincipalCacheKey.ShouldBe("tenant:user-1");
    }

    private static async Task<SessionView> SignInAndViewAsync(CookieSessionHost host)
    {
        var browser = host.NewBrowser();
        await browser.LoginAsync("user-1", "A", expiresIn: 3600);
        return await browser.MeAsync();
    }

    private static ClaimsPrincipal NewPrincipal() => new(new ClaimsIdentity([new Claim("sub", "user-1")], "oidc"));

    /// <summary>Signs in through the real handler and returns the principal read back from the issued cookie.</summary>
    private static async Task<ClaimsPrincipal> SignInDirectlyAsync(CookieSessionHost host, string scheme, ClaimsPrincipal principal)
    {
        using var scope = host.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        await context.SignInAsync(scheme, principal);

        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        var setCookie = context.Response.Headers.SetCookie.ToString();
        var value = setCookie.Split(';', 2)[0].Split('=', 2)[1];
        return options.TicketDataFormat.Unprotect(Uri.UnescapeDataString(value))!.Principal;
    }

    private sealed class CallLog : IEnumerable<string>
    {
        private readonly ConcurrentQueue<string> calls = new();

        public Task Add(string call)
        {
            calls.Enqueue(call);
            return Task.CompletedTask;
        }

        public IEnumerator<string> GetEnumerator() => calls.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private class OverridingEvents(CallLog calls) : CookieAuthenticationEvents
    {
        public bool CallBase { get; init; }

        public override Task SigningIn(CookieSigningInContext context)
        {
            calls.Add("override:SigningIn");
            return CallBase ? base.SigningIn(context) : Task.CompletedTask;
        }

        public override Task ValidatePrincipal(CookieValidatePrincipalContext context)
        {
            calls.Add("override:ValidatePrincipal");
            return Task.CompletedTask;
        }
    }

    private sealed class DependencyInjectedEvents(CallLog calls) : OverridingEvents(calls);

    private sealed class SlidingReplacingEvents : CookieAuthenticationEvents
    {
        public bool Replace { get; set; }

        public override Task CheckSlidingExpiration(CookieSlidingExpirationContext context)
        {
            if (Replace)
            {
                context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [context.Principal!.FindFirst("sub")!, new Claim("replaced", "1")], "rebuilt"));
                context.ShouldRenew = true;
            }

            return Task.CompletedTask;
        }
    }

    public enum BaseCall
    {
        Never,
        BeforeReplacing,
        AfterReplacing,
    }

    private sealed class ReplacingEvents : CookieAuthenticationEvents
    {
        public BaseCall CallBase { get; init; }

        public bool Replace { get; set; }

        public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
        {
            if (CallBase == BaseCall.BeforeReplacing)
            {
                await base.ValidatePrincipal(context);
            }

            if (Replace)
            {
                context.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(
                    [context.Principal!.FindFirst("sub")!, new Claim("replaced", "1")], "rebuilt")));
                context.ShouldRenew = true;
            }

            if (CallBase == BaseCall.AfterReplacing)
            {
                await base.ValidatePrincipal(context);
            }
        }
    }

    private sealed class DroppingClaimsTransformation : IClaimsTransformation
    {
        public bool Enabled { get; set; }

        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
            => Task.FromResult(Enabled && principal.Identity?.IsAuthenticated == true
                ? new ClaimsPrincipal(new ClaimsIdentity([principal.FindFirst("sub")!], "transformed"))
                : principal);
    }

    private sealed class TenantKeyProvider : IUserTokenCacheKeyProvider
    {
        public string? GetCacheKey(ClaimsPrincipal? principal)
            => principal?.FindFirstValue("sub") is { } subject ? $"tenant:{subject}" : null;

        public string? GetCacheKey(string? subject) => "wrong-overload";
    }

    private sealed class InMemoryTicketStore : ITicketStore
    {
        private readonly ConcurrentDictionary<string, byte[]> tickets = new(StringComparer.Ordinal);

        public Task<string> StoreAsync(AuthenticationTicket ticket)
        {
            var key = Guid.NewGuid().ToString("N");
            tickets[key] = TicketSerializer.Default.Serialize(ticket);
            return Task.FromResult(key);
        }

        public Task RenewAsync(string key, AuthenticationTicket ticket)
        {
            tickets[key] = TicketSerializer.Default.Serialize(ticket);
            return Task.CompletedTask;
        }

        public Task<AuthenticationTicket?> RetrieveAsync(string key)
            => Task.FromResult(tickets.TryGetValue(key, out var bytes) ? TicketSerializer.Default.Deserialize(bytes) : null);

        public Task RemoveAsync(string key)
        {
            tickets.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }
}
