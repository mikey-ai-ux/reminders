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
    [InlineData("/manifest.json")]
    [InlineData("/forgot-password")]
    [InlineData("/access-denied")]
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
    [InlineData("/profile/password")]
    [InlineData("/profile/delete")]
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

    [Fact]
    public async Task Pages_UseTheIconSet_NotEmoji()
    {
        var client = NewClient();
        await RegisterAsync(client);

        var anonymous = NewClient();
        foreach (var path in new[] { "/", "/reminders", "/history", "/profile", "/account/login", "/register" })
        {
            // A signed-in user is redirected away from the sign-in page, so fetch the public pages signed out.
            var html = await (path is "/account/login" or "/register" ? anonymous : client).GetStringAsync(path);
            Assert.Contains("<svg", html);
            Assert.DoesNotMatch(@"[\u2600-\u27BF]|\uD83C[\uDC00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDC00-\uDFFF]", html);
        }
    }

    [Fact]
    public async Task ForgotPassword_GivesTheSameAnswerForKnownAndUnknownEmails_AndNeverShowsTheLink()
    {
        var email = await RegisterAsync(NewClient());

        foreach (var address in new[] { email, "nobody@example.com" })
        {
            var client = NewClient();
            var page = await client.GetStringAsync("/forgot-password");
            var response = await client.PostAsync("/forgot-password", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "forgot-password",
                ["__RequestVerificationToken"] = HiddenField(page, "__RequestVerificationToken"),
                ["model.Email"] = address
            }));
            var html = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("a reset link is on its way", html);
            Assert.DoesNotContain("reset-password?userId=", html);
        }
    }

    [Fact]
    public async Task ResetPassword_WithAValidLink_ChangesThePassword()
    {
        const string newPassword = "brand-new-pass-7";
        var email = await RegisterAsync(NewClient());

        string userId, code;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();
            var user = await users.FindByEmailAsync(email);
            userId = user!.Id;
            code = await users.GeneratePasswordResetTokenAsync(user);
        }

        async Task<string> Submit(string token, string password)
        {
            var client = NewClient();
            var url = $"/reset-password?userId={Uri.EscapeDataString(userId)}&code={Uri.EscapeDataString(token)}";
            var page = await client.GetStringAsync(url);
            var response = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "reset-password",
                ["__RequestVerificationToken"] = HiddenField(page, "__RequestVerificationToken"),
                ["model.Password"] = password,
                ["model.ConfirmPassword"] = password
            }));
            return await response.Content.ReadAsStringAsync();
        }

        Assert.Contains("invalid or has expired", await Submit("not-a-real-token", newPassword));
        Assert.Contains("Your password has been changed.", await Submit(code, newPassword));

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(NewClient(), email, Password)).StatusCode); // old password rejected
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(NewClient(), email, newPassword)).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_NeedsTheCurrentPassword_AndKeepsTheUserSignedIn()
    {
        const string newPassword = "another-pass-42";
        var client = NewClient();
        var email = await RegisterAsync(client);

        async Task<string> Submit(string current)
        {
            var page = await client.GetStringAsync("/profile/password");
            var response = await client.PostAsync("/profile/password", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "change-password",
                ["__RequestVerificationToken"] = HiddenField(page, "__RequestVerificationToken"),
                ["model.CurrentPassword"] = current,
                ["model.NewPassword"] = newPassword,
                ["model.ConfirmPassword"] = newPassword
            }));
            return await response.Content.ReadAsStringAsync();
        }

        Assert.Contains("The current password is not correct.", await Submit("wrong-current-1"));
        Assert.Contains("Your password has been changed.", await Submit(Password));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/reminders")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(NewClient(), email, newPassword)).StatusCode);
    }

    private async Task<(int ReminderId, string Token)> AddReminderWithTokenAsync(string email, string interval = "None")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = (await db.Users.SingleAsync(u => u.Email == email)).Id;
        var reminder = new Reminder { UserId = userId, Name = "Take a break", OccursAt = DateTime.UtcNow.AddDays(1), RecurringInterval = interval };
        db.Reminders.Add(reminder);
        await db.SaveChangesAsync();
        var links = scope.ServiceProvider.GetRequiredService<Reminders.Web.Services.ReminderActionLinks>();
        return (reminder.Id, links.CreateToken(reminder));
    }

    private async Task<Reminder?> LoadReminderAsync(int id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Reminders.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id);
    }

    [Fact]
    public async Task NotificationLink_WorksWithoutSigningIn_ToSnoozeAndMarkDone()
    {
        var email = await RegisterAsync(NewClient());
        var (id, token) = await AddReminderWithTokenAsync(email);
        var anonymous = NewClient();

        var page = await anonymous.GetStringAsync($"/r/{token}");
        Assert.Contains("Take a break", page);
        Assert.Contains("Mark as done", page);
        Assert.Contains("In 10 minutes", page);

        // The buttons on the page post a form and get sent back to the page.
        var snooze = await anonymous.PostAsync($"/api/reminder-actions/{token}/snooze?minutes=60", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.Redirect, snooze.StatusCode);
        Assert.Equal($"/r/{Uri.EscapeDataString(token)}?result=snoozed", snooze.Headers.Location?.OriginalString);
        var snoozed = (await LoadReminderAsync(id))!.SnoozedUntil;
        Assert.NotNull(snoozed);
        Assert.InRange(snoozed.Value, DateTime.UtcNow.AddMinutes(58), DateTime.UtcNow.AddMinutes(62));
        Assert.Contains("Snoozed.", await anonymous.GetStringAsync(snooze.Headers.Location!.OriginalString));

        // A push notification's buttons call the same address in the background and get JSON.
        var done = await anonymous.PostAsync($"/api/reminder-actions/{token}/done", null);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Contains("\"ok\":true", await done.Content.ReadAsStringAsync());
        var after = await LoadReminderAsync(id);
        Assert.Null(after!.SnoozedUntil);
        Assert.False(after.IsActive);
    }

    [Fact]
    public async Task NotificationLink_ThatIsForgedOrForADeletedReminder_DoesNothing()
    {
        var email = await RegisterAsync(NewClient());
        var (id, token) = await AddReminderWithTokenAsync(email);
        var anonymous = NewClient();

        var forged = token[..^6] + (token.EndsWith("AAAAAA") ? "BBBBBB" : "AAAAAA");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsync($"/api/reminder-actions/{forged}/snooze?minutes=10", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsync("/api/reminder-actions/not-a-token/done", null)).StatusCode);
        Assert.Contains("This link no longer works", await anonymous.GetStringAsync($"/r/{forged}"));
        Assert.Null((await LoadReminderAsync(id))!.SnoozedUntil);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Reminders.Remove(await db.Reminders.SingleAsync(r => r.Id == id));
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsync($"/api/reminder-actions/{token}/done", null)).StatusCode);
        Assert.Contains("This link no longer works", await anonymous.GetStringAsync($"/r/{token}"));
    }

    [Fact]
    public async Task DeleteAccount_NeedsThePassword_ThenRemovesEverything_AndSignsOut()
    {
        var client = NewClient();
        var email = await RegisterAsync(client);
        var (reminderId, token) = await AddReminderWithTokenAsync(email);

        async Task<HttpResponseMessage> Submit(string confirmation)
        {
            var page = await client.GetStringAsync("/profile/delete");
            return await client.PostAsync("/profile/delete", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "delete-account",
                ["__RequestVerificationToken"] = HiddenField(page, "__RequestVerificationToken"),
                ["model.Confirmation"] = confirmation
            }));
        }

        var wrong = await Submit("not-my-password-1");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("That password is not correct.", await wrong.Content.ReadAsStringAsync());
        Assert.NotNull(await LoadReminderAsync(reminderId));

        var deleted = await Submit(Password);
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.Equal("/?account=deleted", PathOf(deleted));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Users.AnyAsync(u => u.Email == email));
        }
        Assert.Null(await LoadReminderAsync(reminderId));

        Assert.Contains("Your account and all its data have been deleted.", await client.GetStringAsync("/?account=deleted"));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/reminders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(NewClient(), email, Password)).StatusCode); // can no longer sign in
        Assert.Equal(HttpStatusCode.NotFound, (await NewClient().PostAsync($"/api/reminder-actions/{token}/done", null)).StatusCode);
    }

    private static StringContent JsonBody(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
