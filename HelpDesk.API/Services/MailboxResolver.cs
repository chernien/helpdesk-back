using HelpDesk.API.Repositories;
using Microsoft.AspNetCore.DataProtection;

namespace HelpDesk.API.Services;

public interface IMailboxResolver
{
    /// <summary>
    /// La boîte de l'utilisateur : sa boîte Outlook (Graph, app-only) ou IMAP connectée,
    /// sinon la boîte partagée (Graph).
    /// </summary>
    Task<IMailbox> PourAsync(UtilisateurContexte user);
}

public class MailboxResolver : IMailboxResolver
{
    private readonly IMailboxRepository _repository;
    private readonly IDataProtector _protector;
    private readonly GraphMailService _partagee;

    public MailboxResolver(IMailboxRepository repository, IDataProtectionProvider dataProtection, GraphMailService partagee)
    {
        _repository = repository;
        _protector = dataProtection.CreateProtector(MailboxProtection.Purpose);
        _partagee = partagee;
    }

    public async Task<IMailbox> PourAsync(UtilisateurContexte user)
    {
        var mb = await _repository.GetByUserAsync(user.Id);
        if (mb == null) return _partagee; // repli : boîte partagée (Microsoft Graph)

        // Outlook externe/personnel : accès via OAuth délégué (jeton de rafraîchissement chiffré).
        if (mb.Provider == "outlook-oauth")
            return _partagee.PourBoiteDeleguee(mb.Email, _protector.Unprotect(mb.SecretChiffre));

        // Outlook / Microsoft 365 du tenant : accès via l'app Graph (app-only), sans mot de passe.
        if (mb.Provider == "outlook")
            return _partagee.PourBoite(mb.Email);

        // Gmail / IMAP personnalisé : connexion IMAP/SMTP avec le mot de passe (déchiffré).
        var motDePasse = _protector.Unprotect(mb.SecretChiffre);
        return new ImapMailService(new MailboxConnexion(
            mb.Email, mb.Login, motDePasse,
            mb.ImapHost, mb.ImapPort, mb.ImapSsl,
            mb.SmtpHost, mb.SmtpPort, mb.SmtpStartTls));
    }
}
