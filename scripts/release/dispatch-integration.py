"""Installed in component repositories by their release workflow."""
import json
import os
import urllib.request

# El workflow inyecta el secreto en el entorno; nunca se coloca en argumentos o logs.
token = os.environ.get("NEXORUTA_INTEGRATION_TOKEN")
if not token:
    raise SystemExit("Configure NEXORUTA_INTEGRATION_TOKEN with permission to dispatch in NexoRuta/NexoRuta.")
# repository_dispatch notifica al repositorio madre qué fuente cambió.
# No inicia ni actualiza la VM: esas operaciones siguen siendo manuales.
request = urllib.request.Request(
    "https://api.github.com/repos/NexoRuta/NexoRuta/dispatches",
    # El evento identifica el repositorio y commit que el pipeline central debe procesar.
    data=json.dumps({"event_type": "nexoruta-source-changed", "client_payload": {"repository": os.environ["GITHUB_REPOSITORY"], "sha": os.environ["GITHUB_SHA"]}}).encode(),
    headers={"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json", "Content-Type": "application/json", "X-GitHub-Api-Version": "2022-11-28"},
    method="POST",
)
# GitHub confirma el evento con 204. El timeout evita bloquear indefinidamente el job.
with urllib.request.urlopen(request, timeout=30) as response:
    if response.status != 204:
        raise SystemExit("The integrated pipeline could not be triggered")
print("Triggered main image publication. VM start/update/stop require manual Actions runs.")
