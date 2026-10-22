namespace NexoRuta.Application.ZonasCobertura.Exceptions;

public sealed class AccesoCoberturaNoPermitidoException()
    : Exception("Solo un usuario autenticado de Operador puede administrar o consultar zonas de cobertura.");
