using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Reminders.Business.Interfaces;
using Reminders.Business.Senders;
using Reminders.Business.Services;
using Reminders.Data;
using Reminders.Models;
using Reminders.Models.Settings;
using Reminders.Web.Components;
using Reminders.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

// ── Identity ──────────────────────────────────────────────────────────────────
builder.Services.AddIdentity<AppUser, IdentityRole>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.AccessDeniedPath = "/access-denied";
});

// ── External OAuth ────────────────────────────────────────────────────────────
// Providers are only registered once real credentials are configured, so the login page
// never offers a button that leads to a provider error page.
var externalAuth = new ExternalAuthProviders();
var authBuilder = builder.Services.AddAuthentication();

static bool IsConfigured(string? value) =>
    !string.IsNullOrWhiteSpace(value) && !value.Contains("YOUR_", StringComparison.OrdinalIgnoreCase) && value != "placeholder";

var googleId = builder.Configuration["Authentication:Google:ClientId"];
var googleSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (IsConfigured(googleId) && IsConfigured(googleSecret))
{
    authBuilder.AddGoogle(options =>
    {
        options.ClientId = googleId!;
        options.ClientSecret = googleSecret!;
    });
    externalAuth.Names.Add("Google");
}

var appleId = builder.Configuration["Authentication:Apple:ClientId"];
var appleSecret = builder.Configuration["Authentication:Apple:ClientSecret"];
if (IsConfigured(appleId) && IsConfigured(appleSecret))
{
    authBuilder.AddOAuth("Apple", options =>
    {
        options.ClientId = appleId!;
        options.ClientSecret = appleSecret!;
        options.AuthorizationEndpoint = "https://appleid.apple.com/auth/authorize";
        options.TokenEndpoint = "https://appleid.apple.com/auth/token";
        options.CallbackPath = "/signin-apple";
    });
    externalAuth.Names.Add("Apple");
}

builder.Services.AddSingleton(externalAuth);

// ── Settings ──────────────────────────────────────────────────────────────────
builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection("Smtp"));
builder.Services.Configure<TwilioSettings>(builder.Configuration.GetSection("Twilio"));
builder.Services.Configure<VapidSettings>(builder.Configuration.GetSection("Vapid"));
builder.Services.Configure<StripeSettings>(builder.Configuration.GetSection("Stripe"));

// ── Business Services ─────────────────────────────────────────────────────────
builder.Services.AddScoped<IReminderService, ReminderService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IEmailNotificationSender, EmailNotificationSender>();
builder.Services.AddScoped<ISmsNotificationSender, SmsNotificationSender>();
builder.Services.AddScoped<IVoiceNotificationSender, VoiceNotificationSender>();
builder.Services.AddScoped<IPushNotificationSender, PushNotificationSender>();
builder.Services.AddScoped<IVerificationSender, VerificationSender>();
builder.Services.AddSingleton<ReminderActionLinks>();
builder.Services.AddSingleton<IReminderActionLinks>(sp => sp.GetRequiredService<ReminderActionLinks>());
builder.Services.AddHostedService<ReminderScheduler>();

// ── MVC Controllers ───────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddControllersWithViews();

// ── Blazor ────────────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// ── Middleware ────────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ── API Endpoints ─────────────────────────────────────────────────────────────
app.MapControllers();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");
// /login is the one public login URL; the form itself is the server-rendered MVC view.
app.MapGet("/login", (HttpContext http) => Results.Redirect("/account/login" + http.Request.QueryString));

