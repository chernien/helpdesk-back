using HelpDesk.API.Constants;
using HelpDesk.API.Repositories;
using HelpDesk.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.API.Controllers;

public class AgentStatutDto
{
    /// <summary>Fonctionnalité disponible (une clé IA est configurée).</summary>
    public bool Disponible { get; set; }
    /// <summary>L'utilisateur a une boîte mail connectée (requis pour activer l'agent).</summary>
    public bool ABoite { get; set; }
    /// <summary>L'agent est actif sur sa boîte.</summary>
    public bool Actif { get; set; }
    /// <summary>Les tickets créés par l'agent sont auto-affectés à l'utilisateur.</summary>
    public bool AutoAssign { get; set; }
    /// <summary>Modèle IA choisi ; null = automatique (Gemini puis Groq, comme outlook-agent).</summary>
    public string? Modele { get; set; }
    /// <summary>Modèles proposés (pour basculer de l'un à l'autre).</summary>
    public IReadOnlyList<ModeleIa> Modeles { get; set; } = Array.Empty<ModeleIa>();
    /// <summary>Modèle bloqué (quota atteint / indisponible) : l'agent est en pause jusqu'au choix de l'utilisateur.</summary>
    public BlocageIa? Blocage { get; set; }
    /// <summary>Modèles momentanément écartés (quota / indisponibles) avec leur heure de reprise.</summary>
    public IReadOnlyList<ModeleEnPause> ModelesEnPause { get; set; } = Array.Empty<ModeleEnPause>();
    // Activité (depuis le démarrage de l'API).
    public DateTime? DerniereExecution { get; set; }
    public int EmailsTraites { get; set; }
    public int TicketsCrees { get; set; }
    public string? DerniereErreur { get; set; }
}

public class AgentActiverRequest
{
    public bool Actif { get; set; }
    public bool AutoAssign { get; set; }
    /// <summary>Modèle IA choisi ; null ou vide = automatique.</summary>
    public string? Modele { get; set; }
}

/// <summary>
/// Agent IA e-mail → ticket, par utilisateur. Chaque admin / collaborateur active ou désactive
/// l'agent sur SA propre boîte connectée et choisit son modèle IA. Les e-mails reçus deviennent
/// des tickets automatiquement.
/// </summary>
[ApiController]
[Route("api/agent")]
[Authorize(Roles = $"{Roles.Admin},{Roles.User}")]
public class AgentController : ControllerBase
{
    private readonly IUtilisateurContexteService _contexte;
    private readonly IMailboxRepository _mailboxes;
    private readonly AgentIaService _ia;
    private readonly AgentStatistiques _stats;

    public AgentController(IUtilisateurContexteService contexte, IMailboxRepository mailboxes,
        AgentIaService ia, AgentStatistiques stats)
    {
        _contexte = contexte;
        _mailboxes = mailboxes;
        _ia = ia;
        _stats = stats;
    }

    [HttpGet("statut")]
    public async Task<ActionResult<AgentStatutDto>> Statut()
    {
        var user = await _contexte.ChargerAsync(User);
        var mb = await _mailboxes.GetByUserAsync(user.Id);
        return Ok(await ConstruireAsync(user.Id, mb?.AgentActif ?? false, mb?.AgentAutoAssign ?? false, mb?.AgentModele, mb != null));
    }

    [HttpPost("activer")]
    public async Task<ActionResult<AgentStatutDto>> Activer(AgentActiverRequest request)
    {
        var user = await _contexte.ChargerAsync(User);
        var modele = string.IsNullOrWhiteSpace(request.Modele) ? null : request.Modele.Trim();
        if (modele != null && !await _ia.EstModeleConnuAsync(modele))
            return BadRequest(new { message = $"Modèle IA inconnu : {modele}." });

        var avant = await _mailboxes.GetByUserAsync(user.Id);
        var ok = await _mailboxes.DefinirAgentAsync(user.Id, request.Actif, request.AutoAssign, modele);
        if (!ok)
            return Conflict(new { message = "Connectez d'abord une boîte mail avant d'activer l'agent." });

        // Changer de modèle ou couper l'agent lève l'alerte ; si le nouveau modèle est bloqué à son tour,
        // le prochain cycle le redétecte et prévient de nouveau.
        if (!request.Actif || !string.Equals(avant?.AgentModele, modele, StringComparison.OrdinalIgnoreCase))
            _stats.LeverBlocage(user.Id);

        return Ok(await ConstruireAsync(user.Id, request.Actif, request.AutoAssign, modele, true));
    }

    private async Task<AgentStatutDto> ConstruireAsync(int userId, bool actif, bool autoAssign, string? modele, bool aBoite)
    {
        var s = _stats.Pour(userId);
        return new AgentStatutDto
        {
            Disponible = _ia.EstConfigure,
            ABoite = aBoite,
            Actif = actif,
            AutoAssign = autoAssign,
            Modele = modele,
            Modeles = await _ia.ModelesAsync(HttpContext.RequestAborted),
            // Un agent coupé n'est plus « en pause » : pas d'alerte à afficher.
            Blocage = actif ? s.Blocage : null,
            ModelesEnPause = _ia.ModelesEnPause(),
            DerniereExecution = s.DerniereExecution,
            EmailsTraites = s.EmailsTraites,
            TicketsCrees = s.TicketsCrees,
            DerniereErreur = s.DerniereErreur
        };
    }
}
