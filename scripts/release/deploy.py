#!/usr/bin/env python3
"""Despliegue opcional en una VPS Linux con Docker Compose, sin dependencias Python externas."""

import argparse
from datetime import datetime, timezone
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tempfile


class ReleaseError(Exception):
    """Error esperado del despliegue, con su código de salida para el workflow."""

    def __init__(self, message, code=2):
        super().__init__(message)
        self.code = code


def arguments(argv=None):
    # Validar la identidad completa evita desplegar un tag distinto del commit solicitado.
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("directory", "sha", "run", "attempt", "run_id", "tag", "pull_user"):
        parser.add_argument(name)
    args = parser.parse_args(argv)
    valid = (
        Path(args.directory).is_absolute()
        and re.fullmatch(r"[0-9a-f]{40}", args.sha)
        and re.fullmatch(r"[1-9][0-9]{0,14}", args.run)
        and re.fullmatch(r"[1-9][0-9]{0,8}", args.attempt)
        and re.fullmatch(r"[1-9][0-9]{0,19}", args.run_id)
        and args.tag == f"sha-{args.sha}-run-{args.run_id}-attempt-{args.attempt}"
        and re.fullmatch(r"[A-Za-z0-9_-]+", args.pull_user)
    )
    if not valid:
        parser.error("Invalid release arguments")
    return args


