using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NexoRuta.Application.Envios.Commands;

namespace NexoRuta.Application.Envios.Importacion;

public static class HuellaSolicitudEnvio
{
    public const short VersionActual = 2;
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("NexoRuta/EnvioImport/V2\0");

    public static byte[] Calcular(Guid comercioId, Guid operadorId, CrearEnvioCommand solicitud)
    {
        using var canonical = new MemoryStream();
        canonical.Write(Domain);
        Escribir(comercioId.ToString("D"));
        Escribir(operadorId.ToString("D"));
        Escribir(solicitud.DestinatarioNombre);
        Escribir(solicitud.DestinatarioEmail);
        Escribir(solicitud.Direccion);
        foreach (var bulto in solicitud.Bultos.OrderBy(b => b.Codigo, StringComparer.Ordinal))
        {
            Escribir(bulto.Codigo);
            Escribir(bulto.PesoGramos.ToString("G29", CultureInfo.InvariantCulture));
            Escribir(bulto.LargoCentimetros.ToString("G29", CultureInfo.InvariantCulture));
            Escribir(bulto.AnchoCentimetros.ToString("G29", CultureInfo.InvariantCulture));
            Escribir(bulto.AltoCentimetros.ToString("G29", CultureInfo.InvariantCulture));
        }
        Escribir(solicitud.Servicio.ZonaId.ToString("D"));
        Escribir(solicitud.Servicio.Latitud.ToString("R", CultureInfo.InvariantCulture));
        Escribir(solicitud.Servicio.Longitud.ToString("R", CultureInfo.InvariantCulture));
        Escribir(solicitud.Servicio.Modalidad);
        return SHA256.HashData(canonical.GetBuffer().AsSpan(0, checked((int)canonical.Length)));

        void Escribir(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            canonical.Write(length);
            canonical.Write(bytes);
        }
    }
}
