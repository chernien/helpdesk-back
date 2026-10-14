using HelpDesk.API.Constants;
using HelpDesk.API.DTOs;
using HelpDesk.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.API.Controllers;

/// <summary>Discussion (chat) d'un ticket + aperçu Messenger (messages non lus).</summary>
[ApiController]
[Route("api")]
[Authorize]
public class MessagesController : ControllerBase
{
    private readonly IMessageService _messageService;
    private readonly IUtilisateurContexteService _contexte;
    private readonly DigestDiscussionService _digest;

    public MessagesController(IMessageService messageService, IUtilisateurContexteService contexte, DigestDiscussionService digest)
    {
        _messageService = messageService;
        _contexte = contexte;
        _digest = digest;
    }

    /// <summary>Déclenche immédiatement le résumé quotidien des discussions (test admin).</summary>
    [HttpPost("messages/resume-quotidien")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<object>> DeclencherResume()
    {
        var n = await _digest.EnvoyerResumesAsync();
        return Ok(new { emailsEnvoyes = n });
    }

    /// <summary>Fil de la discussion d'un ticket.</summary>
    [HttpGet("tickets/{id:int}/messages")]
    public async Task<ActionResult<List<MessageDto>>> Fil(int id)
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _messageService.FilAsync(id, user));
    }

    /// <summary>Envoyer un message dans la discussion du ticket.</summary>
    [HttpPost("tickets/{id:int}/messages")]
    public async Task<ActionResult<MessageDto>> Envoyer(int id, EnvoyerMessageRequest request)
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _messageService.EnvoyerAsync(id, request.Contenu, user));
    }

    /// <summary>Envoyer un message avec une pièce jointe (10 Mo max). Le texte est facultatif.</summary>
    [HttpPost("tickets/{id:int}/messages/fichier")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<ActionResult<MessageDto>> EnvoyerAvecFichier(int id, [FromForm] string? contenu, IFormFile? fichier)
    {
        if (fichier == null)
            return BadRequest(new { message = "Aucun fichier reçu." });
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _messageService.EnvoyerAvecFichierAsync(id, contenu, fichier, user));
    }

    /// <summary>Télécharge la pièce jointe d'un message.</summary>
    [HttpGet("messages/{messageId:int}/fichier")]
    public async Task<IActionResult> TelechargerFichier(int messageId)
    {
        var user = await _contexte.ChargerAsync(User);
        var (flux, nom, type) = await _messageService.TelechargerFichierAsync(messageId, user);
        return File(flux, type, nom);
    }

    /// <summary>Supprime un message (son auteur, ou un administrateur).</summary>
    [HttpDelete("messages/{messageId:int}")]
    public async Task<IActionResult> Supprimer(int messageId)
    {
        var user = await _contexte.ChargerAsync(User);
        await _messageService.SupprimerAsync(messageId, user);
        return NoContent();
    }

    /// <summary>Marquer la discussion du ticket comme lue (repère individuel).</summary>
    [HttpPost("tickets/{id:int}/messages/lu")]
    public async Task<IActionResult> MarquerLu(int id)
    {
        var user = await _contexte.ChargerAsync(User);
        await _messageService.MarquerLuAsync(id, user);
        return NoContent();
    }

    /// <summary>Conversations avec messages non lus (menu Messenger + badge).</summary>
    [HttpGet("messages/apercu")]
    public async Task<ActionResult<MessagesApercuDto>> Apercu()
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _messageService.ApercuAsync(user));
    }
}
