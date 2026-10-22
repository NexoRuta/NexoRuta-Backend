namespace NexoRuta.Application.Reparto.Dtos;

public sealed record ReglasEnvioDatos(Guid VersionId, int NumeroVersion, int MaximoIntentos,
    int IntervaloEntreIntentosMinutos, string PoliticaDevolucionAlAgotarIntentos,
    bool RequiereFirma, bool RequiereFoto, bool RequiereNombre, bool RequiereDocumento,
    IReadOnlyList<MotivoNoEntregaDatos> Motivos);
public sealed record MotivoNoEntregaDatos(string Codigo, string Descripcion);
