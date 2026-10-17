using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Api.Features.Organizaciones.Contracts;

public sealed record PaginaSolicitudesBajaResponse(
    IReadOnlyList<SolicitudBajaResponse> Items,
    int Pagina,
    int TamanoPagina,
    int Total)
{
    public static PaginaSolicitudesBajaResponse Desde(PaginaSolicitudesBaja page)
        => new(page.Items.Select(SolicitudBajaResponse.Desde).ToArray(),
            page.Pagina, page.TamanoPagina, page.Total);
}
