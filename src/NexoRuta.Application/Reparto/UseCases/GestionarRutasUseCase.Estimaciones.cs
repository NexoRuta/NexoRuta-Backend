using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
namespace NexoRuta.Application.Reparto.UseCases;
public sealed partial class GestionarRutasUseCase
{
    private async Task<ContextoUsuario> ActorOperador(CancellationToken ct)
    {var a=await usuario.ObtenerAsync(ct);if(a.OperadorId is null || a.ComercioId is not null)throw new RepartoNoPermitidoException();return a;}
    public async Task<IReadOnlyList<PerfilVialDatos>> PerfilesVialesAsync(CancellationToken ct)=>await rutas.PerfilesVialesAsync((await ActorOperador(ct)).OperadorId!.Value,ct);
    public async Task<PerfilVialDatos?> ConfigurarPerfilAsync(Guid vehiculo,ConfigurarPerfilVialDatos d,CancellationToken ct)=>await rutas.ConfigurarPerfilAsync(await ActorOperador(ct),vehiculo,d,ct);
    public async Task<EstimacionRutaDatos?> EstimarAsync(Guid ruta,CalcularEstimacionDatos d,CancellationToken ct)=>await rutas.EstimarAsync(await ActorOperador(ct),ruta,d,ct);
    public async Task<IReadOnlyList<EstimacionRutaDatos>?> EstimacionesAsync(Guid ruta,long? antes,CancellationToken ct)=>await rutas.EstimacionesAsync((await ActorOperador(ct)).OperadorId!.Value,ruta,antes,ct);
}
