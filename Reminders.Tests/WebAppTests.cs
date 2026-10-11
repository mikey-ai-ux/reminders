using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Reminders.Business.Services;
using Reminders.Data;
using Reminders.Models;
using Reminders.Models.Enums;

namespace Reminders.Tests;

/// <summary>Hosts the real app in memory on SQLite, with the background scheduler switched off.</summary>
public sealed class ReminderAppFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();
        builder.UseEnvironment("Production");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

            var scheduler = services.Single(d => d.ImplementationType == typeof(ReminderScheduler));
            services.Remove(scheduler);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

public class WebAppTests : IClassFixture<ReminderAppFactory>
{
    private const string Password = "correct-horse-9";
    private readonly ReminderAppFactory _factory;

    public WebAppTests(ReminderAppFactory factory) => _factory = factory;

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost")
    });

    // Blazor redirects with an absolute URL, MVC and minimal APIs with a relative one.
    private static string? PathOf(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null) return null;
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    private static string HiddenField(string html, string name)
    {
        var match = Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"");
        if (!match.Success) match = Regex.Match(html, $"value=\"([^\"]*)\"[^>]*name=\"{Regex.Escape(name)}\"");
        Assert.True(match.Success, $"Hidden field '{name}' not found in page.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private async Task<string> RegisterAsync(HttpClient client, string? email = null)
    {
        email ??= $"user-{Guid.NewGuid():N}@example.com";
        var page = await client.GetStringAsync("/register");
        var response = await client.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "register",
            ["__RequestVerificationToken"] = HiddenField(page, "__RequestVerificationToken"),
            ["model.DisplayName"] = "Test User",
            ["model.Email"] = email,
            ["model.Password"] = Password,
            ["model.ConfirmPassword"] = Password
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/reminders", PathOf(response));
        return email;
    }

    private async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password, string? returnUrl = null)
    {
        var page = await client.GetStringAsync("/account/login");
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HiddenField(page, "__RequestVerificationToken"),
            ["email"] = email,
            ["password"] = password
        };
        if (returnUrl != null) fields["returnUrl"] = returnUrl;
        return await client.PostAsync("/account/login", new FormUrlEncodedContent(fields));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/register")]
    [InlineData("/account/login")]
    [InlineData("/css/app.css")]
    [InlineData("/js/push.js")]
    [InlineData("/sw.js")]
    [InlineData("/icon-192.png")]
    [InlineData("/badge-72.png")]
    public async Task PublicPagesAndAssets_Load(string path)
    {
        var response = await NewClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_Url_RedirectsToTheLoginForm_KeepingItsQuery()
    {
        // Used to throw AmbiguousMatchException: a Razor page and a minimal API both claimed /login.
        var response = await NewClient().GetAsync("/login?returnUrl=%2Fhistory&error=Nope");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/login?returnUrl=%2Fhistory&error=Nope", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task LoginForm_ShowsErrorFromQuery_AndNoUnconfiguredProviders()
    {
        var html = await NewClient().GetStringAsync("/account/login?error=External%20sign-in%20failed.");

        Assert.Contains("External sign-in failed.", html);
        Assert.DoesNotContain("/challenge/", html);
        Assert.Contains("href=\"/register\"", html);
    }

    [Theory]
    [InlineData("/reminders")]
    [InlineData("/history")]
    [InlineData("/profile")]
    [InlineData("/profile/subscription")]
    [InlineData("/profile/payment-history")]
    public async Task ProtectedPages_SendAnonymousVisitorsToLogin(string path)
    {
        var response = await NewClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task Register_SignsIn_AndEveryAccountPageRenders()
    {
        var client = NewClient();
        await RegisterAsync(client);

        foreach (var path in new[] { "/reminders", "/history", "/profile", "/profile/subscription", "/profile/payment-history" })
        {
            var response = await client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} returned {(int)response.StatusCode}");
            Assert.Contains("Logout", html);
        }
    }

    [Fact]
    public async Task Register_RejectsDuplicateEmail_AndMismatchedPasswords()
    {
        var client = NewClient();
        var email = await RegisterAsync(client);

        var other = NewClient();
        var page = await other.GetStringAsync("/register");
        var duplicate = await other.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "register",
            ["__RequestVerificationToken"] = HiddenField(page, "__RequestVerificationToken"),
            ["model.DisplayName"] = "Copycat",
            ["model.Email"] = email,
            ["model.Password"] = Password,
            ["model.ConfirmPassword"] = Password
        }));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Contains("already taken", await duplicate.Content.ReadAsStringAsync());

        page = await other.GetStringAsync("/register");
        var mismatch = await other.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "register",
            ["__RequestVerificationToken"] = HiddenField(page, "__RequestVerificationToken"),
            ["model.DisplayName"] = "Typo",
            ["model.Email"] = $"typo-{Guid.NewGuid():N}@example.com",
            ["model.Password"] = Password,
            ["model.ConfirmPassword"] = Password + "x"
        }));
        Assert.Equal(HttpStatusCode.OK, mismatch.StatusCode);
        Assert.Contains("Passwords do not match", await mismatch.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_Works_ThenLogoutEndsTheSession()
    {
        var email = await RegisterAsync(NewClient());

        var client = NewClient();
        var login = await LoginAsync(client, email, Password, "/history");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/history", login.Headers.Location?.OriginalString);

        var reminders = await client.GetAsync("/reminders");
        Assert.Equal(HttpStatusCode.OK, reminders.StatusCode);
        var html = await reminders.Content.ReadAsStringAsync();

        var logout = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HiddenField(html, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/reminders")).StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ShowsAnError()
    {
        var email = await RegisterAsync(NewClient());

        var response = await LoginAsync(NewClient(), email, "wrong-password-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Invalid email or password.", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("//evil.example/steal")]
    public async Task Login_NeverRedirectsOffSite(string returnUrl)
    {
        var email = await RegisterAsync(NewClient());

        var response = await LoginAsync(NewClient(), email, Password, returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/reminders", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ExternalLogin_ForUnconfiguredProvider_ReturnsToLoginWithAMessage()
    {
        var response = await NewClient().GetAsync("/challenge/Google?returnUrl=/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login?error=", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task PushApi_RequiresSignIn_AndStoresTheDeviceOnce()
    {
        var anonymous = await NewClient().PostAsync("/api/push/subscribe", JsonBody("""{"endpoint":"https://push.example/1","p256dh":"k","auth":"a"}"""));
        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);

        var client = NewClient();
        var email = await RegisterAsync(client);
        for (var i = 0; i < 2; i++)
        {
            var saved = await client.PostAsync("/api/push/subscribe", JsonBody("""{"endpoint":"https://push.example/1","p256dh":"k","auth":"a"}"""));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = (await db.Users.SingleAsync(u => u.Email == email)).Id;
        Assert.Equal(1, await db.PushSubscriptions.CountAsync(p => p.UserId == userId));
    }

    [Fact]
    public async Task CalendarFeed_ListsActiveReminders_WithRecurrenceRules()
    {
        var client = NewClient();
        var email = await RegisterAsync(client);

        string userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            userId = (await db.Users.SingleAsync(u => u.Email == email)).Id;
            var when = new DateTime(2030, 6, 3, 9, 0, 0, DateTimeKind.Utc); // a Monday
            db.Reminders.AddRange(
                new Reminder { UserId = userId, Name = "One, two; three", OccursAt = when, RecurringInterval = "None" },
                new Reminder { UserId = userId, Name = "Birthday", OccursAt = when, RecurringInterval = "Annually" },
                new Reminder { UserId = userId, Name = "Gym", OccursAt = when, RecurringInterval = "Custom:1,3" },
                new Reminder { UserId = userId, Name = "Paused one", OccursAt = when, IsPaused = true });
            await db.SaveChangesAsync();
        }

        var response = await NewClient().GetAsync($"/api/calendar/{userId}/reminders.ics");
        var ics = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.DoesNotContain("\n", ics.Replace("\r\n", ""));
        Assert.Contains("SUMMARY:One\\, two\\; three", ics);
        Assert.Contains("DTSTART:20300603T090000Z", ics);
        Assert.Contains("RRULE:FREQ=YEARLY", ics);
        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=MO,WE", ics);
        Assert.DoesNotContain("Paused one", ics);

        Assert.Equal(HttpStatusCode.NotFound, (await NewClient().GetAsync("/api/calendar/not-a-user/reminders.ics")).StatusCode);
    }

    [Fact]
    public async Task ContactEndpointConfirmationLink_WorksOnce_AndRejectsBadOrExpiredTokens()
    {
        var client = NewClient();
        var email = await RegisterAsync(client);

        int goodId, expiredId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = (await db.Users.SingleAsync(u => u.Email == email)).Id;
            var good = new UserContactEndpoint { UserId = userId, Channel = NotificationChannel.Email, Value = "work@example.com", VerificationToken = "good-token", VerificationExpiresAtUtc = DateTime.UtcNow.AddMinutes(30) };
            var expired = new UserContactEndpoint { UserId = userId, Channel = NotificationChannel.Email, Value = "old@example.com", VerificationToken = "old-token", VerificationExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1) };
            db.UserContactEndpoints.AddRange(good, expired);
            await db.SaveChangesAsync();
            (goodId, expiredId) = (good.Id, expired.Id);
        }

        async Task<string?> Confirm(int id, string token) =>
            (await NewClient().GetAsync($"/confirm-contact-endpoint?endpointId={id}&token={token}")).Headers.Location?.OriginalString;

        Assert.Equal("/profile?confirm=endpoint-failed", await Confirm(goodId, "wrong-token"));
        Assert.Equal("/profile?confirm=endpoint-failed", await Confirm(expiredId, "old-token"));
        Assert.Equal("/profile?confirm=endpoint-ok", await Confirm(goodId, "good-token"));
        Assert.Equal("/profile?confirm=endpoint-already", await Confirm(goodId, "good-token"));
        Assert.Equal("/profile?confirm=endpoint-missing", await Confirm(999999, "x"));
    }

    [Fact]
    public async Task StripeWebhook_RejectsUnsignedRequests()
    {
        var response = await NewClient().PostAsync("/api/stripe/webhook", JsonBody("""{"type":"checkout.session.completed"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static StringContent JsonBody(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
