namespace NexoRuta.Api.Core.Security;

public static class AccesoSeleccionado
{
    public const string Esquema = "SeleccionUsuario";
    public const string Cabecera = "X-NexoRuta-Acceso";
    public const string ClaimAccesoId = "nexoruta:acceso_id";
    public const string ClaimTipoAcceso = "nexoruta:tipo_acceso";
}
