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
builder.Services.AddAuthentication()
    .AddGoogle(options =>
    {
        options.ClientId = builder.Configuration["Authentication:Google:ClientId"] ?? "placeholder";
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? "placeholder";
    })
    .AddOAuth("Apple", options =>
    {
        options.ClientId = builder.Configuration["Authentication:Apple:ClientId"] ?? "placeholder";
        options.ClientSecret = builder.Configuration["Authentication:Apple:ClientSecret"] ?? "placeholder";
        options.AuthorizationEndpoint = "https://appleid.apple.com/auth/authorize";
        options.TokenEndpoint = "https://appleid.apple.com/auth/token";
        options.CallbackPath = "/signin-apple";
    });

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
app.MapGet("/login", () => Results.Redirect("/account/login"));

app.MapPost("/login-local", async (HttpContext http, SignInManager<AppUser> signInManager) =>
{
    var form = await http.Request.ReadFormAsync();
    var email = form["email"].ToString();
    var password = form["password"].ToString();
    var remember = string.Equals(form["rememberMe"], "true", StringComparison.OrdinalIgnoreCase);
    var returnUrl = form["returnUrl"].ToString();
    if (string.IsNullOrWhiteSpace(returnUrl)) returnUrl = "/reminders";

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        return Results.Redirect("/login?error=Email%20and%20password%20are%20required.");

    var result = await signInManager.PasswordSignInAsync(email.Trim(), password, remember, lockoutOnFailure: false);
    if (!result.Succeeded)
        return Results.Redirect("/login?error=Invalid%20email%20or%20password.");

    return Results.Redirect(returnUrl);
});

app.MapGet("/challenge/{provider}", (string provider, string? returnUrl) =>
{
    var redirect = $"/externallogin-callback?returnUrl={Uri.EscapeDataString(returnUrl ?? "/")}";
    var props = new AuthenticationProperties { RedirectUri = redirect };
    return Results.Challenge(props, [provider]);
});

app.MapGet("/externallogin-callback", async (
    SignInManager<AppUser> signInManager,
    UserManager<AppUser> userManager,
    string? returnUrl) =>
{
    var info = await signInManager.GetExternalLoginInfoAsync();
    if (info is null)
        return Results.Redirect("/login?error=ExternalLoginFailed");

    var signInResult = await signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: true);
    if (signInResult.Succeeded)
        return Results.Redirect(returnUrl ?? "/reminders");

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
            return Results.Redirect("/login?error=AccountCreateFailed");
    }

    var addLogin = await userManager.AddLoginAsync(user, info);
    if (!addLogin.Succeeded && addLogin.Errors.Any(e => e.Code != "LoginAlreadyAssociated"))
        return Results.Redirect("/login?error=ExternalLinkFailed");

    await signInManager.SignInAsync(user, isPersistent: false);
    return Results.Redirect(returnUrl ?? "/reminders");
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

app.MapPost("/logout", async (SignInManager<AppUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Redirect("/");
});

// ── Blazor SSR ────────────────────────────────────────────────────────────────
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
