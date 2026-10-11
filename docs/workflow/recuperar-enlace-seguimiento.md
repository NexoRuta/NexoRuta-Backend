# Recover the existing tracking link

`GET /api/envios/{envioId}/seguimiento` recovers the original automatically generated
token. It does not create links, extend their 24-hour lifetime, revoke them, write
delivery state, or publish broker messages. `EntregasSeguimiento` stores ciphertext,
nonce and tag; its operator/shipment/hash identifies the corresponding link.

## Temporary identity boundary

The route requires the existing `X-NexoRuta-Acceso` selection and runs **only in
Development**. Missing/invalid selection returns 401; selected identity outside
Development returns 403. Shipment visibility matches the existing list: assigned
operator OR assigned commerce, not an arbitrary shipment ID or claimed tenant.
Missing and inaccessible shipments both return 404. Selection IDs are not passwords:
anyone who can select an identity can impersonate it in this demo. Keep the demo API
local/trusted. This is not production authentication or production readiness.

## Response

| HTTP | `estado` | URL |
| --- | --- | --- |
| 200 | `vigente` | Original public URL and `expiraEnUtc` |
| 202 | `pendiente` | `url: null`; `Retry-After: 2` |
| 410 | `expirado` / `revocado` | `url: null`, existing expiration |
| 404 | No response body | Missing/inaccessible shipment |
| 503 | Generic ProblemDetails | Missing/invalid configuration or unusable encrypted delivery |

Recovery responses use `Cache-Control: no-store` and `Referrer-Policy: no-referrer`.
For 200: `{ "estado": "vigente", "url": "<public-url>", "expiraEnUtc": "<UTC>" }`.
The URL is a bearer secret; do not publish it or enable body logging.
`pendiente` means no matching automatic delivery/link is available yet; broker
failure can keep it pending, not just a short processing delay. Legacy manually
created hash-only links have no recoverable original token and are not regenerated.

## Required existing configuration

API and Rabbit Worker must use the **same** stable `Seguimiento:EncryptionKey`
(Base64 encoding of 32 bytes) that encrypted the existing deliveries. Supply it
externally; do not generate a replacement or copy it into versioned configuration.
Recovery needs that key only for a currently valid link. Missing/wrong keys return
503 without changing the existing token.

Set `Tracking:PublicBaseUrl` to the public **API** base while no Tracking frontend
exists. Development default matches the HTTP launch profile: `http://localhost:5041`.
For a different listener, override `Tracking__PublicBaseUrl` in the API environment.
Base paths are preserved; HTTP(S) is required, credentials/query/fragment rejected.
The resulting path is `/api/public/seguimiento/{original-token}`. HTTPS is preferable
outside localhost; never derive the base from an untrusted request Host header.

## PowerShell against an already configured Development API

These are manual HTTP commands, not deployment/startup commands. The existing API
still runs migrations/seeding on startup: do not start it against an unreviewed
shared database just to test recovery. No shared API/Worker was started by this task.
Adjust `$api` to the running updated worktree API; its configured public base must
point to the same reachable API. Select a commerce identity deliberately:

```powershell
$api = 'http://localhost:5041'
$accounts = @(Invoke-RestMethod "$api/api/accesos?tipo=Comercio")
$accounts | Select-Object accesoId, usuarioEmail, comercioNombre
$accessId = [Guid](Read-Host 'Commerce accesoId from the list')
$headers = @{ 'X-NexoRuta-Acceso' = $accessId.ToString() }

# Create a shipment (or use an ID from GET /api/envios instead).
$operators = @(Invoke-RestMethod "$api/api/comercio/operadores" -Headers $headers)
if ($operators.Count -eq 0) { throw 'No operator available' }
$body = @{
    operadorId = $operators[0].operadorId
    destinatarioNombre = 'Console recipient'
    direccion = 'Console address'
    codigoBulto = 'CONSOLE-' + [Guid]::NewGuid().ToString('N')
    pesoGramos = 1250; largoCentimetros = 30
    anchoCentimetros = 20; altoCentimetros = 10
} | ConvertTo-Json
$shipment = Invoke-RestMethod "$api/api/envios" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
$envioId = $shipment.id
# Existing shipment alternative:
# Invoke-RestMethod "$api/api/envios" -Headers $headers | Select-Object id, estado
# $envioId = [Guid](Read-Host 'Existing shipment ID')

# Repeat this request while estado is pendiente; 202 is not an error in PowerShell.
$link = Invoke-RestMethod "$api/api/envios/$envioId/seguimiento" -Headers $headers
if ($link.estado -eq 'pendiente') {
    Write-Host 'Pending RabbitMQ processing; wait and repeat the recovery request.'
} elseif ($link.estado -eq 'vigente') {
    $url = $link.url
    # No selected identity is sent to public lookup.
    Invoke-RestMethod -Uri $url
    Start-Process $url
    $again = Invoke-RestMethod "$api/api/envios/$envioId/seguimiento" -Headers $headers
    $again.url -eq $url  # True: the original token is reused.
}
```

410/404/401/403/503 are HTTP errors in Invoke-RestMethod; inspect their response
status/body. No recovery retry can fix an expired/revoked link or a missing AES key.
Browser lookup currently shows public status JSON, not a frontend screen.

## Verification

The isolated PostgreSQL tests cover recovery twice, original ciphertext decryption,
base-path composition, anonymous public lookup, validity states, operator/commerce
scope, missing identity, non-Development denial, invalid base URLs, and key errors.
Replaying the existing Rabbit processing operation preserves one link/delivery and
the same original token. Existing broker tests run only owned Testcontainers.
No Kafka, Rabbit implementation, public endpoint, migrations, or frontend is changed.
