using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
namespace NexoRuta.Infrastructure.Notificaciones;
public sealed class CifradorRegistroPush(IConfiguration configuracion)
{
    private byte[] Clave()
    {
        byte[] clave;
        try { clave = Convert.FromBase64String(configuracion["Push:RegistrationEncryptionKey"] ?? ""); }
        catch (FormatException) { throw new InvalidOperationException("La protección de registros push no está configurada."); }
        if (clave.Length != 32) { CryptographicOperations.ZeroMemory(clave); throw new InvalidOperationException("La protección de registros push no está configurada."); }
        return clave;
    }
    public void Cifrar(InstalacionMovil instalacion, string fid)
    {
        var clave = Clave(); var plano = Encoding.UTF8.GetBytes(fid);
        try
        {
            instalacion.FidHash = Convert.ToHexString(SHA256.HashData(plano));
            instalacion.Nonce = RandomNumberGenerator.GetBytes(12); instalacion.Tag = new byte[16]; instalacion.FidCifrado = new byte[plano.Length];
            using var aes = new AesGcm(clave, 16);
            aes.Encrypt(instalacion.Nonce, plano, instalacion.FidCifrado, instalacion.Tag, Aad(instalacion));
        }
        finally { CryptographicOperations.ZeroMemory(clave); CryptographicOperations.ZeroMemory(plano); }
    }
    public string Descifrar(InstalacionMovil instalacion)
    {
        var clave = Clave(); var plano = new byte[instalacion.FidCifrado.Length];
        try
        {
            using var aes = new AesGcm(clave, 16); aes.Decrypt(instalacion.Nonce, instalacion.FidCifrado, instalacion.Tag, plano, Aad(instalacion));
            return Encoding.UTF8.GetString(plano);
        }
        finally { CryptographicOperations.ZeroMemory(clave); CryptographicOperations.ZeroMemory(plano); }
    }
    private static byte[] Aad(InstalacionMovil i) => Encoding.UTF8.GetBytes($"{i.OperadorId:N}:{i.AccesoId:N}:{i.Id:N}:{i.FidHash}");
}
