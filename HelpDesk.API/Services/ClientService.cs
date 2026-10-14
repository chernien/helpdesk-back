using HelpDesk.API.DTOs;
using HelpDesk.API.Repositories;

namespace HelpDesk.API.Services;

public interface IClientService
{
    Task<List<SocieteClientDto>> RechercherSocietesAsync(string? recherche);
    Task<List<ContactClientDto>> ContactsAsync(string tiers);
    Task<ContactSocieteDto?> TrouverContactAsync(string tiers, string email);
    Task<SocieteClientDto?> TrouverSocieteAsync(string tiers);
    Task<SocieteClientDto?> TrouverSocieteParEmailAsync(string email);
    Task<List<SocieteDivaltoDto>> SocietesParEmailAsync(string email);
    /// <summary>Sociétés (codes tiers) où ce contact a « Profil Admin helpdesk » coché dans Divalto (T2.U_FLAG_ADMIN = 2).</summary>
    Task<List<string>> TiersAdminAsync(string email);
}

public class ClientService : IClientService
{
    private const int MaxResultats = 50;

    private readonly IClientRepository _clientRepository;
    private readonly IUserRepository _userRepository;
    private readonly string _dossier;

    public ClientService(IClientRepository clientRepository, IUserRepository userRepository, IConfiguration configuration)
    {
        _clientRepository = clientRepository;
        _userRepository = userRepository;
        // Dossier Divalto des clients (CLI.DOS / T2.DOS). "1" en production.
        _dossier = configuration["Divalto:Dossier"] ?? "1";
    }

    public Task<List<SocieteClientDto>> RechercherSocietesAsync(string? recherche) =>
        _clientRepository.RechercherSocietesAsync(_dossier, recherche, MaxResultats);

    /// <summary>Contacts de la societe, chacun marque s'il a deja un compte Helpdesk.</summary>
    public async Task<List<ContactClientDto>> ContactsAsync(string tiers)
    {
        var contacts = await _clientRepository.ContactsAsync(_dossier, tiers);
        var avecCompte = await _userRepository.EmailsAvecCompteAsync(contacts.Select(c => c.Email));
        foreach (var c in contacts)
            c.ACompte = avecCompte.Contains(c.Email.Trim().ToLower());
        return contacts;
    }

    public Task<ContactSocieteDto?> TrouverContactAsync(string tiers, string email) =>
        _clientRepository.TrouverContactAsync(_dossier, tiers, email);

    public Task<SocieteClientDto?> TrouverSocieteAsync(string tiers) =>
        _clientRepository.TrouverSocieteAsync(_dossier, tiers);

    /// <summary>Societe dont un contact (T2) porte cet email : pre-remplit un ticket cree depuis un e-mail.</summary>
    public Task<SocieteClientDto?> TrouverSocieteParEmailAsync(string email) =>
        _clientRepository.TrouverSocieteParEmailAsync(_dossier, email);

    /// <summary>Sociétés Divalto du contact qui porte cet e-mail (vide = inconnu dans Divalto).</summary>
    public Task<List<SocieteDivaltoDto>> SocietesParEmailAsync(string email) =>
        _clientRepository.SocietesParEmailAsync(_dossier, email);

    public async Task<List<string>> TiersAdminAsync(string email) =>
        string.IsNullOrWhiteSpace(email) ? new List<string>() : await _clientRepository.TiersAdminParEmailAsync(_dossier, email);
}
