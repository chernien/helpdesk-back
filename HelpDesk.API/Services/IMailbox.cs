using HelpDesk.API.DTOs;

namespace HelpDesk.API.Services;

/// <summary>
/// Opérations d'une boîte mail, quelle que soit la source :
/// boîte partagée (Microsoft Graph) ou boîte personnelle (IMAP/SMTP).
/// Permet d'aiguiller chaque utilisateur vers SA boîte.
/// </summary>
public interface IMailbox
{
    Task<EmailPageDto> GetEmailsAsync(int page, int pageSize, string? search, string folder, bool nonLus);
    Task<EmailDetailDto> GetEmailByIdAsync(string messageId);
    Task<List<EmailDossierDto>> GetDossiersAsync();
    Task MarquerCommeLuAsync(string messageId, bool lu);
    Task DeplacerAsync(string messageId, string dossierId);
    Task RepondreAsync(string messageId, EmailRepondreRequest request);
    Task TransfererAsync(string messageId, EmailEnvoyerRequest request);
    Task EnvoyerAsync(EmailEnvoyerRequest request);
    Task LierTicketAsync(string messageId, int ticketId);
    Task<(byte[] Contenu, string Nom, string Type)> TelechargerPieceJointeAsync(string messageId, string attachmentId);
}

/// <summary>Finalité de chiffrement du mot de passe (identique au chiffrement et au déchiffrement).</summary>
public static class MailboxProtection
{
    public const string Purpose = "HelpDesk.Mailbox.Secret.v1";
}

/// <summary>Paramètres d'une boîte IMAP/SMTP (déchiffrés), passés au service IMAP.</summary>
public record MailboxConnexion(
    string Email, string Login, string MotDePasse,
    string ImapHost, int ImapPort, bool ImapSsl,
    string SmtpHost, int SmtpPort, bool SmtpStartTls);
