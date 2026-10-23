#!/usr/bin/env python3
"""Pruebas del despliegue con Docker simulado: no acceden a una VPS ni a GHCR."""

import contextlib
import io
import os
from pathlib import Path
import runpy
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from deploy import Release, ReleaseError, arguments


class FakeRelease(Release):
    """Simula los resultados de Docker sin reemplazar la lógica de despliegue."""

    def __init__(self, args, failure=None, previous=True):
        super().__init__(args)
        self.failure = failure
        self.has_previous = previous
        self.calls = []

    def command(self, command, **kwargs):
        # Registrar llamadas e inyectar fallos en cada etapa sin ejecutar Docker realmente.
        self.calls.append(command)
        if command[1] == "login":
            assert kwargs["input"] == "test-token"
            assert "NEXORUTA_PULL_TOKEN" not in self.env
            (Path(self.env["DOCKER_CONFIG"]) / "config.json").write_text("credential")
        elif command[1] == "inspect":
            field = command[3]
            value = {"{{.Image}}": "sha256:" + "1" * 64,
                     "{{.Config.Image}}": self.image + ":" + self.args.tag}.get(field)
            if value is None:
                value = "running unhealthy" if self.failure == "health" else "running healthy"
            if self.failure == "image" and field == "{{.Config.Image}}":
                value = "unexpected:latest"
            return subprocess.CompletedProcess(command, 0, value)
        elif "ps" in command:
            return subprocess.CompletedProcess(command, 0, "container-api" if self.has_previous else "")
        elif "pull" in command and self.failure == "pull":
            raise subprocess.CalledProcessError(31, command)
        elif "up" in command:
            rollback = any(str(part).endswith("rollback.yaml") for part in command)
            if rollback:
                self.rollback_contents = (self.tmp / "rollback.yaml").read_text()
            if self.failure == "signal" and not rollback:
                raise SystemExit(143)
            if self.failure in ("up", "rollback", "restore") and not rollback:
                raise subprocess.CalledProcessError(32, command)
            if self.failure == "rollback" and rollback:
                raise subprocess.CalledProcessError(34, command)
        return subprocess.CompletedProcess(command, 0, "")


