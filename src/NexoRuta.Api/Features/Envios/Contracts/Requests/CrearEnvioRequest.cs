using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Features.Envios.Contracts.Requests;

public sealed class CrearEnvioRequest
{
    [Required(ErrorMessage = "Seleccioná un operador para el envío.")]
    public Guid? OperadorId { get; set; }

    [Required, StringLength(160)]
    public string DestinatarioNombre { get; set; } = "";

    [Required, StringLength(240)]
    public string Direccion { get; set; } = "";

    [Required, StringLength(80)]
    public string CodigoBulto { get; set; } = "";

    [Required, Range(typeof(decimal), "0.01", "999999999", ParseLimitsInInvariantCulture = true)]
    public decimal PesoGramos { get; set; }

    [Required, Range(typeof(decimal), "0.01", "999999999", ParseLimitsInInvariantCulture = true)]
    public decimal LargoCentimetros { get; set; }

    [Required, Range(typeof(decimal), "0.01", "999999999", ParseLimitsInInvariantCulture = true)]
    public decimal AnchoCentimetros { get; set; }

    [Required, Range(typeof(decimal), "0.01", "999999999", ParseLimitsInInvariantCulture = true)]
    public decimal AltoCentimetros { get; set; }
}
