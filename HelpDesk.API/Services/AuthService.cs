using HelpDesk.API.DTOs;
using HelpDesk.API.Repositories;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace HelpDesk.API.Services;

public interface IAuthService
{
    Task<LoginResponse?> LoginAsync(LoginRequest request);
    /// <summary>DEV uniquement : (re)définit le mot de passe d'un compte, haché avec le même BCrypt que la connexion.</summary>
    Task<bool> DevSetPasswordAsync(string username, string password);
}

public class AuthService : IAuthService
{
    private readonly IUserRepository _userRepository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthService> _logger;

    public AuthService(IUserRepository userRepository, IConfiguration configuration, ILogger<AuthService> logger)
    {
        _userRepository = userRepository;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request)
    {
        var user = await _userRepository.GetByUsernameAsync(request.Username);

        if (user == null)
            return null;

        // Compte desactive
        if (user.Enabled == false)
            return null;

        // Les mots de passe existants sont haches en BCrypt (60 caracteres)
        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.Password))
            return null;

        user.Connected = true;
        user.NombreConnexion++;
        await _userRepository.SaveChangesAsync();

        return new LoginResponse
        {
            Token = GenerateJwt(user.Id, user.Username ?? "", user.Role ?? "", user.NomComplet),
            User = UserService.ToDto(user)
        };
    }

    public async Task<bool> DevSetPasswordAsync(string username, string password)
    {
        var user = await _userRepository.GetByUsernameAsync(username);
        if (user == null) return false;

        // Haché avec EXACTEMENT le même BCrypt que celui utilisé par la connexion → cohérence garantie.
        user.Password = BCrypt.Net.BCrypt.HashPassword(password, 11);
        user.Enabled = true;
        await _userRepository.SaveChangesAsync();
        _logger.LogWarning("DEV: mot de passe redéfini pour '{U}' (hashLen={H})", username, user.Password.Length);
        return true;
    }

    private string GenerateJwt(int id, string username, string role, string nom)
    {
        var jwt = _configuration.GetSection("Jwt");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, id.ToString()),
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role),
            new Claim("nom", nom)
        };

        var token = new JwtSecurityToken(
            issuer: jwt["Issuer"],
            audience: jwt["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(double.Parse(jwt["ExpireHours"] ?? "8")),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
