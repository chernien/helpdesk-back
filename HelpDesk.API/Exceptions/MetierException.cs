namespace HelpDesk.API.Exceptions;

/// <summary>
/// Regle metier violee. Convertie en reponse HTTP { message } par
/// le gestionnaire d'erreurs global (Program.cs).
/// </summary>
public class MetierException : Exception
{
    public int StatusCode { get; }

    public MetierException(string message, int statusCode = StatusCodes.Status400BadRequest)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public static MetierException Conflit(string message) => new(message, StatusCodes.Status409Conflict);
    public static MetierException Interdit(string message) => new(message, StatusCodes.Status403Forbidden);
    public static MetierException Introuvable(string message) => new(message, StatusCodes.Status404NotFound);
}
