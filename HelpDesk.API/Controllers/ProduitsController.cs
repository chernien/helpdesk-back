using HelpDesk.API.Constants;
using HelpDesk.API.DTOs;
using HelpDesk.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.API.Controllers;

[ApiController]
[Route("api/produits")]
[Authorize]
public class ProduitsController : ControllerBase
{
    private readonly IProduitService _produitService;

    public ProduitsController(IProduitService produitService)
    {
        _produitService = produitService;
    }

    [HttpGet]
    public async Task<ActionResult<List<ProduitDto>>> GetProduits()
    {
        return Ok(await _produitService.GetAllAsync());
    }

    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<ProduitDto>> Ajouter(ProduitCreateRequest request)
    {
        return Ok(await _produitService.AjouterAsync(request.Nom));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Supprimer(int id)
    {
        await _produitService.SupprimerAsync(id);
        return NoContent();
    }
}
