using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Reminders.Data;
using Reminders.Models;

namespace Reminders.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/push")]
public class PushController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly ILogger<PushController> _logger;

    public PushController(AppDbContext db, UserManager<AppUser> userManager, ILogger<PushController> logger)
    {
        _db = db;
        _userManager = userManager;
        _logger = logger;
    }

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe([FromBody] PushSubscribeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Endpoint) ||
            string.IsNullOrWhiteSpace(request.P256dh) ||
            string.IsNullOrWhiteSpace(request.Auth))
            return BadRequest("Invalid subscription data");

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        // Avoid duplicate subscriptions
        var existing = _db.PushSubscriptions.FirstOrDefault(s => s.UserId == user.Id && s.Endpoint == request.Endpoint);
        if (existing != null)
        {
            existing.P256dh = request.P256dh;
            existing.Auth = request.Auth;
        }
        else
        {
            _db.PushSubscriptions.Add(new PushSubscriptionRecord
            {
                UserId = user.Id,
                Endpoint = request.Endpoint,
                P256dh = request.P256dh,
                Auth = request.Auth,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Push subscription saved for user {UserId}", user.Id);
        return Ok();
    }

    [HttpDelete("unsubscribe")]
    public async Task<IActionResult> Unsubscribe([FromBody] PushUnsubscribeRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var sub = _db.PushSubscriptions.FirstOrDefault(s => s.UserId == user.Id && s.Endpoint == request.Endpoint);
        if (sub != null)
        {
            _db.PushSubscriptions.Remove(sub);
            await _db.SaveChangesAsync();
        }

        return Ok();
    }
}

public class PushSubscribeRequest
{
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
}

public class PushUnsubscribeRequest
{
    public string Endpoint { get; set; } = string.Empty;
}
