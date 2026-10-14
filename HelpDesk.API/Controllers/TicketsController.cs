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
    private readonly IUserService _userService;

    public TicketsController(ITicketService ticketService, IUtilisateurContexteService contexte, IUserService userService)
    {
        _ticketService = ticketService;
        _contexte = contexte;
        _userService = userService;
    }

    [HttpGet]
    public async Task<ActionResult<TicketPageDto>> GetTickets(
        [FromQuery] string? etat,
        [FromQuery] string? produit,
        [FromQuery] string? search,
        [FromQuery] string? membre,
        [FromQuery] DateOnly? depuis,
        [FromQuery] string? equipe,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var user = await _contexte.ChargerAsync(User);
        var filter = new TicketFilter
        {
            Etat = etat, Produit = produit, Search = search, Depuis = depuis, Page = page, PageSize = pageSize,
            // « Traités par » un membre de l'équipe : filtre réservé à l'admin.
            Membre = user.EstAdmin ? membre : null,
            // Client : ses tickets, ou (administrateur de sa société) ceux d'un membre de son équipe.
            Equipe = user.EstClient ? equipe : null
        };
        return Ok(await _ticketService.GetTicketsAsync(user, filter));
    }

    /// <summary>Liste des états possibles (liste déroulante).</summary>
    [HttpGet("etats")]
    public ActionResult<string[]> GetEtats() => Ok(TicketEtat.Tous);

    /// <summary>Tableau de bord : nombre de tickets visibles par état (une seule requête).</summary>
    [HttpGet("compteurs")]
    public async Task<ActionResult<Dictionary<string, int>>> GetCompteurs([FromQuery] string? membre, [FromQuery] DateOnly? depuis,
        [FromQuery] string? equipe)
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _ticketService.CompteursAsync(user, user.EstAdmin ? membre : null, depuis, user.EstClient ? equipe : null));
    }

    /// <summary>Client administrateur de sa société : les membres de son équipe, pour voir leurs tickets (vide sinon).</summary>
    [HttpGet("equipe")]
    [Authorize(Roles = Roles.Client)]
    public async Task<ActionResult<List<EquipierDto>>> GetEquipe()
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _ticketService.EquipeClientAsync(user));
    }

    /// <summary>
    /// Export des tickets cochés, sinon de tous ceux des filtres : PDF (logo, critères, synthèse par état, tableau)
    /// ou Excel (tableau de données seul), selon <c>format</c>.
    /// </summary>
    [HttpPost("export")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.User},{Roles.Dev}")]
    public async Task<IActionResult> Exporter(ExportTicketsRequest request)
    {
        var user = await _contexte.ChargerAsync(User);
        var fichier = await _ticketService.ExporterAsync(user, request);
        return File(fichier.Contenu, fichier.Type, $"tickets-{DateTime.Now:yyyyMMdd-HHmm}.{fichier.Extension}");
    }

    /// <summary>Membres de l'équipe (admin, collaborateur, développeur) : suggestions du filtre « traités par ».</summary>
    [HttpGet("membres")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<List<MembreEquipeDto>>> GetMembres() =>
        Ok(await _ticketService.MembresEquipeAsync());

    /// <summary>
    /// Périmètre (produits + modules) à proposer dans le formulaire de création :
    /// - client : son propre périmètre ;
    /// - admin / collaborateur : celui du compte client ciblé (?email=...).
    /// Listes vides = aucune restriction (toutes les valeurs).
    /// </summary>
    [HttpGet("perimetre-creation")]
    public async Task<ActionResult<PerimetreClientDto>> PerimetreCreation([FromQuery] string? email)
    {
        var user = await _contexte.ChargerAsync(User);
        if (user.EstClient)
            return Ok(new PerimetreClientDto
            {
                Produits = user.PerimetreClient.Select(p => p.Produit).ToList(),
                Modules = user.ModulesClient.ToList()
            });
        return Ok(await _userService.PerimetreParEmailAsync(email ?? ""));
    }

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

    /// <summary>Fusionne ce ticket (source) dans un autre (cible) : bascule suivi + discussion, clôture la source.</summary>
    [HttpPost("{id:int}/fusionner")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<TicketDto>> Fusionner(int id, FusionTicketRequest request)
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _ticketService.FusionnerAsync(id, request.CibleId, user));
    }

    /// <summary>Opération unique : rétro-remplit la signature e-mail des anciens tickets (anti-doublon rétroactif).</summary>
    [HttpPost("backfill-signatures")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<object>> BackfillSignatures()
    {
        var n = await _ticketService.BackfillSignaturesAsync();
        return Ok(new { misAJour = n });
    }

    /// <summary>Avertissement de doublon avant création manuelle : tickets récents du même client au sujet proche.</summary>
    [HttpPost("verifier-doublon")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.User},{Roles.Client}")]
    public async Task<ActionResult<List<DoublonTicketDto>>> VerifierDoublon(VerifierDoublonRequest request)
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _ticketService.VerifierDoublonAsync(request, user));
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
