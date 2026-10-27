using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Envios.Commands;
using NexoRuta.Application.Envios.Exceptions;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Envios.UseCases;
using NexoRuta.Application.Reparto.Exceptions;

namespace NexoRuta.Application.Envios.Importacion;

public sealed class ImportarEnviosUseCase(
    IEnviosRepository repository,
    IUsuarioActual usuarioActual,
    CrearEnvioUseCase crearEnvio)
{
    private const string AtomicRejectionCode = "AtomicBatchRejected";

    public async Task<ResultadoImportacionEnvios> EjecutarAsync(
        ReadOnlyMemory<byte> file,
        Guid operadorId,
        CancellationToken cancellationToken = default)
    {
        if (operadorId == Guid.Empty)
            throw new ArgumentException("Seleccioná un operador para el archivo.", nameof(operadorId));

        var records = CsvEnviosParser.Parse(file);
        var context = await usuarioActual.ObtenerAsync(cancellationToken);
        var commerce = context.RequerirComercio();
        var rows = records.Select(record => Validar(record, commerce.Id, operadorId)).ToArray();
        var output = new Dictionary<int, FilaImportacionEnvio>();
        foreach (var row in rows.Where(x => x.Errors.Count != 0))
            output[row.Record] = new(row.Record, row.Reference, EstadoFilaImportacionEnvio.Invalido, null, null, row.Errors);

        var candidateGroups = rows.Where(x => x.Candidate is not null)
            .GroupBy(x => x.Reference!, StringComparer.Ordinal)
            .Select(group => new CandidateGroup(group.Key, group.OrderBy(x => x.Record).ToArray()))
            .ToArray();
        var conflictingGroups = new HashSet<CandidateGroup>();
        foreach (var group in candidateGroups)
        {
            var first = group.Rows[0].Candidate!;
            if (group.Rows.Skip(1).Any(x => !Equivalent(first, x.Candidate!)))
            {
                conflictingGroups.Add(group);
                foreach (var row in group.Rows)
                    output[row.Record] = new(row.Record, group.Reference, EstadoFilaImportacionEnvio.Conflicto,
                        null, null, [new("DuplicateReferenceConflict", "referenciaExterna",
                            "La misma referencia aparece con datos diferentes dentro del archivo.")]);
            }
        }

        var processableGroups = candidateGroups.Where(x => !conflictingGroups.Contains(x)).ToArray();
        var groupResults = new Dictionary<CandidateGroup, GroupResult>();
        if (processableGroups.Length != 0)
        {
            await using var transaction = await repository.IniciarImportacionAsync(cancellationToken);
            var references = processableGroups.Select(x => x.Reference).Distinct(StringComparer.Ordinal).ToArray();
            await repository.BloquearReferenciasImportacionAsync(references, cancellationToken);
            var existing = await repository.BuscarIdentidadesImportacionAsync(references, cancellationToken);
            var existingByReference = existing.ToDictionary(x => x.ReferenciaExterna, StringComparer.Ordinal);
            foreach (var group in processableGroups)
            {
                if (existingByReference.TryGetValue(group.Reference, out var identity))
                {
                    if (identity.OperadorId == operadorId
                        && identity.VersionHuella == HuellaSolicitudEnvio.VersionActual
                        && CryptographicOperations.FixedTimeEquals(identity.Huella, group.Rows[0].Candidate!.Huella))
                    {
                        groupResults[group] = new(EstadoFilaImportacionEnvio.YaExistente, identity.EnvioId);
                    }
                    else
                    {
                        groupResults[group] = new(EstadoFilaImportacionEnvio.Conflicto, null,
                            [new("ExternalReferenceConflict", "referenciaExterna",
                                "La referencia ya existe con datos originales u operador diferentes.")]);
                    }
                }
                else
                {
                    groupResults[group] = new(EstadoFilaImportacionEnvio.NoProcesado, null);
                }
            }

            var hasInvalidRows = rows.Any(x => x.Errors.Count != 0);
            var hasConflict = conflictingGroups.Count != 0
                || groupResults.Values.Any(x => x.State == EstadoFilaImportacionEnvio.Conflicto);
            if (hasInvalidRows || hasConflict)
            {
                FillGroupResults(output, processableGroups, groupResults, AtomicRejectionCode,
                    "No se creó porque otra fila rechazó el archivo completo.");
                return Rejected(hasConflict ? TipoRechazoImportacionEnvios.Conflicto : TipoRechazoImportacionEnvios.Validacion,
                    rows, output);
            }

            var newGroups = processableGroups.Where(x => groupResults[x].State == EstadoFilaImportacionEnvio.NoProcesado).ToArray();
            try
            {
                var created = await crearEnvio.EjecutarImportadosAsync(newGroups
                    .Select(group => group.Rows[0].Candidate!).ToArray(), cancellationToken);
                for (var index = 0; index < newGroups.Length; index++)
                    groupResults[newGroups[index]] = new(EstadoFilaImportacionEnvio.Creado, created[index].Id);
            }
            catch (OperadorNoEncontradoException error)
            {
                return Rejected(TipoRechazoImportacionEnvios.Validacion, rows,
                    AddNewRowsNotProcessed(output, processableGroups, groupResults, "OperatorUnavailable", "operadorId", error.Message));
            }
            catch (OrganizacionSuspendidaException error)
            {
                return Rejected(TipoRechazoImportacionEnvios.Validacion, rows,
                    AddNewRowsNotProcessed(output, processableGroups, groupResults, "OperatorUnavailable", "operadorId", error.Message));
            }
            catch (ReglasEnvioNoDisponiblesException error)
            {
                return Rejected(TipoRechazoImportacionEnvios.Validacion, rows,
                    AddNewRowsNotProcessed(output, processableGroups, groupResults, "OperationalRulesUnavailable", "operadorId", error.Message));
            }
            catch (ConflictoRepartoException error)
            {
                return Rejected(TipoRechazoImportacionEnvios.Conflicto, rows,
                    AddNewRowsNotProcessed(output, processableGroups, groupResults, "OperationalRulesChanged", "operadorId", error.Message));
            }

            await transaction.CommitAsync(cancellationToken);
            FillGroupResults(output, processableGroups, groupResults, null, null);
            return new(true, null, output.Values.OrderBy(x => x.Registro).ToArray());
        }

        var rejected = rows.Any(x => x.Errors.Count != 0) || conflictingGroups.Count != 0;
        foreach (var group in conflictingGroups)
            foreach (var row in group.Rows)
                output[row.Record] = new(row.Record, group.Reference, EstadoFilaImportacionEnvio.Conflicto,
                    null, null, [new("DuplicateReferenceConflict", "referenciaExterna",
                        "La misma referencia aparece con datos diferentes dentro del archivo.")]);
        return rejected
            ? Rejected(conflictingGroups.Count != 0 ? TipoRechazoImportacionEnvios.Conflicto : TipoRechazoImportacionEnvios.Validacion, rows, output)
            : new(true, null, []);
    }

    private static Dictionary<int, FilaImportacionEnvio> AddNewRowsNotProcessed(
        Dictionary<int, FilaImportacionEnvio> output,
        IReadOnlyList<CandidateGroup> groups,
        Dictionary<CandidateGroup, GroupResult> results,
        string code,
        string field,
        string message)
    {
        foreach (var group in groups.Where(x => results[x].State == EstadoFilaImportacionEnvio.NoProcesado))
        {
            results[group] = new(EstadoFilaImportacionEnvio.NoProcesado, null, [new(code, field, message)]);
        }
        FillGroupResults(output, groups, results, null, null);
        return output;
    }

    private static ResultadoImportacionEnvios Rejected(
        TipoRechazoImportacionEnvios reason,
        IReadOnlyList<ValidatedRow> rows,
        Dictionary<int, FilaImportacionEnvio> output)
    {
        var represented = output.Keys.ToHashSet();
        foreach (var row in rows.Where(x => !represented.Contains(x.Record)))
            output[row.Record] = new(row.Record, row.Reference, EstadoFilaImportacionEnvio.NoProcesado,
                null, null, [new(AtomicRejectionCode, null, "No se creó porque el archivo se rechazó de forma atómica.")]);
        return new(false, reason, output.Values.OrderBy(x => x.Registro).ToArray());
    }

    private static void FillGroupResults(
        Dictionary<int, FilaImportacionEnvio> output,
        IReadOnlyList<CandidateGroup> groups,
        Dictionary<CandidateGroup, GroupResult> results,
        string? atomicCode,
        string? atomicMessage)
    {
        foreach (var group in groups)
        {
            var result = results[group];
            var canonical = group.Rows[0];
            var errors = result.Errors ?? (result.State == EstadoFilaImportacionEnvio.NoProcesado && atomicCode is not null
                ? [new ErrorFilaImportacion(atomicCode, null, atomicMessage!)]
                : Array.Empty<ErrorFilaImportacion>());
            output[canonical.Record] = new(canonical.Record, group.Reference, result.State, result.ShipmentId, null, errors);
            foreach (var duplicate in group.Rows.Skip(1))
                output[duplicate.Record] = new(duplicate.Record, group.Reference, EstadoFilaImportacionEnvio.DuplicadoDe,
                    result.ShipmentId, canonical.Record, errors);
        }
    }

    private static ValidatedRow Validar(CsvEnviosRecord record, Guid commerceId, Guid operatorId)
    {
        var errors = new List<ErrorFilaImportacion>();
        if (record.Fields.Count != 9)
        {
            errors.Add(new("ColumnCount", null, $"Se esperaban 9 columnas y se recibieron {record.Fields.Count}."));
            var refOnly = record.Fields.Count == 0 ? null : NormalizarReferencia(record.Fields[0], errors);
            return new(record.Record, refOnly, null, errors);
        }

        var reference = NormalizarReferencia(record.Fields[0], errors);
        var name = NormalizarTexto(record.Fields[1], "nombreDestinatario", 160, errors);
        var email = NormalizarEmail(record.Fields[2], errors);
        var address = NormalizarTexto(record.Fields[3], "direccion", 240, errors);
        var parcelCode = NormalizarTexto(record.Fields[4], "codigoBulto", 80, errors);
        var weight = ParseDimension(record.Fields[5], "pesoGramos", errors);
        var length = ParseDimension(record.Fields[6], "largoCentimetros", errors);
        var width = ParseDimension(record.Fields[7], "anchoCentimetros", errors);
        var height = ParseDimension(record.Fields[8], "altoCentimetros", errors);
        if (reference is null || name is null || email is null || address is null || parcelCode is null
            || weight is null || length is null || width is null || height is null)
            return new(record.Record, reference, null, errors);

        var command = new CrearEnvioCommand(operatorId, name, email, address, parcelCode,
            weight.Value, length.Value, width.Value, height.Value);
        var candidate = new SolicitudEnvioImportado(record.Record, reference, command,
            HuellaSolicitudEnvio.VersionActual, HuellaSolicitudEnvio.Calcular(commerceId, operatorId, command));
        return new(record.Record, reference, candidate, errors);
    }

    private static string? NormalizarReferencia(string value, List<ErrorFilaImportacion> errors)
    {
        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        var characterCount = 0;
        foreach (var _ in normalized.EnumerateRunes())
            characterCount++;
        if (characterCount is < 1 or > 128 || normalized.Any(char.IsControl))
        {
            errors.Add(new("InvalidReference", "referenciaExterna",
                "La referencia externa debe tener entre 1 y 128 caracteres y no contener controles."));
            return null;
        }
        return normalized;
    }

    private static string? NormalizarTexto(string value, string field, int maxLength, List<ErrorFilaImportacion> errors)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            errors.Add(new("Required", field, "El campo es obligatorio."));
            return null;
        }
        if (normalized.Length > maxLength)
        {
            errors.Add(new("MaxLength", field, $"El campo admite hasta {maxLength} caracteres."));
            return null;
        }
        return normalized;
    }

    private static string? NormalizarEmail(string value, List<ErrorFilaImportacion> errors)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            errors.Add(new("Required", "emailDestinatario", "El correo del destinatario es obligatorio."));
            return null;
        }
        if (normalized.Length > 254 || normalized.Any(char.IsControl)
            || !System.Net.Mail.MailAddress.TryCreate(normalized, out var address) || address.Address != normalized)
        {
            errors.Add(new("InvalidEmail", "emailDestinatario", "Ingresá un correo válido de hasta 254 caracteres."));
            return null;
        }
        return normalized;
    }

    private static decimal? ParseDimension(string value, string field, List<ErrorFilaImportacion> errors)
    {
        var validSyntax = value.Length != 0 && value.Count(c => c == '.') <= 1
            && value[0] != '.' && value[^1] != '.'
            && value.All(c => c == '.' || c is >= '0' and <= '9');
        if (!validSyntax || !decimal.TryParse(value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number))
        {
            errors.Add(new("InvalidDecimal", field, "Usá un decimal con punto, sin separador de miles ni notación exponencial."));
            return null;
        }
        if (number is < 0.01m or > 999_999_999m)
        {
            errors.Add(new("OutOfRange", field, "El valor debe estar entre 0.01 y 999999999."));
            return null;
        }
        return number;
    }

    private static bool Equivalent(SolicitudEnvioImportado left, SolicitudEnvioImportado right)
        => left.Datos.OperadorId == right.Datos.OperadorId
            && left.VersionHuella == right.VersionHuella
            && CryptographicOperations.FixedTimeEquals(left.Huella, right.Huella);

    private sealed record ValidatedRow(
        int Record,
        string? Reference,
        SolicitudEnvioImportado? Candidate,
        IReadOnlyList<ErrorFilaImportacion> Errors);

    private sealed record CandidateGroup(string Reference, ValidatedRow[] Rows);

    private sealed record GroupResult(
        EstadoFilaImportacionEnvio State,
        Guid? ShipmentId,
        IReadOnlyList<ErrorFilaImportacion>? Errors = null);
}
