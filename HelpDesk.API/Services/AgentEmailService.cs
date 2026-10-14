using System.Text.RegularExpressions;
using HelpDesk.API.DTOs;
using HelpDesk.API.Hubs;
using HelpDesk.API.Repositories;
using Microsoft.AspNetCore.SignalR;

namespace HelpDesk.API.Services;

/// <summary>
/// Agent e-mail → ticket, PAR utilisateur. Périodiquement, pour chaque admin / collaborateur dont
/// l'agent est actif, examine les e-mails récents de sa boîte (fenêtre OutlookAgent:FenetreHeures,
/// lus ou non — l'état lu n'est jamais modifié), les analyse par IA et crée les tickets
/// automatiquement. Ignore les e-mails internes et automatiques (aucun e-mail n'est envoyé) et ceux
/// déjà liés à un ticket, et enregistre son activité.
/// Si le modèle IA est bloqué (quota atteint / indisponible), l'agent se met en pause, reprend
/// l'e-mail plus tard et prévient l'utilisateur en direct pour qu'il change de modèle ou coupe l'IA.
/// </summary>
public class AgentEmailService : BackgroundService
{
    /// <summary>Événement temps réel : modèle IA bloqué → popup côté utilisateur.</summary>
    public const string EvtAgentBloque = "agentBloque";

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<AgentEmailService> _logger;
    private readonly AgentStatistiques _stats;
    private readonly IHubContext<NotificationHub> _hub;

    public AgentEmailService(IServiceScopeFactory scopes, IConfiguration config,
        ILogger<AgentEmailService> logger, AgentStatistiques stats, IHubContext<NotificationHub> hub)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
        _stats = stats;
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var intervalle = TimeSpan.FromSeconds(
            int.TryParse(_config["OutlookAgent:IntervalleSecondes"], out var s) && s >= 30 ? s : 120);
        try { await Task.Delay(TimeSpan.FromSeconds(15), stop); } catch (TaskCanceledException) { return; }

