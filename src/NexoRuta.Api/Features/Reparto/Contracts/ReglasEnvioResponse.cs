using NexoRuta.Application.Reparto.Dtos;
namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed record ReglasEnvioResponse(Guid VersionId, int NumeroVersion, int MaximoIntentos,
    int IntervaloEntreIntentosMinutos, string PoliticaDevolucionAlAgotarIntentos,
    bool RequiereFirma, bool RequiereFoto, bool RequiereNombre, bool RequiereDocumento,
    IReadOnlyList<MotivoNoEntregaResponse> Motivos)
{
    public static ReglasEnvioResponse Desde(ReglasEnvioDatos x) => new(x.VersionId, x.NumeroVersion, x.MaximoIntentos,
        x.IntervaloEntreIntentosMinutos, x.PoliticaDevolucionAlAgotarIntentos,
        x.RequiereFirma, x.RequiereFoto, x.RequiereNombre, x.RequiereDocumento,
        x.Motivos.Select(m => new MotivoNoEntregaResponse(m.Codigo, m.Descripcion)).ToArray());
}
public sealed record MotivoNoEntregaResponse(string Codigo, string Descripcion);
