using HelpDesk.API.Data;
using HelpDesk.API.Exceptions;
using HelpDesk.API.Hubs;
using HelpDesk.API.Repositories;
using HelpDesk.API.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// --- Base de donnees ---
builder.Services.AddDbContext<HelpDeskContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("HelpDeskDb")));

// --- Injection de dependances (Repositories & Services) ---
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ITicketRepository, TicketRepository>();
builder.Services.AddScoped<IProduitRepository, ProduitRepository>();
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<IClientRepository, ClientRepository>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddSingleton<IFichierStockage, FichierStockageDisque>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ITicketService, TicketService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IProduitService, ProduitService>();
builder.Services.AddScoped<IUtilisateurContexteService, UtilisateurContexteService>();

// --- Authentification JWT ---
// Les secrets ne sont jamais dans appsettings.json (publie sur GitHub) :
// en local ils sont dans appsettings.Development.json (non versionne), en production
// dans les variables d'environnement (ConnectionStrings__HelpDeskDb, Jwt__Key).
if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("HelpDeskDb")) ||
    (builder.Configuration["Jwt:Key"]?.Length ?? 0) < 32)
    throw new InvalidOperationException(
        "Configuration manquante : copiez appsettings.Development.example.json en appsettings.Development.json " +
        "et renseignez ConnectionStrings:HelpDeskDb et Jwt:Key (32 caractères minimum).");

var jwt = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!))
        };

        // SignalR envoie le token en query string (?access_token=...) sur les WebSockets
        options.Events = new JwtBearerEvents
        {
            // Un jeton reste valide 8 h : on verifie a chaque requete que le compte
            // existe toujours et n'est pas desactive (suppression / desactivation immediates).
            OnTokenValidated = async context =>
            {
                var idClaim = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                var db = context.HttpContext.RequestServices.GetRequiredService<HelpDeskContext>();
                var actif = int.TryParse(idClaim, out var id) &&
                            await db.Authentication.AnyAsync(u => u.Id == id && u.Enabled != false);
                if (!actif)
                    context.Fail("Compte supprimé ou désactivé.");
            },
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// --- SignalR ---
builder.Services.AddSignalR();

// --- CORS (Angular) ---
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Angular", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()); // requis pour SignalR
});

// --- Controllers & Swagger ---
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "HelpDesk API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Entrez : Bearer {votre token JWT}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// --- Regles metier violees -> reponse { message } avec le bon code HTTP ---
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (MetierException ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { message = ex.Message });
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Angular");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<NotificationHub>("/hubs/notifications");

app.Run();
