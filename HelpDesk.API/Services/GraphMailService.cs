using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelpDesk.API.Data;
using HelpDesk.API.DTOs;
using HelpDesk.API.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;

namespace HelpDesk.API.Services;

public class GraphMailService : IMailbox
{
    /// <summary>Catégorie Outlook posée sur un email converti en ticket (lien durable email → ticket).</summary>
    private const string PrefixeCategorieTicket = "Ticket HelpDesk #";
    private static readonly Regex CategorieTicket = new(@"^Ticket HelpDesk #(\d+)$", RegexOptions.Compiled);

    /// <summary>Dossiers connus de Graph, dans l'ordre d'affichage.</summary>
    private static readonly string[] DossiersConnus = { "inbox", "drafts", "sentitems", "junkemail", "deleteditems", "archive" };

    private const int MaxDestinataires = 20;
    private const int MaxMessage = 20000;

    private readonly IConfiguration _config;
    private readonly HelpDeskContext _db;
    private readonly IHttpClientFactory _httpFactory;

    /// <summary>Boîte ciblée par cette instance : par défaut la boîte partagée, sinon celle d'un membre.</summary>
    private readonly string _userEmail;

    /// <summary>Jeton de rafraîchissement (OAuth délégué). Null = mode application (app-only).</summary>
    private readonly string? _refreshToken;

    // Partage entre les requetes : MSAL garde le jeton en cache jusqu'a son expiration.
    private static IConfidentialClientApplication? _msalApp;

    public GraphMailService(IConfiguration config, HelpDeskContext db, IHttpClientFactory httpFactory)
        : this(config, db, httpFactory, config["Outlook:UserEmail"] ?? "", null) { }

    private GraphMailService(IConfiguration config, HelpDeskContext db, IHttpClientFactory httpFactory,
        string userEmail, string? refreshToken)
    {
        _config = config;
        _db = db;
        _httpFactory = httpFactory;
        _userEmail = userEmail;
        _refreshToken = refreshToken;
    }

    /// <summary>
    /// Nouvelle instance ciblant la boîte d'un membre précis (même application Graph, mode app-only).
    /// Fonctionne pour toute boîte du tenant Microsoft 365 configuré.
    /// </summary>
    public GraphMailService PourBoite(string email) => new(_config, _db, _httpFactory, email, null);

    /// <summary>
    /// Nouvelle instance en mode OAuth délégué : accède à la boîte de l'utilisateur via son jeton
    /// de rafraîchissement (fonctionne pour toute boîte Microsoft, y compris externes/personnelles).
    /// </summary>
    public GraphMailService PourBoiteDeleguee(string email, string refreshToken) =>
        new(_config, _db, _httpFactory, email, refreshToken);