        while (!stop.IsCancellationRequested)
        {
            try { await CycleAsync(stop); }
            catch (Exception ex) { _logger.LogError(ex, "Agent e-mail : échec du cycle."); }
            try { await Task.Delay(intervalle, stop); } catch (TaskCanceledException) { break; }
        }
    }

    private async Task CycleAsync(CancellationToken stop)
    {
        using var scope = _scopes.CreateScope();
        var ia = scope.ServiceProvider.GetRequiredService<AgentIaService>();
        if (!ia.EstConfigure) return;   // aucune clé IA → agent inactif (optionnel)

        var mailboxes = scope.ServiceProvider.GetRequiredService<IMailboxRepository>();
        var resolver = scope.ServiceProvider.GetRequiredService<IMailboxResolver>();
        var contexte = scope.ServiceProvider.GetRequiredService<IUtilisateurContexteService>();
        var tickets = scope.ServiceProvider.GetRequiredService<ITicketService>();
        var clients = scope.ServiceProvider.GetRequiredService<IClientService>();
        // Produits de la base : l'IA choisit le produit du ticket dans cette liste.
        var produits = (await scope.ServiceProvider.GetRequiredService<IProduitRepository>().GetAllAsync())
            .Select(p => p.Nom).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        // Modules de la base : l'IA choisit celui qui correspond au sujet de l'e-mail.
        var modules = await scope.ServiceProvider.GetRequiredService<IModuleRepository>().NomsAsync();

        var maxParCycle = int.TryParse(_config["OutlookAgent:MaxEmailsParCycle"], out var m) && m > 0 ? m : 10;
        var fenetre = TimeSpan.FromHours(int.TryParse(_config["OutlookAgent:FenetreHeures"], out var h) && h > 0 ? h : 24);
        var depuis = DateTime.UtcNow - fenetre;
        var internes = AdressesInternes();
        Purger(depuis);

        foreach (var (uid, _, modele) in await mailboxes.AgentsActifsAsync())
        {
            if (stop.IsCancellationRequested) break;
            var emailsTraites = 0; var ticketsCrees = 0; string? erreur = null;
            BlocageIa? blocage = null;
            try
            {
                var user = await contexte.ChargerParIdAsync(uid);
                if (!(user.EstAdmin || user.EstCollaborateur)) continue;   // agent réservé admin + collaborateur

                // Les e-mails RÉCENTS de la boîte, lus ou non : un e-mail ouvert par l'utilisateur avant le
                // passage de l'agent est quand même traité, et les vieux e-mails non lus ne font pas de tickets.
                // L'état lu / non lu de la boîte n'est jamais modifié par l'agent.
                var boite = await resolver.PourAsync(user);
                var page = await boite.GetEmailsAsync(1, Math.Max(maxParCycle * 3, 30), null, "inbox", nonLus: false);

                foreach (var e in page.Items)
                {
                    if (stop.IsCancellationRequested || blocage != null || emailsTraites >= maxParCycle) break;
                    if (e.Date is { } recu && recu.ToUniversalTime() < depuis) break;   // liste du plus récent au plus ancien
                    var cle = $"{uid}|{e.Id}";
                    if (_traites.ContainsKey(cle)) continue;

                    // Ignore les e-mails internes (ses propres envois, domaines exclus, notifications, robots…).
                    if (EstInterne(e.DeEmail, e.Objet, user.Email, internes))
                    {
                        _traites[cle] = DateTime.UtcNow;
                        continue;
                    }

                    try
                    {
                        // Seuls les contacts Divalto deviennent des tickets : inutile d'appeler l'IA pour les autres.
                        if ((await clients.SocietesParEmailAsync(e.DeEmail ?? "")).Count == 0)
                        {
                            _logger.LogInformation("Agent (user {Uid}) : {Email} n'est pas un contact Divalto — « {Objet} » ignoré.", uid, e.DeEmail, e.Objet);
                            _traites[cle] = DateTime.UtcNow;
                            continue;
                        }

                        var detail = await boite.GetEmailByIdAsync(e.Id);
                        var signature = LienEmailTicket.Signature(
                            detail.MessageIdInternet, detail.ThreadIdInternet, e.DeEmail, e.Objet, e.Date);
                        // Déjà un ticket (créé par l'agent, à la main ou depuis une autre boîte) : rien à faire.
                        if (signature.EstExploitable && await tickets.TicketLieAsync(signature) != null)
                        {
                            _traites[cle] = DateTime.UtcNow;
                            continue;
                        }
                        var corps = EnTexte(detail.CorpsTexte ?? detail.CorpsHtml ?? e.Apercu ?? "");
                        var message = $"Objet: {e.Objet ?? "(sans objet)"}\n\n{corps}".Trim();

                        // Pièces jointes : la 1re est attachée au ticket ; les IMAGES nourrissent l'IA (multimodal).
                        var medias = new List<(string, byte[])>();
                        PieceJointeAgent? piece = null;
                        foreach (var pj in detail.PiecesJointes.Take(5))
                        {
                            if (string.IsNullOrEmpty(pj.Id)) continue;
                            try
                            {
                                var (contenu, nom, type) = await boite.TelechargerPieceJointeAsync(e.Id, pj.Id);
                                var typeReel = type ?? pj.TypeMime ?? "application/octet-stream";
                                piece ??= new PieceJointeAgent(nom ?? pj.Nom ?? "piece-jointe", typeReel, contenu);
                                if (typeReel.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                                    medias.Add((typeReel, contenu));
                            }
                            catch (Exception ex) { _logger.LogWarning(ex, "Agent : pièce jointe {Pj} illisible.", pj.Id); }
                        }

                        // Images collées DANS le corps (captures d'écran) : elles arrivent en data URI dans le HTML,
                        // pas dans les pièces jointes. L'IA les lit, et la première est jointe au ticket.
                        foreach (var (type, data) in ImagesDuCorps(detail.CorpsHtml))
                        {
                            if (medias.Count >= 5) break;
                            medias.Add((type, data));
                            piece ??= new PieceJointeAgent($"capture-{medias.Count}.{type.Split('/')[1].Split('+')[0]}", type, data);
                        }

                        emailsTraites++;
                        // L'objet seul suffit (« Problem divalto » sans texte dans le corps est une vraie demande).
                        if (!string.IsNullOrWhiteSpace(corps) || !string.IsNullOrWhiteSpace(e.Objet) || medias.Count > 0)
                        {
                            var resultat = await ia.AnalyserAsync(message, e.De ?? "", e.DeEmail ?? "", medias, modele, stop, produits, modules);
                            if (resultat.Blocage != null)
                            {
                                // Modèle bloqué : l'e-mail sera repris dès qu'un modèle répond de nouveau,
                                // et on arrête ce cycle pour cet utilisateur — les suivants échoueraient aussi.
                                blocage = resultat.Blocage;
                                break;
                            }
                            if (resultat.Analyse == null)
                            {
                                // Panne passagère de l'IA : on réessaie aux cycles suivants (3 fois au plus).
                                if (_echecs.AddOrUpdate(cle, 1, (_, n) => n + 1) >= 3) _traites[cle] = DateTime.UtcNow;
                                _logger.LogWarning("Agent (user {Uid}) : analyse IA impossible pour « {Objet} », nouvel essai au prochain cycle.", uid, e.Objet);
                                continue;
                            }
                            _stats.LeverBlocage(uid);   // le modèle répond de nouveau

                            if (resultat.Analyse is { EstUnProbleme: true } analyse)
                            {
                                var id = await tickets.CreerDepuisAgentAsync(
                                    analyse, e.DeEmail ?? "", e.De, e.Objet, signature, user, piece, autoAssign: false);   // file du produit
                                if (id != null)
                                {
                                    ticketsCrees++;
                                    _logger.LogInformation("Agent (user {Uid}) : e-mail → ticket #{Id}.", uid, id);
                                }
                            }
                            else
                            {
                                _logger.LogInformation("Agent (user {Uid}) : « {Objet} » n'est pas une demande de support (pas de ticket).", uid, e.Objet);
                            }
                        }
                        _traites[cle] = DateTime.UtcNow;
                        _echecs.TryRemove(cle, out _);
                    }
                    catch (Exception ex) { erreur = ex.Message; _logger.LogWarning(ex, "Agent (user {Uid}) : e-mail {Id} non traité.", uid, e.Id); }
                }
            }
            catch (Exception ex) { erreur = ex.Message; _logger.LogWarning(ex, "Agent : boîte de l'utilisateur {Uid} indisponible.", uid); }
            _stats.EnregistrerCycle(uid, emailsTraites, ticketsCrees, erreur);

            // Prévient l'utilisateur une seule fois par blocage (popup : changer de modèle ou couper l'IA).
            if (blocage != null && _stats.SignalerBlocage(uid, blocage))
            {
                _logger.LogWarning("Agent (user {Uid}) en pause : {M} {Raison}.", uid,
                    blocage.TousLesModeles ? "tous les modèles" : $"modèle {blocage.Modele}",
                    blocage.Quota ? "en quota" : "indisponible(s)");
                try { await _hub.Clients.Group($"user-{uid}").SendAsync(EvtAgentBloque, blocage, stop); }
                catch (Exception ex) { _logger.LogWarning(ex, "Agent : alerte temps réel non envoyée (user {Uid}).", uid); }
            }
        }
    }

    /// <summary>E-mails déjà examinés (« utilisateur|id ») : ni ré-analysés, ni retransformés en ticket.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _traites = new();
    /// <summary>Échecs d'analyse IA par e-mail (pannes passagères) : abandon après 3 essais.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _echecs = new();

    /// <summary>Oublie les e-mails sortis de la fenêtre de traitement (la mémoire reste petite).</summary>
    private void Purger(DateTime depuis)
    {
        foreach (var (cle, quand) in _traites)
            if (quand < depuis - TimeSpan.FromDays(1)) _traites.TryRemove(cle, out _);
        if (_echecs.Count > 5000) _echecs.Clear();
    }

    /// <summary>Adresses considérées « internes » (notifications / expéditeurs configurés), à ignorer.</summary>
    private HashSet<string> AdressesInternes()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Ajouter(string? v) { if (!string.IsNullOrWhiteSpace(v)) set.Add(v.Trim()); }

        Ajouter(_config["Outlook:ExpediteurNotifications"]);
        Ajouter(_config["Outlook:UserEmail"]);
        foreach (var c in _config.GetSection("Outlook:Expediteurs").GetChildren()) Ajouter(c.Value);
        foreach (var c in _config.GetSection("OutlookAgent:IgnorerExpediteurs").GetChildren()) Ajouter(c.Value);
        // Domaines entiers (ex. l'équipe interne) : enregistrés sous la forme « @domaine ».
        foreach (var c in _config.GetSection("OutlookAgent:IgnorerDomaines").GetChildren())
            if (!string.IsNullOrWhiteSpace(c.Value)) set.Add("@" + c.Value.Trim().TrimStart('@'));
        return set;
    }

    private static bool EstInterne(string? email, string? objet, string boxEmail, HashSet<string> internes)
    {
        var e = (email ?? "").Trim();
        if (e.Length == 0) return true;                                    // expéditeur inconnu : on ignore
        if (string.Equals(e, boxEmail, StringComparison.OrdinalIgnoreCase)) return true; // ses propres e-mails
        if (internes.Contains(e)) return true;                             // expéditeurs internes configurés
        // Domaine ignoré : jamais de NOUVEAU ticket. Une réponse à un ticket existant garde son badge
        // « Ticket #N » (lien par le fil / l'objet, calculé à l'affichage de la boîte).
        var arobase = e.LastIndexOf('@');
        if (arobase >= 0 && internes.Contains(e[arobase..])) return true;

        // Expéditeurs automatiques (retours « non remis », robots) : jamais un ticket, jamais d'accusé.
        var local = e.Split('@')[0];
        if (ExpediteursAutomatiques.Any(a => local.Contains(a, StringComparison.OrdinalIgnoreCase))) return true;

        var o = (objet ?? "").TrimStart();
        return o.StartsWith("[HelpDesk]", StringComparison.OrdinalIgnoreCase)
            || o.StartsWith("HelpDesk -", StringComparison.OrdinalIgnoreCase)
            || o.StartsWith("HelpDesk –", StringComparison.OrdinalIgnoreCase)   // tiret long
            || o.StartsWith("[AI]", StringComparison.OrdinalIgnoreCase)
            // Nos propres accusés de réception, même revenus en retour d'erreur (évite la boucle).
            || o.Contains("Votre demande a bien été enregistrée", StringComparison.OrdinalIgnoreCase)
            || ObjetsAutomatiques.Any(p => o.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] ExpediteursAutomatiques =
        { "postmaster", "mailer-daemon", "microsoftexchange", "noreply", "no-reply", "no_reply", "donotreply", "do-not-reply" };

    /// <summary>Débuts d'objet des messages automatiques : non-remise, réponses automatiques d'absence.</summary>
    private static readonly string[] ObjetsAutomatiques =
    {
        "Non remis", "Undeliverable", "Undelivered", "Delivery Status Notification", "Mail Delivery",
        "Returned mail", "Échec de la remise", "Echec de la remise", "Remise différée", "Delivery delayed",
        "Réponse automatique", "Automatic reply", "Auto:", "Absent", "Out of Office", "Message d'absence"
    };

    /// <summary>Images intégrées au corps HTML (&lt;img src="data:image/…;base64,…"&gt;), décodées.</summary>
    public static IEnumerable<(string Type, byte[] Data)> ImagesDuCorps(string? html)
    {
        if (string.IsNullOrEmpty(html)) yield break;
        foreach (Match m in Regex.Matches(html, @"src\s*=\s*[""']data:(image/[A-Za-z0-9.+-]+);base64,([A-Za-z0-9+/=\s]+)[""']",
                     RegexOptions.IgnoreCase))
        {
            byte[]? data = null;
            try { data = Convert.FromBase64String(Regex.Replace(m.Groups[2].Value, @"\s", "")); }
            catch (FormatException) { /* image illisible : ignorée */ }
            if (data is { Length: > 0 }) yield return (m.Groups[1].Value.ToLowerInvariant(), data);
        }
    }

    /// <summary>Texte brut depuis un corps HTML (balises retirées, entités simples).</summary>
    private static string EnTexte(string html)
    {
        var sansBalises = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        sansBalises = Regex.Replace(sansBalises, "<[^>]+>", " ");
        sansBalises = System.Net.WebUtility.HtmlDecode(sansBalises);
        return Regex.Replace(sansBalises, @"\s+", " ").Trim();
    }
}
