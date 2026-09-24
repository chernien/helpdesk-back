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
}

public class AuthService : IAuthService
{
    private readonly IUserRepository _userRepository;
    private readonly IConfiguration _configuration;

    public AuthService(IUserRepository userRepository, IConfiguration configuration)
    {
        _userRepository = userRepository;
        _configuration = configuration;
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
