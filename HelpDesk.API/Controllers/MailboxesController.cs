using HelpDesk.API.Constants;
using HelpDesk.API.DTOs;
using HelpDesk.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.API.Controllers;

/// <summary>Boîte mail personnelle de l'utilisateur (connexion IMAP/SMTP).</summary>
[ApiController]
[Route("api/mailbox")]
[Authorize(Roles = $"{Roles.Admin},{Roles.User},{Roles.Dev}")]
public class MailboxesController : ControllerBase
{
    private readonly IMailboxService _mailboxService;
    private readonly IUtilisateurContexteService _contexte;

    public MailboxesController(IMailboxService mailboxService, IUtilisateurContexteService contexte)
    {
        _mailboxService = mailboxService;
        _contexte = contexte;
    }

    /// <summary>État de la boîte connectée (sans aucun secret).</summary>
    [HttpGet]
    public async Task<ActionResult<MailboxDto>> Statut()
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _mailboxService.StatutAsync(user));
    }

    /// <summary>Connecte (ou remplace) la boîte de l'utilisateur. Teste les identifiants avant d'enregistrer.</summary>
    [HttpPost]
    public async Task<ActionResult<MailboxDto>> Connecter(MailboxConnectRequest request)
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _mailboxService.ConnecterAsync(request, user));
    }

    /// <summary>Déconnecte la boîte (supprime les identifiants).</summary>
    [HttpDelete]
    public async Task<IActionResult> Deconnecter()
    {
        var user = await _contexte.ChargerAsync(User);
        await _mailboxService.DeconnecterAsync(user);
        return NoContent();
    }

    /// <summary>URL Microsoft de connexion (OAuth délégué) : le front y redirige l'utilisateur.</summary>
    [HttpGet("oauth/url")]
    public ActionResult<object> OAuthUrl()
    {
        var state = Guid.NewGuid().ToString("N");
        return Ok(new { url = _mailboxService.UrlAutorisation(state), state });
    }

    /// <summary>Retour Microsoft : échange le code reçu et enregistre la boîte de l'utilisateur.</summary>
    [HttpPost("oauth/callback")]
    public async Task<ActionResult<MailboxDto>> OAuthCallback(OAuthCallbackRequest request)
    {
        var user = await _contexte.ChargerAsync(User);
        return Ok(await _mailboxService.ConnecterOAuthAsync(request.Code, user));
    }
}
