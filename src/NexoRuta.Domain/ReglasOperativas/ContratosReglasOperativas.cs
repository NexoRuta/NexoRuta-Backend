namespace NexoRuta.Domain.ReglasOperativas;

public enum EstadoVersionReglasOperativas { Borrador, Publicada }
public enum TipoPruebaEntrega { FirmaEnPantalla, Fotografia, NombreReceptor, DocumentoReceptor }
public enum PoliticaDevolucionAlAgotarIntentos { DecisionOperativa, IniciarAutomaticamente }

public static class ReglasOperativas
{
    public const int MotiveCodeMaxLength = 40;
    public const int MotiveDescriptionMaxLength = 120;
    public const int MaxDeliveryAttempts = 100;
    public const int MaxDurationMinutes = 525_600;
    public const int PublicationNumberInitial = 1;

    public static class Errors
    {
        public const string DraftConflict = "operational_rules.draft_conflict";
        public const string DraftAlreadyExists = "operational_rules.draft_already_exists";
        public const string Incomplete = "operational_rules.incomplete";
    }
}

public sealed class ConflictoReglasOperativasException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class ReglasOperativasIncompletasException(IReadOnlyList<string> camposIncompletos)
    : InvalidOperationException("El borrador debe tener una configuración completa antes de publicarse.")
{
    public string Code => ReglasOperativas.Errors.Incomplete;
    public IReadOnlyList<string> CamposIncompletos { get; } = camposIncompletos;
}

public sealed record MotivoNoEntregaConfiguracion(string Codigo, string Descripcion, bool Disponible);

public sealed record ReglasOperativasConfiguracion(
    int? MaxIntentosEntrega,
    int? IntervaloEntreIntentosMinutos,
    PoliticaDevolucionAlAgotarIntentos? PoliticaDevolucionAlAgotarIntentos,
    int? PlazoEstandarMinutos,
    int? PlazoUrgenteMinutos,
    IReadOnlyCollection<TipoPruebaEntrega> RequisitosPrueba,
    IReadOnlyCollection<MotivoNoEntregaConfiguracion> Motivos);
