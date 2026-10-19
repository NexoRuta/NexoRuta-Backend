namespace NexoRuta.Infrastructure.ZonasCobertura;

internal static class CatalogoDepartamentosUruguay
{
    public const string ResourceName = "NexoRuta.Infrastructure.ZonasCobertura.Data.departamentos-uruguay.geojson";
    public const string CountryCode = "UY";
    public const string SourceUrl = "https://sit.mvot.gub.uy/arcgis/rest/services/07_EXTERNO/LIMITES_ADMINISTRATIVOS/MapServer/3/query?where=1%3D1&outFields=DEPTO&returnGeometry=true&outSR=4326&f=geojson";
    public const string Attribution = "Instituto Geográfico Militar (IGM)";
    public const string UndeclaredLicense = "";

    // These codes are the Uruguay subdivision identifiers from ISO 3166-2; source DEPTO names are matched explicitly.
    public static readonly IReadOnlyDictionary<string, string> DepartmentCodes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ARTIGAS"] = "UY-AR",
        ["CANELONES"] = "UY-CA",
        ["CERRO LARGO"] = "UY-CL",
        ["COLONIA"] = "UY-CO",
        ["DURAZNO"] = "UY-DU",
        ["FLORES"] = "UY-FS",
        ["FLORIDA"] = "UY-FD",
        ["LAVALLEJA"] = "UY-LA",
        ["MALDONADO"] = "UY-MA",
        ["MONTEVIDEO"] = "UY-MO",
        ["PAYSANDÚ"] = "UY-PA",
        ["RÍO NEGRO"] = "UY-RN",
        ["RIVERA"] = "UY-RV",
        ["ROCHA"] = "UY-RO",
        ["SALTO"] = "UY-SA",
        ["SAN JOSÉ"] = "UY-SJ",
        ["SORIANO"] = "UY-SO",
        ["TACUAREMBÓ"] = "UY-TA",
        ["TREINTA Y TRES"] = "UY-TT"
    };
}
