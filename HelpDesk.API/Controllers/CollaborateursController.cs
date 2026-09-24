using HelpDesk.API.Constants;
using HelpDesk.API.DTOs;
using HelpDesk.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.API.Controllers;

/// <summary>
/// Collaborateurs d'un produit, pour confier un ticket.
/// Controleur separe de UsersController : les attributs [Authorize] d'une classe et
/// d'une methode se cumulent, un endpoint ouvert aux collaborateurs ne peut donc pas
/// vivre dans un controleur reserve aux administrateurs.
/// </summary>
[ApiController]
[Route("api/users/collaborateurs")]
[Authorize(Roles = $"{Roles.Admin},{Roles.User}")]
public class CollaborateursController : ControllerBase
{
    private readonly IUserService _userService;

    public CollaborateursController(IUserService userService)
    {
        _userService = userService;
    }

    [HttpGet]
    public async Task<ActionResult<List<CollaborateurDto>>> Get([FromQuery] string produit)
    {
        return Ok(await _userService.CollaborateursDuProduitAsync(produit));
    }
}
