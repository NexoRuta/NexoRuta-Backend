namespace NexoRuta.Domain.Reparto;

public sealed class OcupacionRuta
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid RutaId { get; private set; }
    public Guid VehiculoId { get; private set; }
    public Guid RepartidorAccesoId { get; private set; }
    public bool Legada { get; private set; }
    public DateTimeOffset? AdquiridaEnUtc { get; private set; }
    public Guid? AdquiridaPorAccesoId { get; private set; }
    public DateTimeOffset? RetornoEnUtc { get; private set; }
    public Guid? RetornoPorAccesoId { get; private set; }
    public DateTimeOffset? LiberadaEnUtc { get; private set; }
    public Guid? LiberadaPorAccesoId { get; private set; }
    public DateTimeOffset? AnuladaEnUtc { get; private set; }
    public Guid? AnuladaPorAccesoId { get; private set; }
    public long Version { get; private set; }
    public bool Activa => LiberadaEnUtc is null && AnuladaEnUtc is null;
    private OcupacionRuta() { }

    public OcupacionRuta(HojaRuta ruta, Guid actor, DateTimeOffset ahora)
    {
        ValidarActor(actor, ahora);
        Id = Guid.CreateVersion7(); OperadorId = ruta.OperadorId; RutaId = ruta.Id;
        VehiculoId = ruta.VehiculoId; RepartidorAccesoId = ruta.RepartidorAccesoId;
        AdquiridaEnUtc = ahora.ToUniversalTime(); AdquiridaPorAccesoId = actor;
    }

    public void Conciliar(Guid actor, DateTimeOffset ahora)
    {
        ExigirActiva(actor, ahora);
        if (!Legada || AdquiridaEnUtc is not null || RetornoEnUtc is not null)
            throw new InvalidOperationException("La ocupación no requiere confirmar su vigencia inicial.");
        AdquiridaEnUtc = ahora.ToUniversalTime(); AdquiridaPorAccesoId = actor; Version++;
    }

    public void ConfirmarRetorno(Guid actor, DateTimeOffset ahora)
    {
        ExigirActiva(actor, ahora);
        if (RetornoEnUtc is not null) throw new InvalidOperationException("El retorno ya está confirmado.");
        RetornoEnUtc = ahora.ToUniversalTime(); RetornoPorAccesoId = actor; Version++;
    }

    public void Liberar(Guid actor, DateTimeOffset ahora, bool jornadaCerrada, bool sinCargaPendiente)
    {
        ExigirActiva(actor, ahora);
        if (!jornadaCerrada || RetornoEnUtc is null || !sinCargaPendiente)
            throw new InvalidOperationException("Para liberar se requiere jornada cerrada, retorno confirmado y carga pendiente resuelta en depósito o por las conciliaciones existentes.");
        LiberadaEnUtc = ahora.ToUniversalTime(); LiberadaPorAccesoId = actor; Version++;
    }

    public void Anular(Guid actor, DateTimeOffset ahora)
    {
        ExigirActiva(actor, ahora);
        if (RetornoEnUtc is not null) throw new InvalidOperationException("Una ocupación con retorno no admite anulación de despacho.");
        AnuladaEnUtc = ahora.ToUniversalTime(); AnuladaPorAccesoId = actor; Version++;
    }

    private void ExigirActiva(Guid actor, DateTimeOffset ahora)
    {
        ValidarActor(actor, ahora);
        if (!Activa) throw new InvalidOperationException("La ocupación ya fue resuelta.");
    }
    private static void ValidarActor(Guid actor, DateTimeOffset ahora)
    {
        if (actor == Guid.Empty || ahora == default) throw new ArgumentException("La confirmación requiere actor e instante del servidor.");
    }
}
