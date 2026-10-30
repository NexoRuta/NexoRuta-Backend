namespace NexoRuta.Infrastructure.Persistence;

public sealed class EjecucionSeedDemo
{
    public const int IdentificadorUnico = 1;

    public int Id { get; private set; }
    public DateTimeOffset SembradoEnUtc { get; private set; }
    public int Operadores { get; private set; }
    public int Comercios { get; private set; }
    public int CuentasOperador { get; private set; }
    public int CuentasComercio { get; private set; }
    public int Envios { get; private set; }
    public int Rutas { get; private set; }
    public int Operaciones { get; private set; }

    private EjecucionSeedDemo() { }

    public EjecucionSeedDemo(DateTimeOffset sembradoEnUtc, int operadores, int comercios,
        int cuentasOperador, int cuentasComercio, int envios, int rutas, int operaciones)
    {
        if (sembradoEnUtc.Offset != TimeSpan.Zero || new[]
            { operadores, comercios, cuentasOperador, cuentasComercio, envios, rutas, operaciones }.Any(x => x < 0))
            throw new ArgumentException("El resultado del seed debe contener una fecha UTC y conteos no negativos.");

        Id = IdentificadorUnico;
        SembradoEnUtc = sembradoEnUtc;
        Operadores = operadores;
        Comercios = comercios;
        CuentasOperador = cuentasOperador;
        CuentasComercio = cuentasComercio;
        Envios = envios;
        Rutas = rutas;
        Operaciones = operaciones;
    }
}
