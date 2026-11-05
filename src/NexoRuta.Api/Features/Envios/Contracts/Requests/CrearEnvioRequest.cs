using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;
using NexoRuta.Api.Validation;

namespace NexoRuta.Api.Features.Envios.Contracts.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CrearEnvioRequest
{
    public SolicitudRetiroRequest? Retiro { get; set; }

    [Required(ErrorMessage = "Seleccioná un operador para el envío.")]
    public Guid? OperadorId { get; set; }

    [Required, StringLength(160)]
    public string DestinatarioNombre { get; set; } = "";

    [Required, EmailAddress, StringLength(254)]
    public string DestinatarioEmail { get; set; } = "";

    [Required, StringLength(240)]
    public string Direccion { get; set; } = "";

    [Required]
    public ServicioEnvioRequest Servicio { get; set; } = new();

    [Required, MinLength(1)]
    public List<BultoDeclaradoRequest> Bultos { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class BultoDeclaradoRequest
{
    [Required, StringLength(80)]
    public string Codigo { get; set; } = "";

    [Required, Range(typeof(decimal), "0.01", "999999999", ParseLimitsInInvariantCulture = true)]
    [DosDecimales] public decimal PesoGramos { get; set; }

    [Required, Range(typeof(decimal), "0.01", "999999999", ParseLimitsInInvariantCulture = true)]
    [DosDecimales] public decimal LargoCentimetros { get; set; }

    [Required, Range(typeof(decimal), "0.01", "999999999", ParseLimitsInInvariantCulture = true)]
    [DosDecimales] public decimal AnchoCentimetros { get; set; }

    [Required, Range(typeof(decimal), "0.01", "999999999", ParseLimitsInInvariantCulture = true)]
    [DosDecimales] public decimal AltoCentimetros { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ServicioEnvioRequest
{
    public Guid? VersionTarifarioEsperada { get; set; }
    [Required] public Guid? ZonaId { get; set; }
    [Required, Range(-90d, 90d)] public double? Latitud { get; set; }
    [Required, Range(-180d, 180d)] public double? Longitud { get; set; }
    [Required, RegularExpression("^(Estandar|Urgente|Retiro)$")]
    public string Modalidad { get; set; } = "Estandar";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SolicitudRetiroRequest
{
    [Required] public Guid SolicitudId { get; set; }
    [Required, StringLength(500, MinimumLength = 1)] public string Motivo { get; set; } = "";
    [Required] public DateTimeOffset DesdeUtc { get; set; }
    [Required] public DateTimeOffset HastaUtc { get; set; }
}
