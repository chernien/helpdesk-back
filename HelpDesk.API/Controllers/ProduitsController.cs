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
}
