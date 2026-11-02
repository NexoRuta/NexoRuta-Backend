using System.ComponentModel.DataAnnotations;
using NexoRuta.Api.Validation;

namespace NexoRuta.Api.Features.Envios.Contracts.Requests;

public sealed class CrearEnvioRequest
{
    [Required(ErrorMessage = "Seleccioná un operador para el envío.")]
    public Guid? OperadorId { get; set; }

    [Required, StringLength(160)]
    public string DestinatarioNombre { get; set; } = "";

    [Required, EmailAddress, StringLength(254)]
    public string DestinatarioEmail { get; set; } = "";

    [Required, StringLength(240)]
    public string Direccion { get; set; } = "";

    [Required, StringLength(80)]
    public string CodigoBulto { get; set; } = "";

    [Required, Range(typeof(decimal), "0.01", "999999999", ParseLimitsInInvariantCulture = true)]
    [DosDecimales] public decimal PesoGramos { get; set; }

    [Required, Range(typeof(decimal), "0.01", "999999999", ParseLimitsInInvariantCulture = true)]
    [DosDecimales] public decimal LargoCentimetros { get; set; }

    [Required, Range(typeof(decimal), "0.01", "999999999", ParseLimitsInInvariantCulture = true)]
    [DosDecimales] public decimal AnchoCentimetros { get; set; }

    [Required, Range(typeof(decimal), "0.01", "999999999", ParseLimitsInInvariantCulture = true)]
    [DosDecimales] public decimal AltoCentimetros { get; set; }
}
