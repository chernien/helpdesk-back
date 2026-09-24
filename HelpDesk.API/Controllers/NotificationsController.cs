using HelpDesk.API.Models;
using HelpDesk.API.Repositories;
using HelpDesk.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.API.Controllers;

public class NotificationPageDto
{
    public int Total { get; set; }
    public List<NotificationHisto> Items { get; set; } = new();
}

public class ClocheItemDto
{
    public NotificationHisto Evenement { get; set; } = null!;
    public bool Lue { get; set; }
}

public class ClocheDto
{
    public int NonLues { get; set; }
    public List<ClocheItemDto> Items { get; set; } = new();
}

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private const int MaxCloche = 15;

    private readonly INotificationRepository _notificationRepository;
    private readonly IUtilisateurContexteService _contexte;

    public NotificationsController(INotificationRepository notificationRepository, IUtilisateurContexteService contexte)
    {
        _notificationRepository = notificationRepository;
        _contexte = contexte;
    }

    /// <summary>Historique complet, du plus recent au plus ancien, dans le perimetre de l'utilisateur.</summary>
    [HttpGet]
    public async Task<ActionResult<NotificationPageDto>> GetNotifications(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 15)
    {
        var user = await _contexte.ChargerAsync(User);
        var filter = new NotificationFilter
        {
            Search = search,
            Page = Math.Max(1, page),
            PageSize = Math.Clamp(pageSize, 1, 100)
        };

        var (items, total) = await _notificationRepository.SearchAsync(filter, user);
        return Ok(new NotificationPageDto { Total = total, Items = items });
    }

    /// <summary>Contenu de la cloche : nombre de non lues + dernieres notifications.</summary>
    [HttpGet("cloche")]
    public async Task<ActionResult<ClocheDto>> Cloche()
    {
        var user = await _contexte.ChargerAsync(User);
        var luesLe = await _notificationRepository.LuesLeAsync(user.Id);

        var items = await _notificationRepository.DernieresAsync(user, MaxCloche);
        return Ok(new ClocheDto
        {
            NonLues = await _notificationRepository.CompterNonLuesAsync(user, luesLe),
            Items = items.Select(n => new ClocheItemDto
            {
                Evenement = n,
                Lue = luesLe.HasValue && n.DateEvent <= luesLe.Value
            }).ToList()
        });
    }

    /// <summary>Marque toutes les notifications actuelles comme lues.</summary>
    [HttpPost("marquer-lues")]
    public async Task<IActionResult> MarquerLues()
    {
        var user = await _contexte.ChargerAsync(User);
        await _notificationRepository.MarquerLuesAsync(user.Id, DateTime.Now);
        return NoContent();
    }
}
