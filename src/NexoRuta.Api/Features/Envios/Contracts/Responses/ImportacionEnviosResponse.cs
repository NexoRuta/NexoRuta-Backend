using NexoRuta.Application.Envios.Importacion;

namespace NexoRuta.Api.Features.Envios.Contracts.Responses;

public sealed record ImportacionEnviosResponse(
    string Estado,
    ResumenImportacionEnvios Resumen,
    IReadOnlyList<FilaImportacionEnviosResponse> Filas)
{
    public static ImportacionEnviosResponse Desde(ResultadoImportacionEnvios result)
    {
        var rows = result.Filas.Select(row => new FilaImportacionEnviosResponse(
            row.Registro,
            row.ReferenciaExterna,
            row.Estado.ToString(),
            row.Estado is EstadoFilaImportacionEnvio.Creado or EstadoFilaImportacionEnvio.YaExistente ? row.EnvioId : null,
            row.MismoQueRegistro,
            row.Errores.Select(error => new ErrorFilaImportacionResponse(error.Codigo, error.Campo, error.Mensaje)).ToArray())).ToArray();
        return new ImportacionEnviosResponse(result.Completada ? "Completada" : "RechazadaAtomica",
            new(rows.Count(x => x.Estado == nameof(EstadoFilaImportacionEnvio.Creado)),
                rows.Count(x => x.Estado == nameof(EstadoFilaImportacionEnvio.YaExistente)),
                rows.Count(x => x.Estado == nameof(EstadoFilaImportacionEnvio.DuplicadoDe)),
                rows.Count(x => x.Estado == nameof(EstadoFilaImportacionEnvio.Conflicto)),
                rows.Count(x => x.Estado == nameof(EstadoFilaImportacionEnvio.Invalido)),
                rows.Count(x => x.Estado == nameof(EstadoFilaImportacionEnvio.NoProcesado))), rows);
    }
}

public sealed record ResumenImportacionEnvios(
    int Creados,
    int Existentes,
    int DuplicadosEnArchivo,
    int Conflictos,
    int Invalidos,
    int NoProcesados);

public sealed record FilaImportacionEnviosResponse(
    int Registro,
    string? ReferenciaExterna,
    string Estado,
    Guid? EnvioId,
    int? MismoQueRegistro,
    IReadOnlyList<ErrorFilaImportacionResponse> Errores);

public sealed record ErrorFilaImportacionResponse(string Codigo, string? Campo, string Mensaje);

public sealed record ErrorImportacionEnviosResponse(string Tipo, string Codigo, string Mensaje);
