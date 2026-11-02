using NexoRuta.Application.Envios.Results;
using NexoRuta.Application.Envios.Importacion;
using NexoRuta.Domain.Envios;

namespace NexoRuta.Application.Envios.Repositories;

public interface IEnviosRepository
{
    Task<IEnviosImportTransaction> IniciarImportacionAsync(CancellationToken cancellationToken = default);
    Task BloquearReferenciasImportacionAsync(IReadOnlyCollection<string> referencias, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IdentidadEnvioImportadoEncontrada>> BuscarIdentidadesImportacionAsync(
        IReadOnlyCollection<string> referencias, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexoRuta.Domain.Tarifas.ResultadoCalculoTarifa>> GuardarImportadosAsync(
        IReadOnlyList<EnvioImportadoParaPersistir> envios, CancellationToken cancellationToken = default);
    Task<Guid?> ObtenerVersionReglasParaAdmisionAsync(Guid operadorId, CancellationToken cancellationToken = default);
    Task<NexoRuta.Domain.Tarifas.ResultadoCalculoTarifa> GuardarAsync(
        Envio envio,
        Destinatario destinatario,
        Direccion direccion,
        IReadOnlyList<Bulto> bultos,
        NexoRuta.Application.Envios.Commands.ServicioEnvioDeclarado servicio,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnvioDetalle>> ListarAsync(
        Guid? operadorId,
        Guid? comercioId,
        CancellationToken cancellationToken = default);
}
