namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

public sealed class PasswordRecoveryOptions
{
    public string ResetPageUrl { get; set; } = "";
    public int TokenLifespanSeconds { get; set; } = 900;
}