// Only ever redirect back into this site.
static string SafeLocalUrl(string? url, string fallback) =>
    !string.IsNullOrWhiteSpace(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\")
        ? url
        : fallback;

app.MapGet("/challenge/{provider}", (string provider, string? returnUrl, ExternalAuthProviders providers) =>
{
    var name = providers.Names.FirstOrDefault(n => string.Equals(n, provider, StringComparison.OrdinalIgnoreCase));
    if (name is null)
        return Results.Redirect("/login?error=" + Uri.EscapeDataString($"{provider} sign-in is not available."));

    var redirect = $"/externallogin-callback?returnUrl={Uri.EscapeDataString(SafeLocalUrl(returnUrl, "/reminders"))}";
    var props = new AuthenticationProperties { RedirectUri = redirect };
    return Results.Challenge(props, [name]);
});

app.MapGet("/externallogin-callback", async (
    SignInManager<AppUser> signInManager,
    UserManager<AppUser> userManager,
    string? returnUrl) =>
{
    returnUrl = SafeLocalUrl(returnUrl, "/reminders");
    var info = await signInManager.GetExternalLoginInfoAsync();
    if (info is null)
        return Results.Redirect("/login?error=" + Uri.EscapeDataString("External sign-in failed. Please try again."));

    var signInResult = await signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: true);
    if (signInResult.Succeeded)
        return Results.Redirect(returnUrl);

    var email = info.Principal.FindFirstValue(ClaimTypes.Email)
               ?? info.Principal.FindFirstValue("email")
               ?? $"{info.ProviderKey}@{info.LoginProvider}.external";

    var displayName = info.Principal.FindFirstValue(ClaimTypes.Name)
                   ?? info.Principal.Identity?.Name
                   ?? email;

    var user = await userManager.FindByEmailAsync(email);
    if (user is null)
    {
        user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            CreatedAt = DateTime.UtcNow
        };

        var create = await userManager.CreateAsync(user);
        if (!create.Succeeded)
            return Results.Redirect("/login?error=" + Uri.EscapeDataString("Could not create an account for this sign-in."));
    }

    var addLogin = await userManager.AddLoginAsync(user, info);
    if (!addLogin.Succeeded && addLogin.Errors.Any(e => e.Code != "LoginAlreadyAssociated"))
        return Results.Redirect("/login?error=" + Uri.EscapeDataString("Could not link this sign-in to your account."));

    await signInManager.SignInAsync(user, isPersistent: false);
    return Results.Redirect(returnUrl);
});

app.MapGet("/confirm-email", async (UserManager<AppUser> userManager, string userId, string code) =>
{
    var user = await userManager.FindByIdAsync(userId);
    if (user is null) return Results.Redirect("/profile?confirm=email-failed");

    var result = await userManager.ConfirmEmailAsync(user, code);
    return result.Succeeded
        ? Results.Redirect("/profile?confirm=email-ok")
        : Results.Redirect("/profile?confirm=email-failed");
});

app.MapGet("/confirm-contact-endpoint", async (AppDbContext db, int endpointId, string token) =>
{
    var endpoint = await db.UserContactEndpoints.FirstOrDefaultAsync(x => x.Id == endpointId);
    if (endpoint is null)
        return Results.Redirect("/profile?confirm=endpoint-missing");

    if (endpoint.IsConfirmed)
        return Results.Redirect("/profile?confirm=endpoint-already");

    if (string.IsNullOrWhiteSpace(endpoint.VerificationToken)
        || !string.Equals(endpoint.VerificationToken, token, StringComparison.Ordinal)
        || (endpoint.VerificationExpiresAtUtc.HasValue && endpoint.VerificationExpiresAtUtc.Value < DateTime.UtcNow))
    {
        return Results.Redirect("/profile?confirm=endpoint-failed");
    }

    endpoint.IsConfirmed = true;
    endpoint.VerificationToken = null;
    endpoint.VerificationExpiresAtUtc = null;
    await db.SaveChangesAsync();

    return Results.Redirect("/profile?confirm=endpoint-ok");
});

// ── Notification actions (snooze / mark done) ─────────────────────────────────
// Called from a push notification's buttons (fetch, expects JSON) and from the buttons on the
// /r/{token} page (form post, expects a redirect). The signed token is the only credential.
static IResult ActionResult(HttpRequest request, string token, bool ok, string result)
{
    if (request.HasFormContentType)
        return Results.Redirect(ok ? $"/r/{Uri.EscapeDataString(token)}?result={result}" : $"/r/{Uri.EscapeDataString(token)}");
    return ok ? Results.Ok(new { ok = true, result }) : Results.NotFound(new { ok = false });
}

app.MapPost("/api/reminder-actions/{token}/snooze", async (string token, int? minutes, HttpRequest request, ReminderActionLinks links, IReminderService reminders) =>
{
    var ok = links.TryReadToken(token, out var id, out var userId)
             && await reminders.SnoozeAsync(id, userId, TimeSpan.FromMinutes(Math.Clamp(minutes ?? 10, 1, 7 * 24 * 60)));
    return ActionResult(request, token, ok, "snoozed");
}).DisableAntiforgery();

app.MapPost("/api/reminder-actions/{token}/done", async (string token, HttpRequest request, ReminderActionLinks links, IReminderService reminders) =>
{
    var ok = links.TryReadToken(token, out var id, out var userId)
             && await reminders.MarkDoneAsync(id, userId, skipUpcoming: false);
    return ActionResult(request, token, ok, "done");
}).DisableAntiforgery();

app.MapPost("/logout", async (SignInManager<AppUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Redirect("/");
});

// ── Blazor SSR ────────────────────────────────────────────────────────────────
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Names of the external sign-in providers that have real credentials configured.</summary>
public sealed class ExternalAuthProviders
{
    public List<string> Names { get; } = [];
}

// Lets the test project host the app.
public partial class Program { }
