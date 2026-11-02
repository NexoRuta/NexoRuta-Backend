using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Validation;

[AttributeUsage(AttributeTargets.Property)]
public sealed class DosDecimalesAttribute : ValidationAttribute
{
    public DosDecimalesAttribute() : base("Ingresá como máximo dos decimales efectivos.") { }
    public override bool IsValid(object? value) => value is null || value is decimal number && decimal.Round(number, 2) == number;
}
