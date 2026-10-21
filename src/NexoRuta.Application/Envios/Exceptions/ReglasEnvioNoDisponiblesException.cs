namespace NexoRuta.Application.Envios.Exceptions;

public sealed class ReglasEnvioNoDisponiblesException()
    : InvalidOperationException("El operador debe publicar sus reglas operativas antes de admitir envíos.");
