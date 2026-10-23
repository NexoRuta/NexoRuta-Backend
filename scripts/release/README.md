# Scripts de release

Los scripts usan Python 3.10 o posterior y solamente la biblioteca estándar.
Los workflows `ci.yml` y `release.yml` ejecutan las pruebas con `python3` en
`ubuntu-latest`, que incluye ese intérprete. Los comentarios del código explican
las decisiones de seguridad y recuperación.

- `dispatch-integration.py`: el workflow de release lo ejecuta para notificar al
  pipeline central de NexoRuta. Requiere `NEXORUTA_INTEGRATION_TOKEN`,
  `GITHUB_REPOSITORY` y `GITHUB_SHA` en el entorno. No opera la VM.
- `deploy.py`: herramienta opcional para una VPS **Linux** con Docker Compose.
  Reemplaza a `deploy.sh`; el workflow actual sigue usando el pipeline central,
  por lo que no invoca este despliegue directo.
- `test-deploy.py`: reemplaza a `test-deploy.sh`. Simula Docker y usa directorios
  temporales, sin acceder a GHCR, a la VPS ni a servicios reales. Comprueba
  actualización, identidad de ejecución, rollback, señales y bloqueo compartido.

Para ejecutar las pruebas en Linux o WSL:

```sh
python3 scripts/release/test-deploy.py
```

La invocación directa en la VPS conserva los siete argumentos posicionales:

```sh
python3 /ruta/scripts/release/deploy.py /srv/nexoruta "$SHA" "$RUN_NUMBER" \
  "$RUN_ATTEMPT" "$RUN_ID" "sha-$SHA-run-$RUN_ID-attempt-$RUN_ATTEMPT" "$PULL_USER"
```

El token `NEXORUTA_PULL_TOKEN` debe llegar por una variable de entorno segura.
No pasarlo como argumento ni escribirlo en el historial. El directorio destino
debe contener `compose.vps.yaml` y `.env`; Python debe estar instalado en la VPS.
Ya no se debe enviar el script a `bash -s`.

El script mantiene `.release.lock` durante toda la operación y el rollback.
Registra el número de ejecución antes de descargar imágenes, para rechazar
ejecuciones viejas incluso si la nueva falla. Actualiza solamente el servicio
`api` y `NEXORUTA_API_TAG`; `.env` nunca se interpreta como código. Ante un fallo
restaura sus bytes originales y la imagen inmutable anterior. Si no puede
restaurar, conserva los archivos privados de recuperación y lo informa por
stderr; elimina las credenciales temporales de Docker antes de conservarlos.
