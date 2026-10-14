using System.Text;
using HelpDesk.API.DTOs;
using HelpDesk.API.Exceptions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace HelpDesk.API.Services;

/// <summary>
/// Boîte mail personnelle d'un utilisateur, via IMAP (lecture) et SMTP (envoi).
/// Une instance par connexion (créée par MailboxResolver avec les identifiants déchiffrés).
/// </summary>
public class ImapMailService : IMailbox
{
    // Séparateur interne dossier/UID dans l'identifiant d'un message (unit separator).
    private const char Sep = '\u001F';
    private readonly MailboxConnexion _c;

    public ImapMailService(MailboxConnexion connexion) => _c = connexion;

    // ─── Connexion aux serveurs ───────────────────────────────────────────────
    private async Task<ImapClient> OuvrirImapAsync()
    {
        var client = new ImapClient();
        try
        {
            await client.ConnectAsync(_c.ImapHost, _c.ImapPort,
                _c.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_c.Login, _c.MotDePasse);
            return client;
        }
        catch (AuthenticationException)
        {
            client.Dispose();
            throw new MetierException("Identifiants refusés par le serveur de messagerie. " +
                "Pour Gmail, utilisez un mot de passe d'application (2FA activée).", StatusCodes.Status401Unauthorized);
        }
        catch (Exception ex) when (ex is not MetierException)
        {
            client.Dispose();
            throw new MetierException($"Connexion IMAP impossible ({_c.ImapHost}:{_c.ImapPort}) : {ex.Message}", StatusCodes.Status502BadGateway);
        }
    }

