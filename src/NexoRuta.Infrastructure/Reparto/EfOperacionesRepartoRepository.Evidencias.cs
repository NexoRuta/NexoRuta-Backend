using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Application.Reparto.Exceptions;
namespace NexoRuta.Infrastructure.Reparto;

public sealed partial class EfOperacionesRepartoRepository
{
    public async Task<bool> GuardarEvidenciaAsync(Guid operadorId, Guid accesoId, Guid jornadaId, Guid envioId,
        Guid archivoId, string tipo, byte[] contenido, CancellationToken ct)
    {
        if (archivoId == Guid.Empty || envioId == Guid.Empty || tipo is not ("Foto" or "Firma")
            || contenido is null || contenido.Length is < 16 or > 2097152
            || !(contenido.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || contenido.AsSpan().StartsWith(new byte[] { 255, 216, 255 })))
            throw new ArgumentException("La evidencia debe ser una foto o firma PNG/JPEG de hasta 2 MiB.");
        var hash = Convert.ToHexString(SHA256.HashData(contenido));
        await using var tx = await db.BeginOrJoinAsync(ct);
        await ContextoYBloqueo(operadorId, ct);
        var hoja = await Hoja(operadorId, accesoId, jornadaId, ct);
        if (hoja is null || !hoja.Paradas.Any(x => x.EnvioId == envioId)) return false;
        var anterior = await db.EvidenciasReparto.AsNoTracking().SingleOrDefaultAsync(x => x.Id == archivoId, ct);
        if (anterior is not null)
        {
            if (anterior.AccesoId != accesoId || anterior.RutaId != hoja.Id || anterior.EnvioId != envioId || anterior.Tipo != tipo || anterior.Hash != hash)
                throw new ConflictoRepartoException("La evidencia ya existe con otro contenido o contexto.");
            await tx.CommitAsync(ct); return true;
        }
        // Una clave de despliegue obligatoria: nunca generar otra al reiniciar ni guardar imágenes planas.
        byte[] clave;
        try { clave = Convert.FromBase64String(configuracion["Reparto:EvidenciasEncryptionKey"] ?? ""); }
        catch (FormatException) { throw new InvalidOperationException("La protección de evidencias no está configurada."); }
        if (clave.Length != 32) { CryptographicOperations.ZeroMemory(clave); throw new InvalidOperationException("La protección de evidencias no está configurada."); }
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cifrado = new byte[contenido.Length];
        try
        {
            using var aes = new AesGcm(clave, 16);
            aes.Encrypt(nonce, contenido, cifrado, tag, Encoding.UTF8.GetBytes($"{operadorId:N}:{hoja.Id:N}:{envioId:N}:{archivoId:N}:{tipo}:{hash}"));
        }
        finally { CryptographicOperations.ZeroMemory(clave); }
        db.EvidenciasReparto.Add(new()
        {
            Id = archivoId, OperadorId = operadorId, AccesoId = accesoId, RutaId = hoja.Id, EnvioId = envioId,
            Tipo = tipo, Hash = hash, Nonce = nonce, Tag = tag, Cifrado = cifrado, RecibidaEnUtc = reloj.GetUtcNow()
        });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }
}
