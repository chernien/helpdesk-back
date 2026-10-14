using HelpDesk.API.DTOs;
using HelpDesk.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.API.Controllers;

/// <summary>Societes clientes et leurs contacts (tables Divalto CLI et T2).</summary>
[ApiController]
[Route("api/clients")]
[Authorize(Roles = "ROLE_ADMIN,ROLE_USER")]
public class ClientsController : ControllerBase
{
    private readonly IClientService _clientService;
    private readonly IUserService _userService;

    public ClientsController(IClientService clientService, IUserService userService)
    {
        _clientService = clientService;
        _userService = userService;
    }

    /// <summary>Recherche de societes par nom ou code tiers (50 resultats max).</summary>
    [HttpGet("societes")]
    public async Task<ActionResult<List<SocieteClientDto>>> RechercherSocietes([FromQuery] string? recherche)
    {
        return Ok(await _clientService.RechercherSocietesAsync(recherche));
    }

    /// <summary>Societe du contact portant cet email (204 si aucun contact Divalto ne correspond).</summary>
    [HttpGet("societes/par-email")]
    public async Task<ActionResult<SocieteClientDto>> SocieteParEmail([FromQuery] string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return NoContent();
        var societe = await _clientService.TrouverSocieteParEmailAsync(email);
        return societe is null ? NoContent() : Ok(societe);
    }

    /// <summary>Contacts (emails) d'une societe.</summary>
    /// <summary>Sociétés (codes tiers) où ce contact a « Profil Admin helpdesk » coché dans Divalto (pré-coche le formulaire du compte).</summary>
    [HttpGet("admin")]
    public async Task<ActionResult<List<string>>> SocietesAdmin([FromQuery] string? email) =>
        Ok(await _clientService.TiersAdminAsync(email ?? ""));

    [HttpGet("societes/{tiers}/contacts")]
    public async Task<ActionResult<List<ContactClientDto>>> Contacts(string tiers)
    {
        return Ok(await _clientService.ContactsAsync(tiers));
    }

    /// <summary>Cree le compte Helpdesk d'un contact qui n'en a pas encore (formulaire de ticket).</summary>
    [HttpPost("comptes")]
    public async Task<ActionResult<UserDto>> CreerCompteContact(CreerCompteContactRequest request)
    {
        return Ok(await _userService.CreerCompteContactAsync(request));
    }
}
