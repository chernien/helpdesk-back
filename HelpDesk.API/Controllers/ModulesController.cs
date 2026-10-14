using HelpDesk.API.Constants;
using HelpDesk.API.DTOs;
using HelpDesk.API.Exceptions;
using HelpDesk.API.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.API.Controllers;

/// <summary>Modules des tickets : liste pour tous, gestion (ajout/suppression) réservée à l'admin.</summary>
[ApiController]
[Route("api/modules")]
[Authorize]
public class ModulesController : ControllerBase
{
    private readonly IModuleRepository _repository;
    public ModulesController(IModuleRepository repository) => _repository = repository;

    /// <summary>Liste des modules (pour le menu déroulant du formulaire de ticket).</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ModuleDto>>> Liste()
    {
        var modules = await _repository.ListAsync();
        return Ok(modules.Select(m => new ModuleDto { Id = m.Id, Nom = m.Nom }));
    }

    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<ModuleDto>> Ajouter(ModuleCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Nom))
            throw new MetierException("Le nom du module est requis.");
        var module = await _repository.AjouterAsync(request.Nom);
        return Ok(new ModuleDto { Id = module.Id, Nom = module.Nom });
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Supprimer(int id)
    {
        // Comme pour les produits : un module affecté à des clients ne peut pas disparaître de la liste.
        var clients = await _repository.NombreClientsAsync(id);
        if (clients > 0)
            throw new MetierException($"Ce module est affecté à {clients} client(s) : retirez-le d'abord de leurs comptes.");
        var ok = await _repository.SupprimerAsync(id);
        return ok ? NoContent() : NotFound();
    }
}
