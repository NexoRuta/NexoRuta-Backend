using System.ComponentModel.DataAnnotations;
using NexoRuta.Domain.Envios;

namespace NexoRuta.Api.Validation;

[AttributeUsage(AttributeTargets.Property)]
public sealed class DosDecimalesAttribute : ValidationAttribute
{
    public DosDecimalesAttribute() : base("Ingresá como máximo dos decimales efectivos.") { }
    public override bool IsValid(object? value) => value is null || value is decimal number && MedidasBulto.TieneHastaDosDecimalesEfectivos(number);
}
