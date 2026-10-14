using System.Net;
using HelpDesk.API.Data;
using HelpDesk.API.Models;
using HelpDesk.API.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Services;

/// <summary>
/// Résumé quotidien des discussions. Une fois par jour, envoie à chaque participant UN e-mail
/// par ticket regroupant les messages postés par d'autres, non lus dans l'app et non encore
/// notifiés. Le repère <see cref="MessageLu.DerniereNotifLe"/> évite de renvoyer deux fois les
/// mêmes messages (on retient jusqu'où on a déjà notifié).
/// </summary>
public class DigestDiscussionService : BackgroundService
{
    /// <summary>Sentinelle « jamais » (repère absent) : antérieure à tout message réel.</summary>
    private static readonly DateTime Jamais = new(1900, 1, 1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<DigestDiscussionService> _logger;

    public DigestDiscussionService(IServiceScopeFactory scopes, IConfiguration config, ILogger<DigestDiscussionService> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Heure d'envoi quotidien (0–23), configurable ; 7h par défaut.
        var heure = int.TryParse(_config["Discussion:HeureResumeQuotidien"], out var h) && h is >= 0 and <= 23 ? h : 7;

        while (!stop.IsCancellationRequested)
        {
            var maintenant = DateTime.Now;
            var prochain = maintenant.Date.AddHours(heure);
            if (prochain <= maintenant) prochain = prochain.AddDays(1);

            try { await Task.Delay(prochain - maintenant, stop); }
            catch (TaskCanceledException) { break; }
            if (stop.IsCancellationRequested) break;

            try { await EnvoyerResumesAsync(stop); }
            catch (Exception ex) { _logger.LogError(ex, "Résumé quotidien des discussions : échec du cycle."); }
        }
    }

    /// <summary>Calcule et envoie les résumés. Peut aussi être déclenché manuellement (test admin). Renvoie le nombre d'e-mails.</summary>
    public async Task<int> EnvoyerResumesAsync(CancellationToken stop = default)
    {
        using var scope = _scopes.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HelpDeskContext>();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var mail = scope.ServiceProvider.GetRequiredService<GraphMailService>();

        var appUrl = (_config["App:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/');
        var expediteur = _config["Outlook:ExpediteurNotifications"];
        var boite = string.IsNullOrWhiteSpace(expediteur) ? mail : mail.PourBoite(expediteur);

        // Tickets avec de l'activité récente (borne pour ne pas balayer tout l'historique).
        var depuis = DateTime.Now.AddDays(-30);
        var ticketIds = await ctx.Message.AsNoTracking()
            .Where(m => m.DateEnvoi >= depuis)
            .Select(m => m.TicketId).Distinct().ToListAsync(stop);

        var envoyes = 0;
        foreach (var ticketId in ticketIds)
        {
            if (stop.IsCancellationRequested) break;

            var ticket = await ctx.TicketList.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, stop);
            if (ticket == null) continue;

            var messages = await ctx.Message.AsNoTracking()
                .Where(m => m.TicketId == ticketId).OrderBy(m => m.DateEnvoi).ToListAsync(stop);
            if (messages.Count == 0) continue;

            // Participants (ids) : client, responsable, équipe produit, et auteurs du fil (admins inclus).
            var ids = new HashSet<int>();
            if (ticket.ClientId.HasValue) ids.Add(ticket.ClientId.Value);
            if (ticket.CollaborateurId.HasValue) ids.Add(ticket.CollaborateurId.Value);
            foreach (var i in await users.IdsUsersDuProduitAsync(ticket.Produit ?? "")) ids.Add(i);
            foreach (var m in messages) ids.Add(m.AuteurId);
            if (ids.Count == 0) continue;

            var comptes = (await users.GetByIdsAsync(ids)).ToDictionary(c => c.Id);
            // Repères SUIVIS (tracking) : on met à jour DerniereNotifLe directement.
            var reperes = (await ctx.MessageLu.Where(l => l.TicketId == ticketId).ToListAsync(stop))
                .ToDictionary(r => r.UserId);

            foreach (var uid in ids)
            {
                if (!comptes.TryGetValue(uid, out var compte)) continue;
                var email = compte.Username?.Trim();
                if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) continue;

                reperes.TryGetValue(uid, out var repere);
                var lu = repere?.DerniereLectureLe ?? Jamais;
                var notif = repere?.DerniereNotifLe ?? Jamais;
                var seuil = lu > notif ? lu : notif;   // ni déjà lu dans l'app, ni déjà notifié

                var nouveaux = messages.Where(m => m.AuteurId != uid && m.DateEnvoi > seuil).ToList();
                if (nouveaux.Count == 0) continue;

                var objet = $"[HelpDesk] Résumé du jour — Ticket #{ticket.Id} — {ticket.Intitule}";
                var html = ConstruireHtml(ticket, nouveaux, appUrl);
                try
                {
                    await boite.EnvoyerHtmlAsync(new List<string> { email }, objet, html);
                    envoyes++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Résumé ticket #{Id} : e-mail non envoyé à {Email} (on retentera).", ticket.Id, email);
                    continue; // repère non mis à jour → retenté au prochain cycle
                }

                // On a notifié jusqu'au dernier message inclus.
                var derniere = nouveaux[^1].DateEnvoi;
                if (repere == null)
                {
                    repere = new MessageLu { UserId = uid, TicketId = ticketId, DerniereLectureLe = Jamais, DerniereNotifLe = derniere };
                    ctx.MessageLu.Add(repere);
                    reperes[uid] = repere;
                }
                else
                {
                    repere.DerniereNotifLe = derniere;
                }
                await ctx.SaveChangesAsync(stop);
            }
        }

        _logger.LogInformation("Résumé quotidien des discussions : {N} e-mail(s) envoyé(s).", envoyes);
        return envoyes;
    }

    private static string ConstruireHtml(TicketList ticket, List<Message> messages, string appUrl)
    {
        var lignes = string.Join("", messages.Select(m =>
        {
            var auteur = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(m.AuteurNom) ? "Participant" : m.AuteurNom);
            var quand = m.DateEnvoi.ToString("dd/MM HH:mm");
            var corps = string.IsNullOrWhiteSpace(m.Contenu)
                ? (string.IsNullOrWhiteSpace(m.FichierNom) ? "" : $"📎 {WebUtility.HtmlEncode(m.FichierNom)}")
                : WebUtility.HtmlEncode(m.Contenu).Replace("\n", "<br/>");
            return $@"<div style=""margin:0 0 10px;padding:10px 12px;background:#f1f5f9;border-radius:10px;"">
                        <div style=""font-size:12px;color:#64748b;""><strong>{auteur}</strong> · {quand}</div>
                        <div style=""margin-top:4px;line-height:1.5;"">{corps}</div>
                      </div>";
        }));
        var lien = $"{appUrl}/tickets/{ticket.Id}";
        var intitule = WebUtility.HtmlEncode(ticket.Intitule ?? "");
        return $@"
<div style=""font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:auto;color:#1e293b;"">
  <div style=""background:#1769c0;color:#fff;padding:16px 20px;border-radius:12px 12px 0 0;"">
    <div style=""font-size:13px;opacity:.85;"">Résumé du jour — Ticket #{ticket.Id}</div>
    <div style=""font-size:17px;font-weight:600;margin-top:2px;"">{intitule}</div>
  </div>
  <div style=""border:1px solid #e2e8f0;border-top:none;border-radius:0 0 12px 12px;padding:20px;"">
    <p style=""margin:0 0 12px;color:#475569;"">{messages.Count} nouveau(x) message(s) non lu(s) :</p>
    {lignes}
    <p style=""margin:18px 0 0;"">
      <a href=""{lien}"" style=""background:#1769c0;color:#fff;text-decoration:none;padding:10px 18px;border-radius:8px;display:inline-block;font-weight:600;"">Ouvrir la discussion</a>
    </p>
  </div>
</div>";
    }
}