class DeployTests(unittest.TestCase):
    def setUp(self):
        # Cada prueba recibe un directorio descartable y valores sensibles completamente ficticios.
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        (self.directory / "compose.vps.yaml").touch()
        self.original = (b"# preserve me\r\nNEXORUTA_POSTGRES_PASSWORD=literal$ with spaces # !\r\n"
                         b"NEXORUTA_API_TAG=old\r\nNEXORUTA_COMMERCE_TAG=sibling\r\n")
        (self.directory / ".env").write_bytes(self.original)
        self.env = patch.dict(os.environ, {"NEXORUTA_PULL_TOKEN": "test-token"})
        self.env.start()
        self.addCleanup(self.env.stop)

    def release(self, failure=None, previous=True, run="10", attempt="1", sha="1" * 40):
        # Construir una ejecución válida; cada caso cambia solo la condición que quiere probar.
        args = arguments([str(self.directory), sha, run, attempt, "100",
                          f"sha-{sha}-run-100-attempt-{attempt}", "pull-user"])
        return FakeRelease(args, failure, previous)

    def execute(self, release):
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            release.run()

    def journal(self):
        return (self.directory / ".release-backend.log").read_text()

    def test_healthy_release_preserves_sibling_values_and_never_runs_shell(self):
        release = self.release()
        self.execute(release)
        updated = (self.directory / ".env").read_bytes()
        self.assertIn(b"PASSWORD=literal$ with spaces # !\r\n", updated)
        self.assertIn(b"NEXORUTA_COMMERCE_TAG=sibling\r\n", updated)
        self.assertIn(f"NEXORUTA_API_TAG={release.args.tag}\n".encode(), updated)
        self.assertIn("result=healthy", self.journal())
        self.assertFalse(list(self.directory.glob(".release-backend.*/*")))
        for call in release.calls:
            self.assertNotIn("down", call)
            self.assertNotIn("prune", call)
            if "up" in call:
                self.assertIn("--no-deps", call)
                self.assertIn("--wait", call)
                self.assertEqual(call[-1], "api")

    def test_idempotent_execution(self):
        self.execute(self.release())
        self.execute(self.release())

    def test_duplicate_tags_are_replaced_once_and_missing_tag_is_appended(self):
        for original in (b"export NEXORUTA_API_TAG=old\nNEXORUTA_API_TAG=duplicate\nOTHER=value\n",
                         b"OTHER=literal$ without newline"):
            with self.subTest(original=original):
                (self.directory / ".env").write_bytes(original)
                self.execute(self.release())
                updated = (self.directory / ".env").read_bytes()
                self.assertEqual(updated.count(b"NEXORUTA_API_TAG="), 1)
                self.assertIn(b"OTHER=", updated)

    def test_older_run_and_identity_collision_are_rejected(self):
        self.execute(self.release())
        for release in (self.release(run="9"), self.release(sha="2" * 40)):
            with self.assertRaises(ReleaseError) as caught:
                self.execute(release)
            self.assertEqual(caught.exception.code, 3)
            self.assertFalse(release.calls)

    def test_older_attempt_is_rejected(self):
        self.execute(self.release(attempt="2"))
        with self.assertRaises(ReleaseError):
            self.execute(self.release())

    def test_failures_restore_exact_env_and_immutable_images(self):
        # Repetir la misma garantía de recuperación para descarga, arranque, salud y señales.
        for failure in ("pull", "up", "health", "image", "signal"):
            with self.subTest(failure=failure):
                (self.directory / ".env").write_bytes(self.original)
                release = self.release(failure)
                with self.assertRaises((subprocess.CalledProcessError, ReleaseError, SystemExit)):
                    self.execute(release)
                self.assertEqual((self.directory / ".env").read_bytes(), self.original)
                self.assertIn("result=failed_rolled_back", self.journal())
                rollback = [call for call in release.calls if any(str(p).endswith("rollback.yaml") for p in call)]
                self.assertEqual(len(rollback), 1)
                self.assertIn('image: "sha256:' + "1" * 64, release.rollback_contents)
                self.assertFalse(release.tmp.exists())
                with self.assertRaises(ReleaseError):
                    self.execute(self.release(run="9"))

    def test_first_release_failure_has_no_previous_images(self):
        with self.assertRaises(subprocess.CalledProcessError):
            self.execute(self.release("up", previous=False))
        self.assertEqual((self.directory / ".env").read_bytes(), self.original)
        self.assertIn("result=failed_no_previous_images", self.journal())

    def test_rollback_failure_preserves_recovery_without_credentials(self):
        release = self.release("rollback")
        with self.assertRaises(subprocess.CalledProcessError):
            self.execute(release)
        self.assertIn("result=failed_rollback_failed", self.journal())
        self.assertEqual((release.tmp / "env.before").read_bytes(), self.original)
        self.assertIn("sha256:" + "1" * 64, (release.tmp / "rollback.yaml").read_text())
        self.assertTrue((release.tmp / "recovery.txt").exists())
        self.assertFalse((release.tmp / "docker").exists())

    def test_env_restore_failure_does_not_claim_rollback(self):
        # Forzar un fallo al copiar .env comprueba que nunca se informe una recuperación falsa.
        import shutil
        copy = shutil.copy2

        def copy_with_failure(source, destination):
            if Path(destination).name == "env.restore":
                raise OSError("simulated restoration failure")
            return copy(source, destination)

        release = self.release("restore")
        with patch("deploy.shutil.copy2", side_effect=copy_with_failure):
            with self.assertRaises(subprocess.CalledProcessError):
                self.execute(release)
        self.assertIn("result=failed_env_restore", self.journal())
        self.assertNotIn("result=failed_rolled_back", self.journal())
        self.assertEqual((release.tmp / "env.before").read_bytes(), self.original)
        self.assertFalse((release.tmp / "docker").exists())

    def test_invalid_arguments(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            arguments(["relative", "invalid", "0", "1", "100", "latest", "user"])

    def test_corrupt_watermark(self):
        (self.directory / ".release-backend.state").write_text("invalid")
        release = self.release()
        with self.assertRaises(ReleaseError):
            self.execute(release)
        self.assertFalse(release.calls)

    def test_shared_lock_precedes_watermark_and_docker(self):
        # Usar un proceso Python real permite comprobar el bloqueo entre procesos de Linux.
        import fcntl
        import sys
        with (self.directory / ".release.lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            args = self.release().args
            process = subprocess.Popen([sys.executable, str(Path(__file__).with_name("deploy.py")),
                                        args.directory, args.sha, args.run, args.attempt,
                                        args.run_id, args.tag, args.pull_user],
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                       env={**os.environ, "NEXORUTA_PULL_TOKEN": ""})
            try:
                with self.assertRaises(subprocess.TimeoutExpired):
                    process.wait(timeout=0.3)
                self.assertFalse((self.directory / ".release-backend.state").exists())
                fcntl.flock(lock, fcntl.LOCK_UN)
                process.communicate(timeout=10)
                self.assertNotEqual(process.returncode, 0)
                self.assertTrue((self.directory / ".release-backend.state").exists())
            finally:
                if process.poll() is None:
                    process.kill()
                process.communicate()


class DispatchTests(unittest.TestCase):
    def dispatch(self):
        # Ejecutar el script como lo hace Actions; las pruebas interceptan la llamada HTTP.
        runpy.run_path(str(Path(__file__).with_name("dispatch-integration.py")), run_name="__main__")

    def test_dispatch_preserves_metadata_and_uses_authenticated_post(self):
        import json
        with patch.dict(os.environ, {"NEXORUTA_INTEGRATION_TOKEN": "test-token",
                                     "GITHUB_REPOSITORY": "NexoRuta/NexoRuta-Backend",
                                     "GITHUB_SHA": "1" * 40}), patch("urllib.request.urlopen") as urlopen:
            urlopen.return_value.__enter__.return_value.status = 204
            with contextlib.redirect_stdout(io.StringIO()):
                self.dispatch()
            request = urlopen.call_args.args[0]
            self.assertEqual(request.get_method(), "POST")
            self.assertEqual(request.get_header("Authorization"), "Bearer test-token")
            self.assertEqual(request.full_url, "https://api.github.com/repos/NexoRuta/NexoRuta/dispatches")
            self.assertEqual(json.loads(request.data)["client_payload"]["sha"], "1" * 40)
            self.assertEqual(urlopen.call_args.kwargs["timeout"], 30)

    def test_missing_token_does_not_call_github(self):
        with patch.dict(os.environ, {"NEXORUTA_INTEGRATION_TOKEN": ""}), patch("urllib.request.urlopen") as urlopen:
            with self.assertRaises(SystemExit):
                self.dispatch()
            urlopen.assert_not_called()


if __name__ == "__main__":
    unittest.main(verbosity=2)
