namespace NexoRuta.Domain.Gestion;

public sealed class ConflictoLiquidacionException(string message):Exception(message);

public static class ReglasGestion
{
    public static string Transicion(string estado,bool confirmar)
    {
        var destino=confirmar ? "Confirmada" : "Descartada";
        if(estado!=destino && estado!="Borrador")
            throw new ConflictoLiquidacionException("Una liquidación cerrada se conserva; corregila mediante un movimiento compensatorio.");
        return destino;
    }
    public static void ValidarCorreccion(Guid id,Guid original,decimal importe,string motivo)
    {
        if(id==Guid.Empty || original==Guid.Empty || importe==0 || decimal.Round(importe,2)!=importe
            || importe is < -999999999999.99m or > 999999999999.99m || motivo?.Trim().Length is not (>=10 and <=500))
            throw new ArgumentException("La corrección requiere identidad, movimiento confirmado, importe con hasta dos decimales y motivo de 10 a 500 caracteres.");
    }
}
