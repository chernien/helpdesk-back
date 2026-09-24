using HelpDesk.API.Constants;
using HelpDesk.API.DTOs;
using HelpDesk.API.Repositories;
using HelpDesk.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.API.Controllers;

[ApiController]
[Route("api/tickets")]
[Authorize]
public class TicketsController : ControllerBase
{
    private readonly ITicketService _ticketService;
    private readonly IUtilisateurContexteService _contexte;

    public TicketsController(ITicketService ticketService, IUtilisateurContexteService contexte)
    {
        _ticketService = ticketService;
        _contexte = contexte;
    }

    [HttpGet]
    public async Task<ActionResult<TicketPageDto>> GetTickets(
        [FromQuery] string? etat,
        [FromQuery] string? produit,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var user = await _contexte.ChargerAsync(User);
        var filter = new TicketFilter { Etat = etat, Produit = produit, Search = search, Page = page, PageSize = pageSize };
        return Ok(await _ticketService.GetTicketsAsync(user, filter));
    }

    /// <summary>Liste des états possibles (liste déroulante).</summary>
    [HttpGet("etats")]
    public ActionResult<string[]> GetEtats() => Ok(TicketEtat.Tous);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TicketDto>> GetTicket(int id)
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _ticketService.GetByIdAsync(id, user));
    }

    /// <summary>Fil de suivi : toutes les evolutions du ticket, avec les commentaires.</summary>
    [HttpGet("{id:int}/suivi")]
    public async Task<ActionResult<List<Models.NotificationHisto>>> GetSuivi(int id)
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _ticketService.SuiviAsync(id, user));
    }

    [HttpPost]
    public async Task<ActionResult<TicketDto>> CreateTicket(CreateTicketRequest request)
    {
        var user = await _contexte.ChargerAsync(User);
        var ticket = await _ticketService.CreateAsync(request, user);
        return CreatedAtAction(nameof(GetTicket), new { id = ticket.Id }, ticket);
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.User},{Roles.Dev}")]
    public async Task<ActionResult<TicketDto>> UpdateTicket(int id, UpdateTicketRequest request)
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _ticketService.UpdateAsync(id, request, user));
    }

    /// <summary>Joint un fichier au ticket (une seule pièce, 10 Mo max, tous rôles ayant accès au ticket).</summary>
    [HttpPost("{id:int}/fichier")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<ActionResult<TicketDto>> JoindreFichier(int id, IFormFile? fichier)
    {
        if (fichier == null)
            return BadRequest(new { message = "Aucun fichier reçu." });
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _ticketService.JoindreFichierAsync(id, fichier, user));
    }

    /// <summary>Télécharge (ou affiche) la pièce jointe du ticket.</summary>
    [HttpGet("{id:int}/fichier")]
    public async Task<IActionResult> TelechargerFichier(int id)
    {
        var user = await _contexte.ChargerAsync(User);
        var (flux, nom, type) = await _ticketService.TelechargerFichierAsync(id, user);
        return File(flux, type, nom);
    }

    /// <summary>Joint une capture à la dernière action de l'utilisateur dans le suivi (phase de test).</summary>
    [HttpPost("{id:int}/suivi/fichier")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.User},{Roles.Dev}")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> JoindreCaptureSuivi(int id, IFormFile? fichier)
    {
        if (fichier == null)
            return BadRequest(new { message = "Aucun fichier reçu." });
        var user = await _contexte.ChargerAsync(User);
        await _ticketService.JoindreCaptureSuiviAsync(id, fichier, user);
        return NoContent();
    }

    /// <summary>Capture jointe à un événement du suivi.</summary>
    [HttpGet("{id:int}/suivi/{evenementId:int}/fichier")]
    public async Task<IActionResult> TelechargerCaptureSuivi(int id, int evenementId)
    {
        var user = await _contexte.ChargerAsync(User);
        var (flux, nom, type) = await _ticketService.TelechargerCaptureSuiviAsync(id, evenementId, user);
        return File(flux, type, nom);
    }

    /// <summary>Décision du client sur un ticket en attente de validation : valider (clôture) ou annuler.</summary>
    [HttpPost("{id:int}/validation")]
    [Authorize(Roles = Roles.Client)]
    public async Task<ActionResult<TicketDto>> ValiderTicket(int id, ClientValidationRequest request)
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _ticketService.ValiderParClientAsync(id, request, user));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteTicket(int id)
    {
        await _ticketService.DeleteAsync(id);
        return NoContent();
    }
}
