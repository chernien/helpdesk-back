using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace HelpDesk.API.Hubs;

/// <summary>
/// Hub SignalR des notifications temps reel.
/// Chaque utilisateur connecte rejoint le groupe "user-{id}".
/// Les admins rejoignent aussi le groupe "admins".
/// </summary>
[Authorize]
public class NotificationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var role = Context.User?.FindFirstValue(ClaimTypes.Role);

        if (!string.IsNullOrEmpty(userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");

        if (role == "ROLE_ADMIN")
            await Groups.AddToGroupAsync(Context.ConnectionId, "admins");

        await base.OnConnectedAsync();
    }
}
