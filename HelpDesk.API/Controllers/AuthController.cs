using HelpDesk.API.DTOs;
using HelpDesk.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IWebHostEnvironment _env;

    public AuthController(IAuthService authService, IWebHostEnvironment env)
    {
        _authService = authService;
        _env = env;
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request)
    {
        var response = await _authService.LoginAsync(request);
        if (response == null)
            return Unauthorized(new { message = "Identifiants invalides ou compte désactivé." });
        return Ok(response);
    }

    /// <summary>TEMPORAIRE / DEV uniquement : (re)définit le mot de passe d'un compte. À retirer ensuite.</summary>
    [HttpPost("dev-set-password")]
    public async Task<IActionResult> DevSetPassword(LoginRequest request)
    {
        if (!_env.IsDevelopment())
            return NotFound();
        var ok = await _authService.DevSetPasswordAsync(request.Username, request.Password);
        return ok
            ? Ok(new { message = $"Mot de passe défini pour {request.Username}." })
            : NotFound(new { message = "Utilisateur introuvable." });
    }
}
