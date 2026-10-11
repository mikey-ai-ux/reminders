using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Reminders.Models;

namespace Reminders.Web.Controllers;

public class AccountController : Controller
{
    private const string DefaultReturnUrl = "/reminders";

    private readonly SignInManager<AppUser> _signInManager;
    private readonly ExternalAuthProviders _externalAuth;

    public AccountController(SignInManager<AppUser> signInManager, ExternalAuthProviders externalAuth)
    {
        _signInManager = signInManager;
        _externalAuth = externalAuth;
    }

    [HttpGet("/account/login")]
    public IActionResult Login(string? returnUrl = null, string? error = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(SafeReturnUrl(returnUrl));

        return LoginView(returnUrl, error);
    }

    [HttpPost("/account/login")]
    public async Task<IActionResult> LoginPost([FromForm] string? email, [FromForm] string? password, [FromForm] bool rememberMe = false, [FromForm] string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return LoginView(returnUrl, "Email and password are required.", email);

        var result = await _signInManager.PasswordSignInAsync(email.Trim(), password, rememberMe, lockoutOnFailure: true);
        if (result.IsLockedOut)
            return LoginView(returnUrl, "Too many failed attempts. Please try again in a few minutes.", email);
        if (!result.Succeeded)
            return LoginView(returnUrl, "Invalid email or password.", email);

        return LocalRedirect(SafeReturnUrl(returnUrl));
    }

    private ViewResult LoginView(string? returnUrl, string? error, string? email = null)
    {
        ViewData["Error"] = error;
        ViewData["Email"] = email;
        ViewData["ReturnUrl"] = SafeReturnUrl(returnUrl);
        ViewData["ExternalProviders"] = _externalAuth.Names;
        ViewData["Theme"] = Request.Cookies["theme"] is "light" ? "light" : "dark";
        return View("Login");
    }

    // LocalRedirect throws on anything that isn't a local path, so never pass it a caller-supplied absolute URL.
    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : DefaultReturnUrl;
}
