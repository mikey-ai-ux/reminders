using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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

app.MapPost("/logout", async (SignInManager<AppUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Redirect("/");
});

// ── Blazor SSR ────────────────────────────────────────────────────────────────
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
