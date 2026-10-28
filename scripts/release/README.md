# Scripts de release

Los scripts usan Python 3.10 o posterior y solamente la biblioteca estándar.
El workflow `ci.yml` ejecuta las pruebas con `python3` en `ubuntu-latest`, que
incluye ese intérprete. Los comentarios del código explican las decisiones de
seguridad y recuperación.

- `deploy.py`: herramienta opcional para una VPS **Linux** con Docker Compose.
  No forma parte de la publicación automática de imágenes del repositorio
  principal ni se ejecuta en la VM de demostración.
- `test-deploy.py`: simula Docker y usa directorios temporales, sin acceder a
  GHCR, a la VPS ni a servicios reales. Comprueba actualización, identidad de
  ejecución, fallos, señales y bloqueo compartido.

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

El script mantiene `.release.lock` durante toda la operación.
Registra el número de ejecución antes de descargar imágenes, para rechazar
ejecuciones viejas incluso si la nueva falla. Actualiza solamente el servicio
`api` y `NEXORUTA_API_TAG`; `.env` nunca se interpreta como código. Ante un fallo
el tag objetivo permanece en `.env` y no se reinicia una imagen anterior. Corregir
el despliegue y volver a ejecutar una release nueva; el watermark bloquea jobs
viejos que intenten reemplazarla.
