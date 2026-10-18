namespace NexoRuta.Infrastructure.Correo;

public sealed class ConfiguracionSmtp
{
    public const string ConfigurationSection = "Email:Smtp";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "NexoRuta";
}