    // En mode délégué le jeton EST celui de l'utilisateur : on cible /me. Sinon /users/{email} (app-only).
    private string BaseUrl => _refreshToken != null
        ? "https://graph.microsoft.com/v1.0/me"
        : $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(_userEmail)}";

    /// <summary>Vérifie que l'application peut accéder à cette boîte (utilisé avant d'enregistrer la connexion).</summary>
    public async Task TesterAsync()
    {
        var client = await CreateGraphClientAsync();
        var response = await client.GetAsync($"{BaseUrl}/mailFolders/inbox?$select=id");

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new MetierException(
                "Cette boîte est introuvable dans l'organisation Microsoft 365. " +
                "Vérifiez l'adresse : elle doit appartenir au même domaine que la boîte partagée (ex. @mediasoft.tn).");

        await VerifierAsync(response);
    }

    // ─── OAuth2 Token ─────────────────────────────────────────────────────────
    private async Task<string> GetAccessTokenAsync()
    {
        // Mode délégué : on échange le refresh token contre un access token (boîte de l'utilisateur).
        if (_refreshToken != null)
            return await RafraichirJetonDelegueAsync(_refreshToken);

        // Mode application (app-only) : jeton de l'application pour les boîtes du tenant.
        _msalApp ??= ConfidentialClientApplicationBuilder
            .Create(_config["Outlook:ClientId"])
            .WithClientSecret(_config["Outlook:ClientSecret"])
            .WithAuthority($"https://login.microsoftonline.com/{_config["Outlook:TenantId"]}")
            .Build();

        var result = await _msalApp
            .AcquireTokenForClient(new[] { "https://graph.microsoft.com/.default" })
            .ExecuteAsync();

        return result.AccessToken;
    }

    /// <summary>OAuth délégué : échange le refresh token contre un access token frais.</summary>
    private async Task<string> RafraichirJetonDelegueAsync(string refreshToken)
    {
        var client = _httpFactory.CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _config["Outlook:ClientId"] ?? "",
            ["client_secret"] = _config["Outlook:ClientSecret"] ?? "",
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = "https://graph.microsoft.com/Mail.ReadWrite https://graph.microsoft.com/Mail.Send offline_access"
        });

        var response = await client.PostAsync("https://login.microsoftonline.com/common/oauth2/v2.0/token", form);
        var racine = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        if (!response.IsSuccessStatusCode)
        {
            var err = racine.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            throw new MetierException("Connexion Outlook expirée : reconnectez votre boîte." +
                (err != null ? $" ({err.Split('\n')[0]})" : ""));
        }

        return racine.GetProperty("access_token").GetString()!;
    }

    private async Task<HttpClient> CreateGraphClientAsync()
    {
        var token = await GetAccessTokenAsync();
        var client = _httpFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>Réponse Graph en échec : exception portant le message d'erreur de Graph.</summary>
    private static async Task VerifierAsync(HttpResponseMessage reponse)
    {
        if (reponse.IsSuccessStatusCode) return;

        if (reponse.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                "L'application Outlook n'a pas l'autorisation Microsoft Graph requise pour cette action " +
                "(Mail.ReadWrite pour classer les e-mails, Mail.Send pour envoyer).");

        string? detail = null;
        try
        {
            var racine = JsonDocument.Parse(await reponse.Content.ReadAsStringAsync()).RootElement;
            if (racine.TryGetProperty("error", out var erreur) && erreur.TryGetProperty("message", out var message))
                detail = message.GetString();
        }
        catch (JsonException) { /* corps non JSON */ }

        throw new InvalidOperationException(detail ?? $"Réponse Graph {(int)reponse.StatusCode}.");
    }

    // ─── Liste des emails (paginée) ───────────────────────────────────────────
    public async Task<EmailPageDto> GetEmailsAsync(int page = 1, int pageSize = 20, string? search = null,
        string folder = "inbox", bool nonLus = false)
    {
        var skip = (page - 1) * pageSize;
        var select = "$select=id,subject,bodyPreview,from,receivedDateTime,isRead,hasAttachments,categories,internetMessageId,conversationId,internetMessageHeaders";
        var dossier = Uri.EscapeDataString(string.IsNullOrWhiteSpace(folder) ? "inbox" : folder);
        var recherche = search?.Replace("\"", "").Replace("\\", "").Trim();

        var client = await CreateGraphClientAsync();
        string url;
        if (!string.IsNullOrEmpty(recherche))
        {
            // Graph refuse $orderby, $skip et $filter avec $search sur les messages :
            // on demande les N premiers resultats (tries par date) et on decoupe la page ici.
            url = $"{BaseUrl}/mailFolders/{dossier}/messages?{select}&$top={page * pageSize}" +
                  $"&$search=\"{Uri.EscapeDataString(recherche)}\"";
        }
        else
        {
            // $count necessite ce header
            client.DefaultRequestHeaders.Add("ConsistencyLevel", "eventual");
            // Avec $orderby, la propriete triee doit apparaitre en premier dans $filter (sinon InefficientFilter).
            var filtre = nonLus ? "&$filter=receivedDateTime ge 1900-01-01T00:00:00Z and isRead eq false" : "";
            url = $"{BaseUrl}/mailFolders/{dossier}/messages?{select}&$orderby=receivedDateTime desc" +
                  $"&$top={pageSize}&$skip={skip}&$count=true{filtre}";
        }

        var response = await client.GetAsync(url);
        await VerifierAsync(response);

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var messages = root.TryGetProperty("value", out var valueArr)
            ? valueArr.EnumerateArray().ToList()
            : new List<JsonElement>();

        int total;
        if (!string.IsNullOrEmpty(recherche))
        {
            // Page pleine : il reste peut-etre des resultats, on annonce au moins un de plus.
            var pleine = messages.Count >= page * pageSize;
            if (nonLus)
                messages = messages.Where(m => !(m.TryGetProperty("isRead", out var lu) && lu.GetBoolean())).ToList();
            total = pleine ? messages.Count + 1 : messages.Count;
            messages = messages.Skip(skip).ToList();
        }
        else
        {
            total = root.TryGetProperty("@odata.count", out var countProp) ? countProp.GetInt32() : messages.Count;
        }

        // Tickets candidats pour le rapprochement par expediteur (emails sans categorie ticket)
        var senderEmails = messages
            .Select(e => e.TryGetProperty("from", out var f) &&
                         f.TryGetProperty("emailAddress", out var ea) &&
                         ea.TryGetProperty("address", out var addr) ? addr.GetString() : null)
            .Where(e => e != null)
            .Distinct()
            .ToList();

        var tickets = await _db.TicketList
            .Where(t => t.Email != null && senderEmails.Contains(t.Email))
            .Select(t => new { t.Email, t.Id, t.DateDemande2 })
            .ToListAsync();

        var items = new List<EmailSummaryDto>();
        foreach (var emailEl in messages)
        {
            var id = emailEl.TryGetProperty("id", out var idP) ? idP.GetString() : null;
            var subject = emailEl.TryGetProperty("subject", out var subP) ? subP.GetString() : null;
            var preview = emailEl.TryGetProperty("bodyPreview", out var bpP) ? bpP.GetString() : null;
            var isRead = emailEl.TryGetProperty("isRead", out var irP) && irP.GetBoolean();
            var hasAtt = emailEl.TryGetProperty("hasAttachments", out var haP) && haP.GetBoolean();

            var (fromName, fromEmail) = LireAdresse(emailEl, "from");
            DateTime? receivedDate = emailEl.TryGetProperty("receivedDateTime", out var rdP) ? rdP.GetDateTime() : null;

            // Ticket cree depuis cet email (categorie) ; sinon meme expediteur, date proche (± 10 min)
            var ticketId = TicketDepuisCategories(emailEl);
            if (ticketId == null && fromEmail != null && receivedDate != null)
            {
                var linked = tickets.FirstOrDefault(t =>
                    t.Email?.Equals(fromEmail, StringComparison.OrdinalIgnoreCase) == true &&
                    t.DateDemande2.HasValue &&
                    Math.Abs((t.DateDemande2.Value - receivedDate.Value).TotalMinutes) < 10);
                ticketId = linked?.Id;
            }

            items.Add(new EmailSummaryDto
            {
                Id = id ?? "",
                De = fromName,
                DeEmail = fromEmail,
                Objet = subject,
                Apercu = preview?.Length > 150 ? preview[..150] + "..." : preview,
                Date = receivedDate,
                EstLu = isRead,
                HasAttachments = hasAtt,
                TicketId = ticketId,
                MessageIdInternet = ImapMailService.NettoyerMessageId(
                    emailEl.TryGetProperty("internetMessageId", out var imP) ? imP.GetString() : null),
                ThreadIdInternet = FilRfc(emailEl)
            });
        }

        return new EmailPageDto
        {
            Total = total,
            Items = items,
            NextLink = root.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null
        };
    }

    // ─── Détail d'un email ────────────────────────────────────────────────────
    public async Task<EmailDetailDto> GetEmailByIdAsync(string messageId)
    {
        var url = $"{BaseUrl}/messages/{messageId}?$select=id,subject,body,from,toRecipients,ccRecipients,receivedDateTime,isRead,hasAttachments,categories,internetMessageId,conversationId,internetMessageHeaders";

        var client = await CreateGraphClientAsync();
        var response = await client.GetAsync(url);
        await VerifierAsync(response);

        var el = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var (fromName, fromEmail) = LireAdresse(el, "from");

        string? bodyHtml = null, bodyText = null;
        if (el.TryGetProperty("body", out var bodyEl))
        {
            var contentType = bodyEl.TryGetProperty("contentType", out var ctP) ? ctP.GetString() : "text";
            var content = bodyEl.TryGetProperty("content", out var cP) ? cP.GetString() : null;
            if (contentType?.Equals("html", StringComparison.OrdinalIgnoreCase) == true)
                bodyHtml = content;
            else
                bodyText = content;
        }

        DateTime? receivedDate = el.TryGetProperty("receivedDateTime", out var rdP) ? rdP.GetDateTime() : null;

        // Ticket lié : categorie posee a la creation, sinon rapprochement expediteur + date
        var ticketId = TicketDepuisCategories(el);
        if (ticketId == null && fromEmail != null && receivedDate != null)
        {
            var candidates = await _db.TicketList
                .Where(t => t.Email != null &&
                            t.Email.ToLower() == fromEmail.ToLower() &&
                            t.DateDemande2 != null)
                .Select(t => new { t.Id, t.DateDemande2 })
                .ToListAsync();

            var linked = candidates.FirstOrDefault(t =>
                t.DateDemande2.HasValue &&
                Math.Abs((t.DateDemande2.Value - receivedDate.Value).TotalMinutes) < 10);

            if (linked != null) ticketId = linked.Id;
        }

        // Pièces jointes (+ images inline : on les injecte dans le corps HTML à la place des « cid: »)
        var attachments = new List<EmailAttachmentDto>();
        var hasAtt = el.TryGetProperty("hasAttachments", out var haP) && haP.GetBoolean();
        // Outlook met parfois hasAttachments=false pour les e-mails avec SEULEMENT des images inline :
        // on récupère donc aussi les pièces jointes si le corps contient des références « cid: ».
        var corpsAvecCid = bodyHtml != null && bodyHtml.Contains("cid:", StringComparison.OrdinalIgnoreCase);
        if (hasAtt || corpsAvecCid)
        {
            try
            {
                // Pas de $select : « contentBytes » n'existe que sur fileAttachment (type dérivé) ;
                // le sélectionner sur la collection de base renvoie 400. Sans $select, Graph renvoie
                // les objets complets (dont contentBytes pour les images).
                var attUrl = $"{BaseUrl}/messages/{messageId}/attachments";
                var attResp = await client.GetAsync(attUrl);
                if (attResp.IsSuccessStatusCode)
                {
                    var attJson = JsonDocument.Parse(await attResp.Content.ReadAsStringAsync()).RootElement;
                    if (attJson.TryGetProperty("value", out var attArr))
                    {
                        foreach (var att in attArr.EnumerateArray())
                        {
                            var nom = att.TryGetProperty("name", out var nP) ? nP.GetString() : null;
                            var type = att.TryGetProperty("contentType", out var cP) ? cP.GetString() : null;
                            var estInline = att.TryGetProperty("isInline", out var ilP) && ilP.ValueKind == JsonValueKind.True;
                            var contentId = att.TryGetProperty("contentId", out var ciP) && ciP.ValueKind == JsonValueKind.String ? ciP.GetString() : null;
                            var aBytes = att.TryGetProperty("contentBytes", out var cbP) && cbP.ValueKind == JsonValueKind.String;

                            // Image intégrée : remplace « cid:xxx » par les données réelles (data URI) et on ne la liste pas.
                            if (estInline && aBytes && bodyHtml != null && !string.IsNullOrEmpty(contentId))
                            {
                                var cid = contentId!.Trim('<', '>');
                                var dataUri = $"data:{type ?? "image/png"};base64,{cbP.GetString()}";
                                bodyHtml = bodyHtml.Replace($"cid:{cid}", dataUri).Replace($"cid:{contentId}", dataUri);
                                continue;
                            }

                            attachments.Add(new EmailAttachmentDto
                            {
                                Id = att.TryGetProperty("id", out var iP) ? iP.GetString() : null,
                                Nom = nom,
                                TypeMime = type,
                                Taille = att.TryGetProperty("size", out var sP) ? sP.GetInt64() : 0
                            });
                        }
                    }
                }
            }
            catch { /* ignorer les erreurs pièces jointes */ }
        }

        return new EmailDetailDto
        {
            Id = messageId,
            De = fromName,
            DeEmail = fromEmail,
            Objet = el.TryGetProperty("subject", out var subP) ? subP.GetString() : null,
            Date = receivedDate,
            EstLu = el.TryGetProperty("isRead", out var irP) && irP.GetBoolean(),
            HasAttachments = hasAtt,
            CorpsHtml = bodyHtml,
            CorpsTexte = bodyText,
            PiecesJointes = attachments,
            A = LireDestinataires(el, "toRecipients"),
            Cc = LireDestinataires(el, "ccRecipients"),
            TicketId = ticketId,
            MessageIdInternet = ImapMailService.NettoyerMessageId(
                el.TryGetProperty("internetMessageId", out var imP) ? imP.GetString() : null),
            ThreadIdInternet = FilRfc(el)
        };
    }

    // ─── Dossiers de la boîte ─────────────────────────────────────────────────
    public async Task<List<EmailDossierDto>> GetDossiersAsync()
    {
        var client = await CreateGraphClientAsync();
        const string champs = "$select=id,displayName,unreadItemCount,totalItemCount,childFolderCount,isHidden";

        // Les noms affiches sont localises : on identifie les dossiers connus par leur ID.
        var clesTask = Task.WhenAll(DossiersConnus.Select(async cle =>
        {
            var r = await client.GetAsync($"{BaseUrl}/mailFolders/{cle}?$select=id");
            if (!r.IsSuccessStatusCode) return (cle, id: (string?)null);
            var id = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
            return (cle, id);
        }));

        var response = await client.GetAsync($"{BaseUrl}/mailFolders?$top=100&{champs}");
        await VerifierAsync(response);
        var racines = LireDossiers(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement);

        // Un niveau de sous-dossiers (ex. dossiers crees sous la Boite de reception)
        var enfants = await Task.WhenAll(racines.Where(d => d.enfants > 0).Select(async parent =>
        {
            var r = await client.GetAsync($"{BaseUrl}/mailFolders/{parent.dto.Id}/childFolders?$top=100&{champs}");
            if (!r.IsSuccessStatusCode) return (parentId: parent.dto.Id, liste: new List<EmailDossierDto>());
            var liste = LireDossiers(JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement)
                .Select(d => d.dto).OrderBy(d => d.Nom).ToList();
            return (parentId: parent.dto.Id, liste);
        }));

        var cles = (await clesTask).Where(c => c.id != null).ToDictionary(c => c.id!, c => c.cle);
        foreach (var (dto, _) in racines)
            dto.Cle = cles.GetValueOrDefault(dto.Id);

        var resultat = new List<EmailDossierDto>();
        var ordonnes = racines.Select(r => r.dto)
            .OrderBy(d => d.Cle == null ? DossiersConnus.Length : Array.IndexOf(DossiersConnus, d.Cle))
            .ThenBy(d => d.Nom);
        foreach (var dossier in ordonnes)
        {
            resultat.Add(dossier);
            var sous = enfants.FirstOrDefault(e => e.parentId == dossier.Id).liste;
            if (sous != null)
                resultat.AddRange(sous.Select(s => { s.Nom = $"{dossier.Nom} / {s.Nom}"; return s; }));
        }
        return resultat;
    }

    private static List<(EmailDossierDto dto, int enfants)> LireDossiers(JsonElement racine)
    {
        var liste = new List<(EmailDossierDto, int)>();
        if (!racine.TryGetProperty("value", out var tableau)) return liste;

        foreach (var d in tableau.EnumerateArray())
        {
            if (d.TryGetProperty("isHidden", out var cache) && cache.ValueKind == JsonValueKind.True) continue;
            liste.Add((new EmailDossierDto
            {
                Id = d.GetProperty("id").GetString()!,
                Nom = d.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : "",
                NonLus = d.TryGetProperty("unreadItemCount", out var nl) ? nl.GetInt32() : 0,
                Total = d.TryGetProperty("totalItemCount", out var t) ? t.GetInt32() : 0
            }, d.TryGetProperty("childFolderCount", out var c) ? c.GetInt32() : 0));
        }
        return liste;
    }

    // ─── Télécharger une pièce jointe ─────────────────────────────────────────
    public async Task<(byte[] Contenu, string Nom, string Type)> TelechargerPieceJointeAsync(string messageId, string attachmentId)
    {
        var url = $"{BaseUrl}/messages/{messageId}/attachments/{attachmentId}";
        var client = await CreateGraphClientAsync();
        var response = await client.GetAsync(url);
        await VerifierAsync(response);

        var el = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var nom = el.TryGetProperty("name", out var nP) ? nP.GetString() : null;
        var type = el.TryGetProperty("contentType", out var cP) ? cP.GetString() : null;

        // fileAttachment : le contenu est en base64 dans contentBytes.
        if (!el.TryGetProperty("contentBytes", out var bP) || bP.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Cette pièce jointe n'a pas de contenu téléchargeable.");

        var contenu = Convert.FromBase64String(bP.GetString()!);
        return (contenu, string.IsNullOrWhiteSpace(nom) ? "piece-jointe" : nom!,
                string.IsNullOrWhiteSpace(type) ? "application/octet-stream" : type!);
    }

    // ─── Lu / non lu ─────────────────────────────────────────────────────────
    public async Task MarquerCommeLuAsync(string messageId, bool lu = true)
    {
        var client = await CreateGraphClientAsync();
        var response = await client.PatchAsync($"{BaseUrl}/messages/{messageId}", JsonContent.Create(new { isRead = lu }));
        await VerifierAsync(response);
    }

    // ─── Déplacer (dossier, courrier indésirable, éléments supprimés) ────────
    public async Task DeplacerAsync(string messageId, string dossierId)
    {
        if (string.IsNullOrWhiteSpace(dossierId))
            throw new MetierException("Le dossier de destination est requis.");

        var client = await CreateGraphClientAsync();
        var response = await client.PostAsync($"{BaseUrl}/messages/{messageId}/move",
            JsonContent.Create(new { destinationId = dossierId.Trim() }));
        await VerifierAsync(response);
    }

    // ─── Répondre / répondre à tous ──────────────────────────────────────────
    public async Task RepondreAsync(string messageId, EmailRepondreRequest request)
    {
        var message = ValiderMessage(request.Message);
        var client = await CreateGraphClientAsync();
        var action = request.Tous ? "replyAll" : "reply";
        var response = await client.PostAsync($"{BaseUrl}/messages/{messageId}/{action}",
            JsonContent.Create(new { comment = EnHtml(message) }));
        await VerifierAsync(response);
    }

    // ─── Transférer ──────────────────────────────────────────────────────────
    public async Task TransfererAsync(string messageId, EmailEnvoyerRequest request)
    {
        var destinataires = ValiderAdresses(request.Destinataires, obligatoire: true);
        var client = await CreateGraphClientAsync();
        var response = await client.PostAsync($"{BaseUrl}/messages/{messageId}/forward",
            JsonContent.Create(new
            {
                comment = EnHtml(request.Message ?? ""),
                toRecipients = destinataires.Select(Destinataire)
            }));
        await VerifierAsync(response);
    }

    // ─── Nouveau message ─────────────────────────────────────────────────────
    public async Task EnvoyerAsync(EmailEnvoyerRequest request)
    {
        var destinataires = ValiderAdresses(request.Destinataires, obligatoire: true);
        var cc = ValiderAdresses(request.Cc, obligatoire: false);
        var message = ValiderMessage(request.Message);
        var objet = request.Objet?.Trim() ?? "";
        if (objet.Length > 255) throw new MetierException("L'objet ne doit pas dépasser 255 caractères.");

        var client = await CreateGraphClientAsync();
        var response = await client.PostAsync($"{BaseUrl}/sendMail", JsonContent.Create(new
        {
            message = new
            {
                subject = objet,
                body = new { contentType = "Text", content = message },
                toRecipients = destinataires.Select(Destinataire),
                ccRecipients = cc.Select(Destinataire)
            },
            saveToSentItems = true
        }));
        await VerifierAsync(response);
    }

    // ─── Nouveau message HTML (notifications stylées) ────────────────────────
    public async Task EnvoyerHtmlAsync(List<string> destinataires, string objet, string html)
    {
        var dests = ValiderAdresses(destinataires, obligatoire: true);
        var sujet = (objet ?? "").Trim();
        if (sujet.Length > 255) sujet = sujet[..255];

        var client = await CreateGraphClientAsync();
        var response = await client.PostAsync($"{BaseUrl}/sendMail", JsonContent.Create(new
        {
            message = new
            {
                subject = sujet,
                body = new { contentType = "HTML", content = html },
                toRecipients = dests.Select(Destinataire)
            },
            saveToSentItems = true
        }));
        await VerifierAsync(response);
    }

    // ─── Lien email → ticket (catégorie Outlook) ─────────────────────────────
    public async Task LierTicketAsync(string messageId, int ticketId)
    {
        if (!await _db.TicketList.AnyAsync(t => t.Id == ticketId))
            throw MetierException.Introuvable($"Le ticket #{ticketId} n'existe pas.");

        var client = await CreateGraphClientAsync();
        var lecture = await client.GetAsync($"{BaseUrl}/messages/{messageId}?$select=categories");
        await VerifierAsync(lecture);

        var el = JsonDocument.Parse(await lecture.Content.ReadAsStringAsync()).RootElement;
        var categories = el.TryGetProperty("categories", out var cats)
            ? cats.EnumerateArray().Select(c => c.GetString()).Where(c => c != null).Select(c => c!).ToList()
            : new List<string>();

        var categorie = $"{PrefixeCategorieTicket}{ticketId}";
        if (categories.Contains(categorie)) return;
        categories.Add(categorie);

        var response = await client.PatchAsync($"{BaseUrl}/messages/{messageId}", JsonContent.Create(new { categories }));
        await VerifierAsync(response);
    }

    // ─── Utilitaires ─────────────────────────────────────────────────────────
    /// <summary>
    /// Fil de discussion RFC (racine de References, sinon In-Reply-To, sinon Message-ID) — identique
    /// à la logique IMAP, pour que le rapprochement fonctionne entre fournisseurs (Graph ↔ IMAP).
    /// </summary>
    private static string? FilRfc(JsonElement el)
    {
        string? references = null, inReplyTo = null;
        if (el.TryGetProperty("internetMessageHeaders", out var hs) && hs.ValueKind == JsonValueKind.Array)
            foreach (var h in hs.EnumerateArray())
            {
                var name = h.TryGetProperty("name", out var nP) ? nP.GetString() : null;
                var value = h.TryGetProperty("value", out var vP) ? vP.GetString() : null;
                if (string.Equals(name, "References", StringComparison.OrdinalIgnoreCase)) references = value;
                else if (string.Equals(name, "In-Reply-To", StringComparison.OrdinalIgnoreCase)) inReplyTo = value;
            }
        var premiereRef = references?.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var messageId = el.TryGetProperty("internetMessageId", out var imP) ? imP.GetString() : null;
        return ImapMailService.NettoyerMessageId(premiereRef ?? inReplyTo ?? messageId);
    }

    private static int? TicketDepuisCategories(JsonElement message)
    {
        if (!message.TryGetProperty("categories", out var cats) || cats.ValueKind != JsonValueKind.Array) return null;
        foreach (var c in cats.EnumerateArray())
        {
            var m = CategorieTicket.Match(c.GetString() ?? "");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var id)) return id;
        }
        return null;
    }

    private static (string? nom, string? email) LireAdresse(JsonElement el, string propriete)
    {
        if (el.TryGetProperty(propriete, out var p) && p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("emailAddress", out var ea))
        {
            return (ea.TryGetProperty("name", out var n) ? n.GetString() : null,
                    ea.TryGetProperty("address", out var a) ? a.GetString() : null);
        }
        return (null, null);
    }

    private static List<EmailContactDto> LireDestinataires(JsonElement el, string propriete)
    {
        if (!el.TryGetProperty(propriete, out var tableau) || tableau.ValueKind != JsonValueKind.Array)
            return new List<EmailContactDto>();

        return tableau.EnumerateArray()
            .Where(d => d.TryGetProperty("emailAddress", out _))
            .Select(d =>
            {
                var ea = d.GetProperty("emailAddress");
                return new EmailContactDto
                {
                    Nom = ea.TryGetProperty("name", out var n) ? n.GetString() : null,
                    Email = ea.TryGetProperty("address", out var a) ? a.GetString() : null
                };
            })
            .ToList();
    }

    private static object Destinataire(string adresse) => new { emailAddress = new { address = adresse } };

    private static List<string> ValiderAdresses(List<string>? adresses, bool obligatoire)
    {
        var liste = (adresses ?? new List<string>())
            .Select(a => a?.Trim() ?? "")
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (obligatoire && liste.Count == 0)
            throw new MetierException("Indiquez au moins un destinataire.");
        if (liste.Count > MaxDestinataires)
            throw new MetierException($"{MaxDestinataires} destinataires maximum.");

        var invalide = liste.FirstOrDefault(a => !MailAddress.TryCreate(a, out _));
        if (invalide != null)
            throw new MetierException($"Adresse e-mail invalide : « {invalide} ».");
        return liste;
    }

    private static string ValiderMessage(string? message)
    {
        var texte = message?.Trim() ?? "";
        if (texte.Length == 0) throw new MetierException("Le message est vide.");
        if (texte.Length > MaxMessage) throw new MetierException($"Le message ne doit pas dépasser {MaxMessage} caractères.");
        return texte;
    }

    /// <summary>Texte saisi → HTML (le commentaire est insere dans le corps HTML de la reponse).</summary>
    private static string EnHtml(string texte) =>
        WebUtility.HtmlEncode(texte).Replace("\r\n", "\n").Replace("\n", "<br>");
}