    private async Task<SmtpClient> OuvrirSmtpAsync()
    {
        var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(_c.SmtpHost, _c.SmtpPort,
                _c.SmtpStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect);
            await client.AuthenticateAsync(_c.Login, _c.MotDePasse);
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new MetierException($"Envoi SMTP impossible ({_c.SmtpHost}:{_c.SmtpPort}) : {ex.Message}", StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>Teste la connexion (IMAP + SMTP) à la configuration du compte.</summary>
    public async Task TesterAsync()
    {
        (await OuvrirImapAsync()).Dispose();
        (await OuvrirSmtpAsync()).Dispose();
    }

    // ─── Dossiers ─────────────────────────────────────────────────────────────
    public async Task<List<EmailDossierDto>> GetDossiersAsync()
    {
        using var client = await OuvrirImapAsync();
        var liste = new List<EmailDossierDto>();

        void Ajouter(IMailFolder? f, string? cle)
        {
            if (f == null) return;
            try { f.Status(StatusItems.Unread | StatusItems.Count); } catch { /* compteurs indisponibles */ }
            liste.Add(new EmailDossierDto { Id = f.FullName, Nom = f.Name, Cle = cle, NonLus = f.Unread, Total = f.Count });
        }

        Ajouter(client.Inbox, "inbox");
        Ajouter(Special(client, SpecialFolder.Sent), "sentitems");
        Ajouter(Special(client, SpecialFolder.Drafts), "drafts");
        Ajouter(Special(client, SpecialFolder.Junk), "junkemail");
        Ajouter(Special(client, SpecialFolder.Trash), "deleteditems");
        Ajouter(Special(client, SpecialFolder.Archive), "archive");

        // Autres dossiers personnels (hors spéciaux déjà listés).
        var connus = new HashSet<string>(liste.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var ns in client.PersonalNamespaces)
        {
            foreach (var f in client.GetFolders(ns))
            {
                if (f.Attributes.HasFlag(FolderAttributes.NonExistent) || connus.Contains(f.FullName)) continue;
                if ((f.Attributes & (FolderAttributes.All | FolderAttributes.Trash | FolderAttributes.Junk
                    | FolderAttributes.Sent | FolderAttributes.Drafts | FolderAttributes.Archive)) != 0) continue;
                Ajouter(f, null);
            }
        }

        await client.DisconnectAsync(true);
        return liste;
    }

    private static IMailFolder? Special(ImapClient c, SpecialFolder s)
    {
        try { return c.GetFolder(s); } catch { return null; }
    }

    private IMailFolder ResoudreDossier(ImapClient c, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder == "inbox") return c.Inbox;
        var special = folder switch
        {
            "sentitems" => Special(c, SpecialFolder.Sent),
            "drafts" => Special(c, SpecialFolder.Drafts),
            "junkemail" => Special(c, SpecialFolder.Junk),
            "deleteditems" => Special(c, SpecialFolder.Trash),
            "archive" => Special(c, SpecialFolder.Archive),
            _ => null
        };
        if (special != null) return special;
        try { return c.GetFolder(folder); } catch { return c.Inbox; }
    }

    // ─── Liste paginée ────────────────────────────────────────────────────────
    public async Task<EmailPageDto> GetEmailsAsync(int page, int pageSize, string? search, string folder, bool nonLus)
    {
        using var client = await OuvrirImapAsync();
        var dossier = ResoudreDossier(client, folder);
        await dossier.OpenAsync(FolderAccess.ReadOnly);

        var recherche = search?.Trim();
        const MessageSummaryItems champs = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope
            | MessageSummaryItems.Flags | MessageSummaryItems.BodyStructure | MessageSummaryItems.References;

        IList<IMessageSummary> resumes;
        int total;

        if (!string.IsNullOrEmpty(recherche) || nonLus)
        {
            SearchQuery requete = SearchQuery.All;
            if (nonLus) requete = SearchQuery.NotSeen;
            if (!string.IsNullOrEmpty(recherche))
            {
                var q = SearchQuery.SubjectContains(recherche)
                    .Or(SearchQuery.FromContains(recherche))
                    .Or(SearchQuery.BodyContains(recherche));
                requete = nonLus ? requete.And(q) : q;
            }
            var uids = await dossier.SearchAsync(requete);
            total = uids.Count;
            // Les plus récents d'abord (les UID croissent avec l'arrivée).
            var pageUids = uids.Reverse().Skip((page - 1) * pageSize).Take(pageSize).ToList();
            resumes = pageUids.Count > 0 ? await dossier.FetchAsync(pageUids, champs) : new List<IMessageSummary>();
            resumes = resumes.OrderByDescending(r => r.UniqueId.Id).ToList();
        }
        else
        {
            total = dossier.Count;
            var fin = total - 1 - (page - 1) * pageSize;
            var debut = Math.Max(0, fin - pageSize + 1);
            resumes = fin < 0 ? new List<IMessageSummary>() : await dossier.FetchAsync(debut, fin, champs);
            resumes = resumes.OrderByDescending(r => r.Index).ToList();
        }

        var items = resumes.Select(r =>
        {
            var exp = r.Envelope?.From?.Mailboxes?.FirstOrDefault();
            return new EmailSummaryDto
            {
                Id = EncodeId(dossier.FullName, r.UniqueId),
                De = exp?.Name,
                DeEmail = exp?.Address,
                Objet = r.Envelope?.Subject,
                Apercu = null,
                Date = (r.Envelope?.Date ?? r.InternalDate)?.LocalDateTime,
                EstLu = r.Flags?.HasFlag(MessageFlags.Seen) ?? false,
                HasAttachments = r.Attachments?.Any() ?? false,
                TicketId = null,                       // rempli ensuite (lien partagé en base)
                MessageIdInternet = NettoyerMessageId(r.Envelope?.MessageId),
                ThreadIdInternet = NettoyerMessageId(
                    r.References?.FirstOrDefault() ?? r.Envelope?.InReplyTo ?? r.Envelope?.MessageId)
            };
        }).ToList();

        await client.DisconnectAsync(true);
        return new EmailPageDto { Total = total, Items = items, NextLink = null };
    }

    // ─── Détail d'un message ──────────────────────────────────────────────────
    public async Task<EmailDetailDto> GetEmailByIdAsync(string messageId)
    {
        var (folderPath, uid) = DecodeId(messageId);
        using var client = await OuvrirImapAsync();
        var dossier = client.GetFolder(folderPath);
        await dossier.OpenAsync(FolderAccess.ReadOnly);

        var resume = (await dossier.FetchAsync(new[] { uid }, MessageSummaryItems.Flags)).FirstOrDefault();
        var message = await dossier.GetMessageAsync(uid)
            ?? throw MetierException.Introuvable("E-mail introuvable.");

        var exp = message.From?.Mailboxes?.FirstOrDefault();
        var pj = new List<EmailAttachmentDto>();
        int index = 0;
        foreach (var piece in message.Attachments)
        {
            long taille = 0;
            if (piece is MimePart mp)
            {
                using var compte = new MemoryStream();
                await mp.Content.DecodeToAsync(compte);
                taille = compte.Length;
            }
            pj.Add(new EmailAttachmentDto
            {
                Id = index.ToString(),
                Nom = (piece as MimePart)?.FileName ?? piece.ContentDisposition?.FileName ?? $"piece-{index + 1}",
                TypeMime = piece.ContentType?.MimeType,
                Taille = taille
            });
            index++;
        }

        // Images collées dans le corps (« cid: ») : intégrées en data URI, comme côté Outlook,
        // pour l'affichage et pour que l'agent IA les voie.
        var html = message.HtmlBody;
        if (html != null && html.Contains("cid:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var image in message.BodyParts.OfType<MimePart>()
                         .Where(p => !string.IsNullOrEmpty(p.ContentId) && p.ContentType.MediaType == "image"))
            {
                using var flux = new MemoryStream();
                await image.Content.DecodeToAsync(flux);
                var dataUri = $"data:{image.ContentType.MimeType};base64,{Convert.ToBase64String(flux.ToArray())}";
                html = html.Replace($"cid:{image.ContentId.Trim('<', '>')}", dataUri, StringComparison.OrdinalIgnoreCase);
            }
        }

        await client.DisconnectAsync(true);
        return new EmailDetailDto
        {
            Id = messageId,
            De = exp?.Name,
            DeEmail = exp?.Address,
            Objet = message.Subject,
            Date = message.Date.LocalDateTime,
            EstLu = resume?.Flags?.HasFlag(MessageFlags.Seen) ?? true,
            HasAttachments = pj.Count > 0,
            CorpsHtml = html,
            CorpsTexte = message.HtmlBody == null ? message.TextBody : null,
            PiecesJointes = pj,
            A = Contacts(message.To),
            Cc = Contacts(message.Cc),
            MessageIdInternet = NettoyerMessageId(message.MessageId),
            ThreadIdInternet = NettoyerMessageId(
                message.References?.FirstOrDefault() ?? message.InReplyTo ?? message.MessageId)
        };
    }

    private static List<EmailContactDto> Contacts(InternetAddressList liste) =>
        liste.Mailboxes.Select(m => new EmailContactDto { Nom = m.Name, Email = m.Address }).ToList();

    // ─── Téléchargement d'une pièce jointe ────────────────────────────────────
    public async Task<(byte[] Contenu, string Nom, string Type)> TelechargerPieceJointeAsync(string messageId, string attachmentId)
    {
        if (!int.TryParse(attachmentId, out var index))
            throw MetierException.Introuvable("Pièce jointe introuvable.");

        var (folderPath, uid) = DecodeId(messageId);
        using var client = await OuvrirImapAsync();
        var dossier = client.GetFolder(folderPath);
        await dossier.OpenAsync(FolderAccess.ReadOnly);
        var message = await dossier.GetMessageAsync(uid)
            ?? throw MetierException.Introuvable("E-mail introuvable.");

        var pieces = message.Attachments.ToList();
        if (index < 0 || index >= pieces.Count || pieces[index] is not MimePart part)
            throw MetierException.Introuvable("Pièce jointe introuvable.");

        using var flux = new MemoryStream();
        await part.Content.DecodeToAsync(flux);
        await client.DisconnectAsync(true);
        return (flux.ToArray(), part.FileName ?? $"piece-{index + 1}", part.ContentType?.MimeType ?? "application/octet-stream");
    }

    // ─── Lu / non lu ─────────────────────────────────────────────────────────
    public async Task MarquerCommeLuAsync(string messageId, bool lu)
    {
        var (folderPath, uid) = DecodeId(messageId);
        using var client = await OuvrirImapAsync();
        var dossier = client.GetFolder(folderPath);
        await dossier.OpenAsync(FolderAccess.ReadWrite);
        if (lu) await dossier.AddFlagsAsync(new[] { uid }, MessageFlags.Seen, true);
        else await dossier.RemoveFlagsAsync(new[] { uid }, MessageFlags.Seen, true);
        await client.DisconnectAsync(true);
    }

    // ─── Déplacer ─────────────────────────────────────────────────────────────
    public async Task DeplacerAsync(string messageId, string dossierId)
    {
        var (folderPath, uid) = DecodeId(messageId);
        using var client = await OuvrirImapAsync();
        var source = client.GetFolder(folderPath);
        var cible = ResoudreDossier(client, dossierId);
        await source.OpenAsync(FolderAccess.ReadWrite);
        await source.MoveToAsync(uid, cible);
        await client.DisconnectAsync(true);
    }

    // ─── Répondre / transférer / envoyer ─────────────────────────────────────
    public async Task RepondreAsync(string messageId, EmailRepondreRequest request)
    {
        var (folderPath, uid) = DecodeId(messageId);
        using var client = await OuvrirImapAsync();
        var dossier = client.GetFolder(folderPath);
        await dossier.OpenAsync(FolderAccess.ReadOnly);
        var original = await dossier.GetMessageAsync(uid)
            ?? throw MetierException.Introuvable("E-mail introuvable.");
        await client.DisconnectAsync(true);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("", _c.Email));
        message.To.AddRange(original.From.Mailboxes);                 // répondre à l'expéditeur
        if (request.Tous)
        {
            message.To.AddRange(original.To.Mailboxes.Where(m => !m.Address.Equals(_c.Email, StringComparison.OrdinalIgnoreCase)));
            message.Cc.AddRange(original.Cc.Mailboxes.Where(m => !m.Address.Equals(_c.Email, StringComparison.OrdinalIgnoreCase)));
        }
        message.Subject = Prefixer(original.Subject, "Re:");
        message.InReplyTo = original.MessageId;
        if (!string.IsNullOrEmpty(original.MessageId)) message.References.Add(original.MessageId);
        message.Body = CorpsAvecCitation(ValiderMessage(request.Message), original);

        await EnvoyerMimeAsync(message);
    }

