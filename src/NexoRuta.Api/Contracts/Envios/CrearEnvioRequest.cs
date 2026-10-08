using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Contracts.Envios;

public sealed class CrearEnvioRequest
{
    [Required, StringLength(160)]
    public string DestinatarioNombre { get; set; } = "";

    [Required, StringLength(240)]
    public string Direccion { get; set; } = "";

    [Required, StringLength(80)]
    public string CodigoBulto { get; set; } = "";

    [Required, Range(typeof(decimal), "0.01", "999999999")]
    public decimal PesoGramos { get; set; }

    [Required, Range(typeof(decimal), "0.01", "999999999")]
    public decimal LargoCentimetros { get; set; }

    [Required, Range(typeof(decimal), "0.01", "999999999")]
    public decimal AnchoCentimetros { get; set; }

    [Required, Range(typeof(decimal), "0.01", "999999999")]
    public decimal AltoCentimetros { get; set; }
}
