using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Reminders.Models;

namespace Reminders.Web.Controllers;

public class AccountController : Controller
{
    private readonly SignInManager<AppUser> _signInManager;

    public AccountController(SignInManager<AppUser> signInManager)
    {
        _signInManager = signInManager;
    }

    [HttpGet("/account/login")]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = string.IsNullOrWhiteSpace(returnUrl) ? "/reminders" : returnUrl;
        return View();
    }

    [HttpPost("/account/login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LoginPost([FromForm] string email, [FromForm] string password, [FromForm] bool rememberMe = false, [FromForm] string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ViewData["Error"] = "Email and password are required.";
            ViewData["ReturnUrl"] = string.IsNullOrWhiteSpace(returnUrl) ? "/reminders" : returnUrl;
            return View("Login");
        }

        var result = await _signInManager.PasswordSignInAsync(email.Trim(), password, rememberMe, lockoutOnFailure: false);
        if (!result.Succeeded)
        {
            ViewData["Error"] = "Invalid email or password.";
            ViewData["ReturnUrl"] = string.IsNullOrWhiteSpace(returnUrl) ? "/reminders" : returnUrl;
            return View("Login");
        }

        return LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) ? "/reminders" : returnUrl);
    }
}