class Release:
    # Este componente administra solo la API; no modifica servicios de otros repositorios.
    services = ("api",)
    image = "ghcr.io/nexoruta/nexoruta-api"
    key = "NEXORUTA_API_TAG"

    def __init__(self, args):
        self.args = args
        self.directory = Path(args.directory)
        self.env = os.environ.copy()
        # El token se envía únicamente por stdin a docker login, nunca a sus hijos.
        self.token = self.env.pop("NEXORUTA_PULL_TOKEN", "")
        self.env.pop(self.key, None)
        # Todos los comandos Compose usan el mismo proyecto y archivo de configuración.
        self.compose = ["docker", "compose", "--project-name", "nexoruta-demo",
                        "--env-file", ".env", "-f", "compose.vps.yaml"]

    def command(self, command, **kwargs):
        # Se usan argumentos separados y check=True: no hay shell y los fallos se propagan.
        return subprocess.run(command, cwd=self.directory, env=self.env,
                              check=True, text=True, **kwargs)

    def output(self, command):
        # Las consultas a Docker devuelven un identificador o estado para verificarlo después.
        return self.command(command, stdout=subprocess.PIPE).stdout.strip()

    def record(self, result):
        # El historial permite relacionar cada resultado con el commit y la ejecución de Actions.
        stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        a = self.args
        with (self.directory / ".release-backend.log").open("a") as journal:
            journal.write(f"{stamp} component=backend run={a.run} attempt={a.attempt} "
                          f"sha={a.sha} tag={a.tag} result={result}\n")

    def record_image(self, message):
        with (self.directory / ".release-backend.log").open("a") as journal:
            journal.write(message + "\n")

    def watermark(self):
        # La última pareja (ejecución, intento) aceptada impide que un job viejo pise uno nuevo.
        state = self.directory / ".release-backend.state"
        if state.exists():
            fields = state.read_text().split()
            if (len(fields) != 4 or not re.fullmatch(r"[1-9][0-9]{0,14}", fields[0])
                    or not re.fullmatch(r"[1-9][0-9]{0,8}", fields[1])):
                raise ReleaseError("Invalid release high-watermark")
            previous = tuple(map(int, fields[:2]))
            current = (int(self.args.run), int(self.args.attempt))
            if current < previous:
                raise ReleaseError("Stale release rejected", 3)
            if current == previous and fields[2:] != [self.args.sha, self.args.tag]:
                raise ReleaseError("Execution identity collision", 3)
        # Se registra antes del pull: una ejecución nueva fallida también bloquea las viejas.
        pending = self.tmp / "state"
        a = self.args
        pending.write_text(f"{a.run} {a.attempt} {a.sha} {a.tag}\n")
        pending.replace(state)

    def update(self):
        # Autenticar la descarga usando una configuración Docker temporal y privada.
        if not self.token:
            raise ReleaseError("GHCR pull token required")
        try:
            self.command(["docker", "login", "ghcr.io", "--username", self.args.pull_user,
                          "--password-stdin"], input=self.token, stdout=subprocess.DEVNULL)
        finally:
            self.token = ""
        # .env es dato, nunca código. Solo reemplazamos nuestra clave; el resto mantiene sus bytes.
        original = (self.directory / ".env").read_bytes()
        pattern = rb"(?m)^[ \t]*(?:export[ \t]+)?NEXORUTA_API_TAG[ \t]*=[^\r\n]*(?:\r?\n|$)"
        replacement = f"{self.key}={self.args.tag}\n".encode()
        found = False

        def replace(match):
            # Si la clave aparece repetida, dejar una sola asignación con el tag nuevo.
            nonlocal found
            if found:
                return b""
            found = True
            return replacement

        updated = re.sub(pattern, replace, original)
        if not found:
            updated += (b"\n" if updated and not updated.endswith(b"\n") else b"") + replacement
        pending = self.tmp / "env.next"
        shutil.copy2(self.directory / ".env", pending)
        pending.write_bytes(updated)
        # El entorno y el watermark identifican la release objetivo incluso si su arranque falla.
        pending.replace(self.directory / ".env")
        # Descargar y recrear solo la API, esperando su healthcheck sin reiniciar dependencias.
        self.command(self.compose + ["pull", *self.services])
        self.command(self.compose + ["up", "-d", "--no-deps", "--wait", "--wait-timeout", "180", *self.services])
        for service in self.services:
            container = self.output(self.compose + ["ps", "-q", service])
            # No basta con que Docker termine: debe estar sano y ejecutar la imagen esperada.
            if not container or "\n" in container:
                raise ReleaseError(f"Missing or multiple containers for {service}")
            status = self.output(["docker", "inspect", "--format",
                                  "{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}}", container])
            if status != "running healthy":
                raise ReleaseError(f"Service not healthy: {service}", 1)
            image = self.output(["docker", "inspect", "--format", "{{.Config.Image}}", container])
            if image != f"{self.image}:{self.args.tag}":
                raise ReleaseError("Unexpected service image", 1)
            self.record_image(f"{service} image={image} source={self.args.sha}")
        self.record("healthy")
        print(f"Release healthy: backend {self.args.sha}")

    def run_locked(self):
        # Aislar la configuración temporal de Docker por ejecución.
        self.tmp = Path(tempfile.mkdtemp(prefix=".release-backend.", dir=self.directory))
        docker_config = self.tmp / "docker"
        docker_config.mkdir(mode=0o700)
        self.env["DOCKER_CONFIG"] = str(docker_config)
        try:
            self.watermark()
            self.record("started")
            self.update()
        except BaseException:
            self.record("failed")
            raise
        finally:
            try:
                # Nunca conservar credenciales de registro en el directorio del despliegue.
                shutil.rmtree(docker_config)
            except OSError:
                print("Registry credential cleanup failed; remove the temporary Docker config immediately", file=sys.stderr)
                raise
            finally:
                shutil.rmtree(self.tmp, ignore_errors=True)

    def run(self):
    # flock serializa las actualizaciones del mismo proyecto.
        import fcntl
        if not all((self.directory / name).is_file() for name in ("compose.vps.yaml", ".env")):
            raise ReleaseError("Missing VPS compose or .env")
        with (self.directory / ".release.lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            self.run_locked()


def main():
    # Los archivos nuevos solo son accesibles por el usuario que ejecuta el despliegue.
    os.umask(0o077)
    args = arguments()

    def interrupted(number, frame):
        # Convertir la señal en una salida controlada para registrar el fallo y limpiar.
        raise SystemExit(130 if number == signal.SIGINT else 143)

    for number in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
        signal.signal(number, interrupted)
    try:
        Release(args).run()
    # Devolver un código distinto de cero permite que Actions marque el job como fallido.
    except ReleaseError as error:
        print(error, file=sys.stderr)
        return error.code
    except (OSError, subprocess.CalledProcessError) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
