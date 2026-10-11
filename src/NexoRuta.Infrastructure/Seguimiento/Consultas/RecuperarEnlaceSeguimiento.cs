using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Seguimiento.Services;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Security;

namespace NexoRuta.Infrastructure.Seguimiento.Consultas;

public sealed record EnlaceSeguimientoRecuperado(string Estado, string? Url = null, DateTimeOffset? ExpiraEnUtc = null);

public sealed class RecuperarEnlaceSeguimiento(
    NexoRutaDbContext db, IUsuarioActual usuarioActual, IConfiguration configuration)
{
    public async Task<EnlaceSeguimientoRecuperado?> EjecutarAsync(Guid envioId, CancellationToken cancellationToken = default)
    {
        var usuario = await usuarioActual.ObtenerAsync(cancellationToken);
        // Same operator/commerce visibility as the existing shipment list, applied in SQL.
        var envio = await db.Envios.AsNoTracking()
            .Where(x => x.Id == envioId &&
                ((usuario.OperadorId != null && x.OperadorId == usuario.OperadorId) ||
                 (usuario.ComercioId != null && x.ComercioId == usuario.ComercioId)))
            .Select(x => new { x.Id, x.OperadorId })
            .SingleOrDefaultAsync(cancellationToken);
        if (envio is null) return null;

        var generado = await (
            from entrega in db.EntregasSeguimiento.AsNoTracking()
            join enlace in db.EnlacesSeguimiento.AsNoTracking()
                on new { entrega.OperadorId, entrega.EnvioId, entrega.TokenHash }
                equals new { enlace.OperadorId, enlace.EnvioId, enlace.TokenHash }
            where entrega.EnvioId == envio.Id && entrega.OperadorId == envio.OperadorId
            select new { Entrega = entrega, Enlace = enlace })
            .SingleOrDefaultAsync(cancellationToken);
        if (generado is null) return new("pendiente");

        var link = generado.Enlace;
        if (link.RevocadoEnUtc is not null) return new("revocado", ExpiraEnUtc: link.ExpiraEnUtc);
        var now = DateTimeOffset.UtcNow;
        if (now >= link.ExpiraEnUtc) return new("expirado", ExpiraEnUtc: link.ExpiraEnUtc);
        if (!link.EstaVigente(now)) return new("pendiente");

        if (!Uri.TryCreate(configuration["Tracking:PublicBaseUrl"], UriKind.Absolute, out var publicBase) ||
            (publicBase.Scheme != Uri.UriSchemeHttp && publicBase.Scheme != Uri.UriSchemeHttps) ||
            publicBase.UserInfo.Length != 0 || publicBase.Query.Length != 0 || publicBase.Fragment.Length != 0)
            throw new InvalidOperationException("Falta una base HTTP(S) válida para seguimiento.");

        // Reuse the original encrypted delivery; no key is required for pending/invalid links.
        var token = new CifradorTokenSeguimiento(configuration).Descifrar(generado.Entrega);
        if (GeneradorTokenSeguimiento.CalcularHash(token) != link.TokenHash)
            throw new InvalidOperationException("La entrega no corresponde al enlace de seguimiento.");
        var url = publicBase.AbsoluteUri.TrimEnd('/') + "/api/public/seguimiento/" + Uri.EscapeDataString(token);
        return new("vigente", url, link.ExpiraEnUtc);
    }
}
