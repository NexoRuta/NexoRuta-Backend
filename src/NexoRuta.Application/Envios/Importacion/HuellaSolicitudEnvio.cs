using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NexoRuta.Application.Envios.Commands;

namespace NexoRuta.Application.Envios.Importacion;

public static class HuellaSolicitudEnvio
{
    public const short VersionActual = 1;
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("NexoRuta/EnvioImport/V1\0");

    public static byte[] Calcular(Guid comercioId, Guid operadorId, CrearEnvioCommand solicitud)
    {
        using var canonical = new MemoryStream();
        canonical.Write(Domain);
        Escribir(comercioId.ToString("D"));
        Escribir(operadorId.ToString("D"));
        Escribir(solicitud.DestinatarioNombre);
        Escribir(solicitud.DestinatarioEmail);
        Escribir(solicitud.Direccion);
        Escribir(solicitud.CodigoBulto);
        Escribir(solicitud.PesoGramos.ToString("G29", CultureInfo.InvariantCulture));
        Escribir(solicitud.LargoCentimetros.ToString("G29", CultureInfo.InvariantCulture));
        Escribir(solicitud.AnchoCentimetros.ToString("G29", CultureInfo.InvariantCulture));
        Escribir(solicitud.AltoCentimetros.ToString("G29", CultureInfo.InvariantCulture));
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
