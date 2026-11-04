using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Reparto;
public sealed partial class EfRutasRepository
{
    public async Task<ConsultaRecepcionDatos?> ConsultarRecepcionAsync(Guid op, string codigo, CancellationToken ct)
    {
        codigo = codigo.Trim();
        if (codigo.Length is 0 or > 80) throw new ArgumentException("Escaneá el QR o ingresá el UUID o código del bulto.");
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(op, ct);
        var consulta = db.Bultos.AsNoTracking().Where(b => b.OperadorId == op);
        var encontrados = Guid.TryParseExact(codigo, "D", out var id)
            ? await consulta.Where(b => b.Id == id).Take(2).ToArrayAsync(ct)
            : await consulta.Where(b => b.Codigo == codigo).Take(2).ToArrayAsync(ct);
        if (encontrados.Length > 1) throw new ConflictoRepartoException("Ese código identifica varios bultos. Escaneá el QR o ingresá el UUID de la etiqueta.");
        if (encontrados.Length == 0) return null;
        var resultado = await ConsultaRecepcion(op, encontrados[0].EnvioId, encontrados[0].Id, ct);
        await tx.CommitAsync(ct); return resultado;
    }

    public async Task<ResultadoRecepcionDatos?> CotejarBultoAsync(ContextoUsuario actor, Guid id, CotejoBultoDatos datos, CancellationToken ct)
    {
        var op = actor.OperadorId ?? throw new RepartoNoPermitidoException();
        datos = datos with { Observaciones = datos.Observaciones?.Trim() ?? "" };
        var valores = new[] { datos.PesoObservado, datos.LargoObservado, datos.AnchoObservado, datos.AltoObservado };
        if (datos.ComandoId == Guid.Empty || datos.VersionEnvio < 0 || datos.VersionBulto < 0
            || datos.Observaciones.Length > 500 || !datos.Conforme && datos.Observaciones.Length == 0
            || datos.Conforme && valores.Any(v => v is null)
            || valores.Any(v => v is {} n && (n < .01m || n > 999999999m || decimal.Round(n,2) != n)))
            throw new ArgumentException("Indicá versiones vigentes y medidas positivas con hasta dos decimales; una discrepancia requiere observaciones de hasta 500 caracteres.");
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(op, ct); await Bloquear(op, ct);
        var bulto = await db.Bultos.SingleOrDefaultAsync(b => b.OperadorId == op && b.Id == id, ct);
        if (bulto is null) return null;
        var previa = await db.Database.SqlQuery<RegistroRecepcion>($"SELECT * FROM public.\"RecepcionesBulto\" WHERE \"ComandoId\"={datos.ComandoId} AND \"OperadorId\"={op}").SingleOrDefaultAsync(ct);
        if (previa is not null)
        {
            if (previa.BultoId != id || previa.AccesoId != actor.AccesoId || previa.UsuarioId != actor.UsuarioId
                || JsonSerializer.Deserialize<CotejoBultoDatos>(previa.Cotejo) != datos)
                throw new ConflictoRepartoException("El comando ya fue usado con otro bulto, contenido u operario.");
            var replay = await ConsultaRecepcion(op, bulto.EnvioId, id, ct);
            await tx.CommitAsync(ct); return new(true, replay);
        }
        var envio = await db.Envios.SingleAsync(e => e.Id == bulto.EnvioId && e.OperadorId == op, ct);
        var declaradas = Medidas(bulto);
        var iguales = datos.PesoObservado == declaradas.PesoGramos && datos.LargoObservado == declaradas.LargoCentimetros
            && datos.AnchoObservado == declaradas.AnchoCentimetros && datos.AltoObservado == declaradas.AltoCentimetros;
        if (datos.Conforme && !iguales) throw new ArgumentException("Las medidas observadas difieren de las declaradas. Registrá una discrepancia.");
        var hechos = await RegistrosRecepcion(op, envio.Id, ct);
        var ultimo = hechos.FirstOrDefault(h => h.BultoId == id);
        // A new scan never creates another physical receipt for an already conforming parcel.
        if (envio.Estado is EstadoEnvio.Admitido or EstadoEnvio.EnDeposito && bulto.Custodia == EstadoCustodiaBulto.Deposito
            && ultimo is not null && JsonSerializer.Deserialize<CotejoBultoDatos>(ultimo.Cotejo) is { Conforme: true } cotejo
            && datos.Conforme && iguales && datos.Observaciones == cotejo.Observaciones)
        {
            var repetida = await ConsultaRecepcion(op, envio.Id, id, ct);
            await tx.CommitAsync(ct); return new(true, repetida);
        }
        if (envio.Estado != EstadoEnvio.Admitido || envio.Version != datos.VersionEnvio || bulto.Version != datos.VersionBulto
            || bulto.Custodia is not (EstadoCustodiaBulto.SinRecepcion or EstadoCustodiaBulto.Deposito))
            throw new ConflictoRepartoException("El bulto o envío cambió, o no admite primera recepción. Revisá el estado; los retornos se reciben por su circuito específico.");
        var ahora = reloj.GetUtcNow();
        var declarado = JsonSerializer.Serialize(declaradas); var cotejoJson = JsonSerializer.Serialize(datos);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public."RecepcionesBulto" ("ComandoId","OperadorId","EnvioId","BultoId","UsuarioId","AccesoId","RegistradaEnUtc","Declarado","Cotejo")
            VALUES ({datos.ComandoId},{op},{envio.Id},{id},{actor.UsuarioId},{actor.AccesoId},{ahora},{declarado}::jsonb,{cotejoJson}::jsonb)
            """, ct);
        bulto.RecibirEnDeposito();
        // Rechecks while in deposit are versioned too; receipt commands never modify declared measures.
        if (ultimo is not null) bulto.RegistrarCotejoDeposito();
        var todos = await db.Bultos.Where(b => b.OperadorId == op && b.EnvioId == envio.Id).ToArrayAsync(ct);
        var conformes = hechos.GroupBy(h => h.BultoId).Where(g => JsonSerializer.Deserialize<CotejoBultoDatos>(g.First().Cotejo)!.Conforme)
            .Select(g => g.Key).ToHashSet();
        if (datos.Conforme) conformes.Add(id); else conformes.Remove(id);
        if (todos.All(b => b.Custodia == EstadoCustodiaBulto.Deposito && conformes.Contains(b.Id)))
        {
            await db.SetShipmentHistoryContextAsync("RecepcionDeposito", datos.ComandoId, datos.ComandoId, ahora, cancellationToken: ct);
            envio.CambiarEstado(EstadoEnvio.EnDeposito, envio.Version);
        }
        await db.SaveChangesAsync(ct);
        var salida = await ConsultaRecepcion(op, envio.Id, id, ct);
        await tx.CommitAsync(ct); return new(false, salida);
    }

    private static MedidasRecepcionDatos Medidas(Bulto b) => new(b.PesoGramos,b.LargoCentimetros,b.AnchoCentimetros,b.AltoCentimetros);
    private Task<RegistroRecepcion[]> RegistrosRecepcion(Guid op, Guid envio, CancellationToken ct) =>
        db.Database.SqlQuery<RegistroRecepcion>($"SELECT * FROM public.\"RecepcionesBulto\" WHERE \"OperadorId\"={op} AND \"EnvioId\"={envio} ORDER BY \"Secuencia\" DESC").ToArrayAsync(ct);
    private async Task<ConsultaRecepcionDatos> ConsultaRecepcion(Guid op, Guid envioId, Guid seleccionado, CancellationToken ct)
    {
        var envio = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == envioId && e.OperadorId == op, ct);
        var persona = await db.Destinatarios.AsNoTracking().SingleAsync(d => d.Id == envio.DestinatarioId && d.OperadorId == op, ct);
        var bultos = await db.Bultos.AsNoTracking().Where(b => b.EnvioId == envioId && b.OperadorId == op).OrderBy(b => b.Codigo).ThenBy(b => b.Id).ToArrayAsync(ct);
        var hechos = await RegistrosRecepcion(op,envioId,ct);
        return new(envioId,envio.Estado.ToString(),envio.Version,persona.Nombre,seleccionado,
            bultos.Select(b => new BultoDepositoDatos(b.Id,b.Codigo,b.Version,b.Custodia.ToString(),Medidas(b),
                hechos.FirstOrDefault(h => h.BultoId == b.Id) is {} h ? JsonSerializer.Deserialize<CotejoBultoDatos>(h.Cotejo)!.Conforme ? "Conforme" : "Discrepancia pendiente" : "Sin cotejar")).ToArray(),
            hechos.Where(h => h.BultoId == seleccionado).Select(h => new HechoRecepcionDatos(h.ComandoId,h.BultoId,h.UsuarioId,h.AccesoId,h.RegistradaEnUtc,
                JsonSerializer.Deserialize<MedidasRecepcionDatos>(h.Declarado)!,JsonSerializer.Deserialize<CotejoBultoDatos>(h.Cotejo)!)).ToArray());
    }
    private sealed class RegistroRecepcion
    {
        public long Secuencia { get; set; }
        public Guid ComandoId { get; set; } public Guid OperadorId { get; set; } public Guid EnvioId { get; set; }
        public Guid BultoId { get; set; } public Guid UsuarioId { get; set; } public Guid AccesoId { get; set; }
        public DateTimeOffset RegistradaEnUtc { get; set; } public string Declarado { get; set; } = ""; public string Cotejo { get; set; } = "";
    }
}
