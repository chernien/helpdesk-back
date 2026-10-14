using System.Collections.Concurrent;

namespace HelpDesk.API.Services;

/// <summary>Activité de l'agent par utilisateur (depuis le démarrage de l'API) : pour l'affichage du statut.</summary>
public record AgentStatUtilisateur(DateTime? DerniereExecution, int EmailsTraites, int TicketsCrees, string? DerniereErreur, BlocageIa? Blocage);

/// <summary>Suivi en mémoire de l'activité de l'agent e-mail, par utilisateur (partagé, singleton).</summary>
public class AgentStatistiques
{
    private sealed record Etat(DateTime? Run, int Emails, int Tickets, string? Err, BlocageIa? Blocage);

    private readonly ConcurrentDictionary<int, Etat> _parUser = new();

    /// <summary>Enregistre un cycle : cumule e-mails/tickets, met à jour la date et la dernière erreur (null = OK).</summary>
    public void EnregistrerCycle(int userId, int emails, int tickets, string? erreur) =>
        _parUser.AddOrUpdate(userId,
            _ => new Etat(DateTime.Now, emails, tickets, erreur, null),
            (_, p) => p with { Run = DateTime.Now, Emails = p.Emails + emails, Tickets = p.Tickets + tickets, Err = erreur });

    /// <summary>Le modèle de l'utilisateur est bloqué (quota / indisponible). Vrai si c'est nouveau : on ne le prévient qu'une fois.</summary>
    public bool SignalerBlocage(int userId, BlocageIa blocage)
    {
        var nouveau = true;
        _parUser.AddOrUpdate(userId,
            _ => new Etat(null, 0, 0, null, blocage),
            (_, p) => { nouveau = p.Blocage != blocage; return p with { Blocage = blocage }; });
        return nouveau;
    }

    /// <summary>Le modèle répond de nouveau, ou l'utilisateur a changé de modèle / coupé l'agent.</summary>
    public void LeverBlocage(int userId)
    {
        if (_parUser.TryGetValue(userId, out var p) && p.Blocage != null)
            _parUser.TryUpdate(userId, p with { Blocage = null }, p);
    }

    public AgentStatUtilisateur Pour(int userId) =>
        _parUser.TryGetValue(userId, out var v)
            ? new AgentStatUtilisateur(v.Run, v.Emails, v.Tickets, v.Err, v.Blocage)
            : new AgentStatUtilisateur(null, 0, 0, null, null);
}
