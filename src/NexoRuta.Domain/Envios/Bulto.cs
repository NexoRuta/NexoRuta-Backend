namespace NexoRuta.Domain.Envios;

public sealed class Bulto
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid EnvioId { get; private set; }
    public string Codigo { get; private set; }
    public decimal PesoGramos { get; private set; }
    public decimal LargoCentimetros { get; private set; }
    public decimal AnchoCentimetros { get; private set; }
    public decimal AltoCentimetros { get; private set; }
    public EstadoCustodiaBulto Custodia { get; private set; }
    public Guid? AccesoCustodioId { get; private set; }
    public Guid? RutaCustodiaId { get; private set; }
    public long Version { get; private set; }

    public Bulto(
        Guid operadorId,
        Guid envioId,
        string codigo,
        decimal pesoGramos,
        decimal largoCentimetros,
        decimal anchoCentimetros,
        decimal altoCentimetros)
    {
        if (operadorId == Guid.Empty)
            throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (envioId == Guid.Empty)
            throw new ArgumentException("El envío es obligatorio.", nameof(envioId));
        if (string.IsNullOrWhiteSpace(codigo))
            throw new ArgumentException("El código es obligatorio.", nameof(codigo));
        if (pesoGramos <= 0)
            throw new ArgumentOutOfRangeException(nameof(pesoGramos), "El peso debe ser positivo.");
        if (largoCentimetros <= 0)
            throw new ArgumentOutOfRangeException(nameof(largoCentimetros), "El largo debe ser positivo.");
        if (anchoCentimetros <= 0)
            throw new ArgumentOutOfRangeException(nameof(anchoCentimetros), "El ancho debe ser positivo.");
        if (altoCentimetros <= 0)
            throw new ArgumentOutOfRangeException(nameof(altoCentimetros), "El alto debe ser positivo.");

        Id = Guid.CreateVersion7();
        OperadorId = operadorId;
        EnvioId = envioId;
        Codigo = codigo.Trim();
        PesoGramos = pesoGramos;
        LargoCentimetros = largoCentimetros;
        AnchoCentimetros = anchoCentimetros;
        AltoCentimetros = altoCentimetros;
    }

    public void RecibirEnDeposito(bool devolucionPosterior = false)
    {
        if (Custodia == EstadoCustodiaBulto.Deposito) return;
        if (Custodia is not (EstadoCustodiaBulto.SinRecepcion or EstadoCustodiaBulto.PendienteDeposito)
            && !(devolucionPosterior && Custodia == EstadoCustodiaBulto.Destinatario))
            throw new InvalidOperationException("El bulto no está declarado para recepción en depósito.");
        Custodia = EstadoCustodiaBulto.Deposito;
        AccesoCustodioId = null; RutaCustodiaId = null; Version++;
    }

    public void Cargar(Guid accesoId, Guid rutaId)
    {
        if (accesoId == Guid.Empty || rutaId == Guid.Empty) throw new ArgumentException("La carga requiere repartidor y ruta.");
        if (Custodia == EstadoCustodiaBulto.Repartidor && AccesoCustodioId == accesoId && RutaCustodiaId == rutaId) return;
        if (Custodia != EstadoCustodiaBulto.Deposito) throw new InvalidOperationException("El bulto todavía no está disponible en depósito.");
        Custodia = EstadoCustodiaBulto.Repartidor; AccesoCustodioId = accesoId; RutaCustodiaId = rutaId; Version++;
    }

    public void RecibirTrasIncidenciaDeCierre(Guid accesoId, Guid rutaId)
    {
        ExigirCustodia(accesoId, rutaId);
        if (Custodia != EstadoCustodiaBulto.Repartidor)
            throw new InvalidOperationException("La recepción requiere la custodia registrada en la incidencia de cierre.");
        Custodia = EstadoCustodiaBulto.Deposito;
        AccesoCustodioId = null; RutaCustodiaId = null; Version++;
    }

    public void Entregar(Guid accesoId, Guid rutaId)
    {
        ExigirCustodia(accesoId, rutaId);
        if (Custodia != EstadoCustodiaBulto.Repartidor) throw new InvalidOperationException("El bulto no está cargado para entrega.");
        Custodia = EstadoCustodiaBulto.Destinatario; Version++;
    }

    public void DeclararRendicion(Guid accesoId, Guid rutaId)
    {
        ExigirCustodia(accesoId, rutaId);
        if (Custodia == EstadoCustodiaBulto.PendienteDeposito) return;
        if (Custodia != EstadoCustodiaBulto.Repartidor) throw new InvalidOperationException("No se puede rendir un bulto ya entregado.");
        Custodia = EstadoCustodiaBulto.PendienteDeposito; Version++;
    }

    private void ExigirCustodia(Guid accesoId, Guid rutaId)
    {
        if (AccesoCustodioId != accesoId || RutaCustodiaId != rutaId)
            throw new InvalidOperationException("El bulto está bajo otra custodia.");
    }
}
