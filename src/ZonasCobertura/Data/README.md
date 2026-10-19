# Uruguay department reference dataset

`departamentos-uruguay.geojson` is the checked-in source used to populate the
shared department reference catalog for CU-02. It is embedded in the
Infrastructure assembly at build time; runtime imports do not depend on the
working directory or an absolute path.

## Provenance and validation

| Field | Observed value |
| --- | --- |
| Source organization | Ministerio de Vivienda y Ordenamiento Territorial (MVOT), Sistema de Información Territorial (SIT) |
| Layer | “Límites departamentales (IGM)”, MapServer layer 3 |
| Attribution exposed by the layer | Instituto Geográfico Militar (IGM) |
| Download date recorded with the source file | 2026-10-19 |
| Request format | GeoJSON FeatureCollection |
| Requested output CRS | EPSG:4326 (`outSR=4326`), coordinates in longitude/latitude order |
| Source field used for names | `DEPTO` |
| Observed feature/name counts | 21 features grouped into 19 distinct departments; ARTIGAS has three source features |
| Coordinate positions checked | 405,249 finite, two-dimensional positions; all fall in WGS 84 longitude/latitude ranges |
| Observed output extent | longitude [-58.43909711699995, -53.18302005199996], latitude [-34.97384064599993, -30.085543431999966] |
| SHA-256 of the checked-in file | `6853cf948843aeaaefe277c09ecbfc01fe824231ede4e824aa03a045c6864219` |
| Edition | Not identified by the queried service metadata |
| License | Not declared in the queried service metadata; no license is inferred here |

Source and request:

- [MVOT/SIT layer metadata](https://sit.mvot.gub.uy/arcgis/rest/services/07_EXTERNO/LIMITES_ADMINISTRATIVOS/MapServer/3?f=pjson)
- [Exact query used for the local download](https://sit.mvot.gub.uy/arcgis/rest/services/07_EXTERNO/LIMITES_ADMINISTRATIVOS/MapServer/3/query?where=1%3D1&outFields=DEPTO&returnGeometry=true&outSR=4326&f=geojson)

The source layer metadata reports inconsistent CRS identifiers in different
fields (the source spatial-reference field says EPSG:5381 while its extent
field says EPSG:5382; the source context supplied for the download identifies
5382). The query explicitly requested `outSR=4326`, and the returned
coordinates were checked as plausible Uruguay longitude/latitude. The importer
does not reproject the file or merely relabel projected coordinates: it reads
the queried output as longitude/latitude and assigns SRID 4326 to those
coordinates. A future replacement file must repeat this CRS/source check.

## Stable department codes

`DEPTO` values are mapped explicitly to ISO 3166-2 Uruguay subdivision codes;
ArcGIS `OBJECTID` values and feature order are not used as identifiers.

| Code | `DEPTO` |
| --- | --- |
| UY-AR | ARTIGAS |
| UY-CA | CANELONES |
| UY-CL | CERRO LARGO |
| UY-CO | COLONIA |
| UY-DU | DURAZNO |
| UY-FS | FLORES |
| UY-FD | FLORIDA |
| UY-LA | LAVALLEJA |
| UY-MA | MALDONADO |
| UY-MO | MONTEVIDEO |
| UY-PA | PAYSANDÚ |
| UY-RN | RÍO NEGRO |
| UY-RV | RIVERA |
| UY-RO | ROCHA |
| UY-SA | SALTO |
| UY-SJ | SAN JOSÉ |
| UY-SO | SORIANO |
| UY-TA | TACUAREMBÓ |
| UY-TT | TREINTA Y TRES |

The code format follows [ISO 3166](https://www.iso.org/iso-3166-country-codes.html);
the mapping from the exact source `DEPTO` labels is maintained in
`CatalogoDepartamentosUruguay` and tested against all 19 codes.

## Geometry handling

The importer validates the complete file before opening a write transaction:
FeatureCollection structure, all 19 names, geometry types, valid/non-empty
polygons, finite 2D coordinates, and WGS 84 coordinate ranges. Polygon rings,
holes, islands, and disconnected pieces are retained.

The 21 source features are grouped by `DEPTO`. A direct MultiPolygon assembled
from overlapping parts can be topologically invalid (observed for ARTIGAS as
a self-intersection). Only when that grouped geometry is invalid, the importer
uses a topological unary union of that department's source polygons. This
dissolves overlapping internal edges while preserving their exact geometric
set; it does not clip, simplify, shift, or apply `MakeValid`. The resulting
geometry must still be valid and non-empty or the whole import fails.

## Import operation

The catalog reuses `DepartamentosReferencia`; it is shared data, not an
operator-owned copy. The one-off API process command is:

```text
department-catalog-import
```

It reads the embedded resource, computes `sha256:<checksum>` as
`VersionFuente`, validates all departments before writing, and inserts the 19
rows in one transaction through the migration/init connection. It exits without
starting Kestrel. API and worker roles remain read-only for this catalog.

Repeating the same checksum is a no-op. A different checksum is rejected unless
the explicit `--allow-source-update` option is supplied; an explicit update
adds a new version and retains prior reference rows. The current local
development startup script invokes the no-op-safe import after migrations and
before launching services. No external source is contacted at request time.

The current national extent and map geometry are derived from the latest
catalog version. Coverage checks use PostGIS `ST_Covers`, including borders,
over the original department component collection; the dissolved national
outline is used for map display. CU-02 does not assert cadastral precision.

## Recheck checksum

From the repository root in PowerShell:

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath "backend/src/ZonasCobertura/Data/departamentos-uruguay.geojson"
```

Never store Mapbox tokens, database credentials, user information, or operator
configuration in this folder.