    public async Task TransfererAsync(string messageId, EmailEnvoyerRequest request)
    {
        var destinataires = ValiderAdresses(request.Destinataires);
        var (folderPath, uid) = DecodeId(messageId);
        using var client = await OuvrirImapAsync();
        var dossier = client.GetFolder(folderPath);
        await dossier.OpenAsync(FolderAccess.ReadOnly);
        var original = await dossier.GetMessageAsync(uid)
            ?? throw MetierException.Introuvable("E-mail introuvable.");
        await client.DisconnectAsync(true);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("", _c.Email));
        foreach (var a in destinataires) message.To.Add(MailboxAddress.Parse(a));
        message.Subject = Prefixer(original.Subject, "Fwd:");
        message.Body = CorpsAvecCitation(request.Message ?? "", original);

        await EnvoyerMimeAsync(message);
    }

    public async Task EnvoyerAsync(EmailEnvoyerRequest request)
    {
        var destinataires = ValiderAdresses(request.Destinataires);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("", _c.Email));
        foreach (var a in destinataires) message.To.Add(MailboxAddress.Parse(a));
        foreach (var a in (request.Cc ?? new()).Where(x => !string.IsNullOrWhiteSpace(x))) message.Cc.Add(MailboxAddress.Parse(a.Trim()));
        message.Subject = request.Objet?.Trim() ?? "";
        message.Body = new TextPart("plain") { Text = ValiderMessage(request.Message) };

        await EnvoyerMimeAsync(message);
    }

    /// <summary>Lien e-mail ↔ ticket : non pris en charge en IMAP (pas de catégories). Sans effet.</summary>
    public Task LierTicketAsync(string messageId, int ticketId) => Task.CompletedTask;

    /// <summary>Normalise un Message-ID RFC pour une comparaison fiable (sans chevrons, minuscules).</summary>
    public static string? NettoyerMessageId(string? messageId) => LienEmailTicket.NettoyerId(messageId);

    // ─── Utilitaires ──────────────────────────────────────────────────────────
    private async Task EnvoyerMimeAsync(MimeMessage message)
    {
        using var smtp = await OuvrirSmtpAsync();
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
    }

    private static MimeEntity CorpsAvecCitation(string texte, MimeMessage original)
    {
        var builder = new BodyBuilder();
        var citation = original.TextBody ?? "";
        builder.TextBody = $"{texte}\n\n----- Message d'origine -----\n{citation}";
        return builder.ToMessageBody();
    }

    private static string Prefixer(string? sujet, string prefixe)
    {
        sujet ??= "";
        return sujet.StartsWith(prefixe, StringComparison.OrdinalIgnoreCase) ? sujet : $"{prefixe} {sujet}".Trim();
    }

    private static string ValiderMessage(string? message)
    {
        var texte = message?.Trim() ?? "";
        if (texte.Length == 0) throw new MetierException("Le message est vide.");
        return texte;
    }

    private static List<string> ValiderAdresses(List<string>? adresses)
    {
        var liste = (adresses ?? new()).Select(a => a?.Trim() ?? "").Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (liste.Count == 0) throw new MetierException("Indiquez au moins un destinataire.");
        var invalide = liste.FirstOrDefault(a => !MailboxAddress.TryParse(a, out _));
        if (invalide != null) throw new MetierException($"Adresse e-mail invalide : « {invalide} ».");
        return liste;
    }

    private static string EncodeId(string folder, UniqueId uid)
    {
        var brut = $"{folder}{Sep}{uid.Id}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(brut))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static (string folder, UniqueId uid) DecodeId(string id)
    {
        var s = id.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        var brut = Encoding.UTF8.GetString(Convert.FromBase64String(s));
        var i = brut.LastIndexOf(Sep);
        if (i < 0 || !uint.TryParse(brut[(i + 1)..], out var n))
            throw MetierException.Introuvable("E-mail introuvable.");
        return (brut[..i], new UniqueId(n));
    }
}
