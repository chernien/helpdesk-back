using HelpDesk.API.Data;
using HelpDesk.API.Models;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Repositories;

public interface IMailboxRepository
{
    Task<Mailbox?> GetByUserAsync(int userId);
    /// <summary>Crée ou remplace la boîte connectée de l'utilisateur.</summary>
    Task EnregistrerAsync(Mailbox mailbox);
    Task SupprimerAsync(int userId);
    /// <summary>Règle l'agent IA (actif, auto-assignation, modèle ; null = automatique) sur la boîte de l'utilisateur. Vrai si une boîte existe.</summary>
    Task<bool> DefinirAgentAsync(int userId, bool actif, bool autoAssign, string? modele);
    /// <summary>Utilisateurs dont l'agent IA est actif, avec leur auto-assignation et leur modèle (tâche de fond).</summary>
    Task<List<(int UserId, bool AutoAssign, string? Modele)>> AgentsActifsAsync();
}

public class MailboxRepository : IMailboxRepository
{
    private readonly HelpDeskContext _context;

    public MailboxRepository(HelpDeskContext context) => _context = context;

    public Task<Mailbox?> GetByUserAsync(int userId) =>
        _context.Mailbox.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == userId);

    public async Task EnregistrerAsync(Mailbox mailbox)
    {
        var existante = await _context.Mailbox.FirstOrDefaultAsync(m => m.UserId == mailbox.UserId);
        if (existante == null)
        {
            _context.Mailbox.Add(mailbox);
        }
        else
        {
            existante.Provider = mailbox.Provider;
            existante.Email = mailbox.Email;
            existante.Login = mailbox.Login;
            existante.SecretChiffre = mailbox.SecretChiffre;
            existante.ImapHost = mailbox.ImapHost;
            existante.ImapPort = mailbox.ImapPort;
            existante.ImapSsl = mailbox.ImapSsl;
            existante.SmtpHost = mailbox.SmtpHost;
            existante.SmtpPort = mailbox.SmtpPort;
            existante.SmtpStartTls = mailbox.SmtpStartTls;
            existante.DateCreation = mailbox.DateCreation;
        }
        await _context.SaveChangesAsync();
    }

    public async Task SupprimerAsync(int userId)
    {
        var mb = await _context.Mailbox.FirstOrDefaultAsync(m => m.UserId == userId);
        if (mb == null) return;
        _context.Mailbox.Remove(mb);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> DefinirAgentAsync(int userId, bool actif, bool autoAssign, string? modele)
    {
        var mb = await _context.Mailbox.FirstOrDefaultAsync(m => m.UserId == userId);
        if (mb == null) return false;
        mb.AgentActif = actif;
        mb.AgentAutoAssign = autoAssign;
        mb.AgentModele = modele;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<List<(int UserId, bool AutoAssign, string? Modele)>> AgentsActifsAsync() =>
        (await _context.Mailbox.AsNoTracking().Where(m => m.AgentActif)
            .Select(m => new { m.UserId, m.AgentAutoAssign, m.AgentModele }).ToListAsync())
            .Select(x => (x.UserId, x.AgentAutoAssign, x.AgentModele)).ToList();
}
