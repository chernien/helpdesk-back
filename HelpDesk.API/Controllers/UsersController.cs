using HelpDesk.API.Constants;
using HelpDesk.API.DTOs;
using HelpDesk.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace HelpDesk.API.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Roles = Roles.Admin)]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;

    public UsersController(IUserService userService)
    {
        _userService = userService;
    }

    /// <summary>Liste paginee des comptes (?role=ROLE_CLIENT&amp;recherche=...).</summary>
    [HttpGet]
    public async Task<ActionResult<UserPageDto>> Rechercher(
        [FromQuery] string? role,
        [FromQuery] string? recherche,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        return Ok(await _userService.RechercherAsync(role, recherche, page, pageSize));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<UserDto>> GetUser(int id)
    {
        return Ok(await _userService.GetByIdAsync(id));
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> CreateUser(CreateUserRequest request)
    {
        var user = await _userService.CreateAsync(request);
        return CreatedAtAction(nameof(GetUser), new { id = user.Id }, user);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<UserDto>> UpdateUser(int id, UpdateUserRequest request)
    {
        return Ok(await _userService.UpdateAsync(id, request, AdminId));
    }

    /// <summary>
    /// Suppression definitive d'un ou plusieurs comptes (tout ou rien).
    /// POST plutot que DELETE : un corps de requete DELETE est mal supporte par les proxys.
    /// </summary>
    [HttpPost("suppression")]
    public async Task<ActionResult<SuppressionUsersResultDto>> Supprimer(SuppressionUsersRequest request)
    {
        return Ok(await _userService.SupprimerAsync(request.Ids, AdminId));
    }

    private int AdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
