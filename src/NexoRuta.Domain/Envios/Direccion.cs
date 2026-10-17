namespace NexoRuta.Domain.Envios;

public sealed class Direccion
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public string Descripcion { get; private set; }
    public double? Latitud { get; private set; }
    public double? Longitud { get; private set; }
    public Guid? ConfirmadaPorUsuarioId { get; private set; }
    public DateTimeOffset? ConfirmadaEnUtc { get; private set; }
    public long Version { get; private set; }

    public Direccion(Guid operadorId, string descripcion)
    {
        if (operadorId == Guid.Empty)
            throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (string.IsNullOrWhiteSpace(descripcion))
            throw new ArgumentException("La dirección es obligatoria.", nameof(descripcion));

        Id = Guid.CreateVersion7();
        OperadorId = operadorId;
        Descripcion = descripcion.Trim();
    }

    public void ConfirmarUbicacion(double latitud, double longitud, Guid usuarioId, DateTimeOffset ahora, long version)
    {
        if (version != Version) throw new InvalidOperationException("La dirección cambió. Consultá su versión actual.");
        if (!double.IsFinite(latitud) || !double.IsFinite(longitud) || latitud is < -90 or > 90
            || longitud is < -180 or > 180 || usuarioId == Guid.Empty || ahora == default)
            throw new ArgumentException("Las coordenadas y el usuario que confirma no son válidos.");
        Latitud = latitud; Longitud = longitud; ConfirmadaPorUsuarioId = usuarioId;
        ConfirmadaEnUtc = ahora.ToUniversalTime(); Version++;
    }

    public void CambiarDescripcion(string descripcion, long version)
    {
        if (version != Version) throw new InvalidOperationException("La dirección cambió. Consultá su versión actual.");
        if (string.IsNullOrWhiteSpace(descripcion) || descripcion.Trim().Length > 240) throw new ArgumentException("La dirección es obligatoria y admite hasta 240 caracteres.");
        Descripcion = descripcion.Trim(); Latitud = null; Longitud = null;
        ConfirmadaPorUsuarioId = null; ConfirmadaEnUtc = null; Version++;
    }
}
