using HelpDesk.API.Data;
using HelpDesk.API.DTOs;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Repositories;

/// <summary>
/// Lecture des clients depuis les tables Divalto :
/// CLI (societes, filtrees par dossier DOS) et T2 (contacts, jointure sur TIERS).
/// Les colonnes sont en char : on applique RTRIM partout.
/// </summary>
public interface IClientRepository
{
    Task<List<SocieteClientDto>> RechercherSocietesAsync(string dossier, string? recherche, int max);
    Task<List<ContactClientDto>> ContactsAsync(string dossier, string tiers);
    Task<ContactSocieteDto?> TrouverContactAsync(string dossier, string tiers, string email);
    Task<SocieteClientDto?> TrouverSocieteAsync(string dossier, string tiers);
}

public class ClientRepository : IClientRepository
{
    private readonly HelpDeskContext _context;

    public ClientRepository(HelpDeskContext context)
    {
        _context = context;
    }

    public Task<List<SocieteClientDto>> RechercherSocietesAsync(string dossier, string? recherche, int max)
    {
        // La base est en collation binaire (sensible a la casse) : on compare en majuscules.
        var motif = $"%{(recherche ?? "").Trim().ToUpperInvariant()}%";

        return _context.Database.SqlQuery<SocieteClientDto>($"""
            SELECT TOP ({max}) RTRIM(TIERS) AS Tiers, RTRIM(NOM) AS Nom
            FROM CLI
            WHERE DOS = {dossier}
              AND RTRIM(NOM) <> ''
              AND (UPPER(NOM) LIKE {motif} OR UPPER(TIERS) LIKE {motif})
            ORDER BY NOM
            """).ToListAsync();
    }

    public Task<List<ContactClientDto>> ContactsAsync(string dossier, string tiers)
    {
        return _context.Database.SqlQuery<ContactClientDto>($"""
            SELECT LOWER(RTRIM(EMAIL)) AS Email, RTRIM(NOM) AS Nom, RTRIM(PRENOM) AS Prenom,
                   CAST(0 AS bit) AS ACompte -- renseigne ensuite par ClientService
            FROM T2
            WHERE DOS = {dossier}
              AND TIERS = {tiers}
              AND EMAIL IS NOT NULL
              AND RTRIM(EMAIL) <> ''
            ORDER BY EMAIL
            """).ToListAsync();
    }

    public async Task<ContactSocieteDto?> TrouverContactAsync(string dossier, string tiers, string email)
    {
        var resultats = await _context.Database.SqlQuery<ContactSocieteDto>($"""
            SELECT TOP (1) RTRIM(c.NOM) AS SocieteNom, LOWER(RTRIM(t.EMAIL)) AS Email
            FROM CLI c
            JOIN T2 t ON t.TIERS = c.TIERS AND t.DOS = c.DOS
            WHERE c.DOS = {dossier}
              AND c.TIERS = {tiers}
              -- Divalto peut contenir AMINE@MEDIASOFT.TN : comparaison sans tenir compte de la casse
              AND LOWER(RTRIM(t.EMAIL)) = LOWER(LTRIM(RTRIM({email})))
            """).ToListAsync();

        return resultats.FirstOrDefault();
    }

    public async Task<SocieteClientDto?> TrouverSocieteAsync(string dossier, string tiers)
    {
        var resultats = await _context.Database.SqlQuery<SocieteClientDto>($"""
            SELECT TOP (1) RTRIM(TIERS) AS Tiers, RTRIM(NOM) AS Nom
            FROM CLI
            WHERE DOS = {dossier} AND TIERS = {tiers}
            """).ToListAsync();

        return resultats.FirstOrDefault();
    }
}
