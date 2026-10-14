using HelpDesk.API.Constants;
using HelpDesk.API.DTOs;
using HelpDesk.API.Exceptions;
using HelpDesk.API.Repositories;
using HelpDesk.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.API.Controllers;

[ApiController]
[Route("api/emails")]
[Authorize(Roles = $"{Roles.Admin},{Roles.User},{Roles.Dev}")]
public class EmailsController : ControllerBase
{
    private readonly IMailboxResolver _resolver;
    private readonly IUtilisateurContexteService _contexte;
    private readonly GraphMailService _graph;
    private readonly IConfiguration _config;
    private readonly ITicketRepository _tickets;

    public EmailsController(IMailboxResolver resolver, IUtilisateurContexteService contexte,
        GraphMailService graph, IConfiguration config, ITicketRepository tickets)
    {
        _resolver = resolver;
        _contexte = contexte;
        _graph = graph;
        _config = config;
        _tickets = tickets;
    }

    /// <summary>Boîte de l'utilisateur : sa boîte IMAP connectée, sinon la boîte partagée.</summary>
    private async Task<IMailbox> BoiteAsync() => await _resolver.PourAsync(await _contexte.ChargerAsync(User));

    /// <summary>Adresses d'expéditeur autorisées (configurées), ou la boîte partagée par défaut.</summary>
    private List<string> Expediteurs()
    {
        var liste = _config.GetSection("Outlook:Expediteurs").GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();
        var defaut = _config["Outlook:UserEmail"];
        if (liste.Count == 0 && !string.IsNullOrWhiteSpace(defaut)) liste.Add(defaut);
        return liste;
    }

    /// <summary>Liste des expéditeurs sélectionnables dans le formulaire « Nouveau message ».</summary>
    [HttpGet("expediteurs")]
    public ActionResult<IEnumerable<string>> GetExpediteurs() => Ok(Expediteurs());

    [HttpGet]
    public Task<IActionResult> GetEmails(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] string folder = "inbox",
        [FromQuery] bool nonLus = false) =>
        Graph(async () =>
        {
            var resultat = await (await BoiteAsync()).GetEmailsAsync(page, pageSize, search, folder, nonLus);
            await EnrichirTicketsAsync(resultat.Items);
            return Ok(resultat);
        });

    /// <summary>
    /// Renseigne TicketId par rapprochement e-mail ↔ ticket (Message-ID, fil, ou objet+expéditeur) :
    /// si un membre de l'équipe a déjà transformé cet e-mail en ticket, tous les destinataires (To/CC)
    /// voient le badge « Ticket #N » — même si l'e-mail est arrivé en copie séparée ou dans un fil.
    /// </summary>
    private async Task EnrichirTicketsAsync(IEnumerable<EmailSummaryDto> emails)
    {
        var liste = emails.ToList();
        var signatures = liste.ToDictionary(e => e,
            e => LienEmailTicket.Signature(e.MessageIdInternet, e.ThreadIdInternet, e.DeEmail, e.Objet, e.Date));

        var exploitables = signatures.Values.Where(s => s.EstExploitable).ToList();
        if (exploitables.Count == 0) return;

        var messageIds = exploitables.Where(s => s.MessageId != null).Select(s => s.MessageId!).Distinct().ToList();
        var threadIds = exploitables.Where(s => s.ThreadId != null).Select(s => s.ThreadId!).Distinct().ToList();
        var objets = exploitables.Where(s => s.Objet != null).Select(s => s.Objet!).Distinct().ToList();
        var expediteurs = exploitables.Where(s => s.Expediteur != null).Select(s => s.Expediteur!).Distinct().ToList();

        var candidats = await _tickets.CandidatsLienEmailAsync(messageIds, threadIds, objets, expediteurs);
        if (candidats.Count == 0) return;

        foreach (var e in liste)
        {
            var sig = signatures[e];
            if (!sig.EstExploitable) continue;
            var lie = candidats
                .Where(c => LienEmailTicket.Correspond(sig, c.MessageId, c.ThreadId, c.Expediteur, c.Objet, c.Date))
                .Select(c => (int?)c.Id)
                .DefaultIfEmpty(null)
                .Min();   // le 1er ticket créé fait foi
            if (lie != null) e.TicketId = lie;
        }
    }

    [HttpGet("dossiers")]
    public Task<IActionResult> GetDossiers() =>
        Graph(async () => Ok(await (await BoiteAsync()).GetDossiersAsync()));

    [HttpGet("{messageId}")]
    public Task<IActionResult> GetEmail(string messageId) =>
        Graph(async () =>
        {
            var email = await (await BoiteAsync()).GetEmailByIdAsync(messageId);
            await EnrichirTicketsAsync(new[] { email });
            return Ok(email);
        });

    [HttpGet("{messageId}/attachments/{attachmentId}")]
    public Task<IActionResult> TelechargerPieceJointe(string messageId, string attachmentId) =>
        Graph(async () =>
        {
            var (contenu, nom, type) = await (await BoiteAsync()).TelechargerPieceJointeAsync(messageId, attachmentId);
            return File(contenu, type, nom);
        });

    [HttpPatch("{messageId}/lu")]
    public Task<IActionResult> MarquerCommeLu(string messageId, [FromQuery] bool valeur = true) =>
        Graph(async () => { await (await BoiteAsync()).MarquerCommeLuAsync(messageId, valeur); return NoContent(); });

    [HttpPost("{messageId}/deplacer")]
    public Task<IActionResult> Deplacer(string messageId, EmailDeplacerRequest request) =>
        Graph(async () => { await (await BoiteAsync()).DeplacerAsync(messageId, request.DossierId); return NoContent(); });

    [HttpPost("{messageId}/repondre")]
    public Task<IActionResult> Repondre(string messageId, EmailRepondreRequest request) =>
        Graph(async () => { await (await BoiteAsync()).RepondreAsync(messageId, request); return NoContent(); });

    [HttpPost("{messageId}/transferer")]
    public Task<IActionResult> Transferer(string messageId, EmailEnvoyerRequest request) =>
        Graph(async () => { await (await BoiteAsync()).TransfererAsync(messageId, request); return NoContent(); });

    [HttpPost("envoyer")]
    public Task<IActionResult> Envoyer(EmailEnvoyerRequest request) =>
        Graph(async () =>
        {
            var from = request.From?.Trim();
            // Expéditeur choisi et autorisé → envoi depuis cette boîte (Graph app-only) ; sinon boîte de l'utilisateur.
            if (!string.IsNullOrWhiteSpace(from) &&
                Expediteurs().Any(e => string.Equals(e, from, StringComparison.OrdinalIgnoreCase)))
            {
                await _graph.PourBoite(from).EnvoyerAsync(request);
            }
            else
            {
                await (await BoiteAsync()).EnvoyerAsync(request);
            }
            return NoContent();
        });

    [HttpPost("{messageId}/ticket")]
    [Authorize(Roles = $"{Roles.Admin},{Roles.User}")]
    public Task<IActionResult> LierTicket(string messageId, EmailLierTicketRequest request) =>
        Graph(async () => { await (await BoiteAsync()).LierTicketAsync(messageId, request.TicketId); return NoContent(); });

    /// <summary>
    /// Erreur de la boîte (Graph/IMAP) → 502 { message }. Les règles métier (MetierException)
    /// passent au gestionnaire global, qui renvoie leur propre code HTTP.
    /// </summary>
    private async Task<IActionResult> Graph(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not MetierException)
        {
            return StatusCode(502, new { message = $"Erreur de la boîte mail : {ex.Message}" });
        }
    }
}
