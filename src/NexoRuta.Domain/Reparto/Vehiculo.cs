namespace NexoRuta.Domain.Reparto;

public sealed class Vehiculo
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public string Matricula { get; private set; } = "";
    public decimal PesoMaximoGramos { get; private set; }
    public decimal VolumenMaximoCm3 { get; private set; }
    public bool Activo { get; private set; } = true;
    public long Version { get; private set; }
    public DateTimeOffset? VerificacionInicialEnUtc { get; private set; }
    public Guid? VerificacionInicialPorAccesoId { get; private set; }
    private Vehiculo() { }

    public Vehiculo(Guid operadorId, string matricula, decimal pesoMaximoGramos, decimal volumenMaximoCm3)
    {
        if (operadorId == Guid.Empty) throw new ArgumentException("El operador es obligatorio.");
        Id = Guid.CreateVersion7(); OperadorId = operadorId;
        Definir(matricula, pesoMaximoGramos, volumenMaximoCm3);
    }

    public void Actualizar(string matricula, decimal pesoMaximoGramos, decimal volumenMaximoCm3, bool activo)
    {
        Definir(matricula, pesoMaximoGramos, volumenMaximoCm3);
        Activo = activo; Version++;
    }

    private void Definir(string matricula, decimal peso, decimal volumen)
    {
        if (string.IsNullOrWhiteSpace(matricula) || matricula.Trim().Length > 20)
            throw new ArgumentException("Indicá una matrícula de hasta 20 caracteres.");
        if (peso is < 0.01m or > 9999999999999999m || volumen is < 0.01m or > 9999999999999999999999m)
            throw new ArgumentException("Las capacidades de peso y volumen deben ser positivas y estar dentro de los límites admitidos.");
        if (decimal.Round(peso, 2) != peso || decimal.Round(volumen, 2) != volumen)
            throw new ArgumentException("Las capacidades admiten como máximo dos decimales efectivos.");
        Matricula = matricula.Trim().ToUpperInvariant(); PesoMaximoGramos = peso; VolumenMaximoCm3 = volumen;
    }

    public void ConfirmarVerificacionInicial(Guid accesoId, DateTimeOffset ahora)
    {
        if (accesoId == Guid.Empty || ahora == default || VerificacionInicialEnUtc is not null)
            throw new InvalidOperationException("La verificación inicial requiere un actor y aún debe estar pendiente.");
        VerificacionInicialEnUtc = ahora.ToUniversalTime(); VerificacionInicialPorAccesoId = accesoId; Version++;
    }
}
