"""The gate must union hits without double-counting or hiding an uncovered layer."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('coverage_report', Path(__file__).with_name('report.py'))
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)

class CoverageGateTests(unittest.TestCase):
    def document(self, root, name, covered, absolute=False):
        packages = ''
        for layer in report.LAYERS:
            filename = f'/checkout/src/{layer}/Rules.cs' if absolute else f'{layer}/Rules.cs'
            lines = ''.join(f'<line number="{n}" hits="{int(n in covered)}"/>' for n in range(1, 11))
            packages += f'<package name="{layer}"><classes><class filename="{filename}"><lines>{lines}</lines></class></classes></package>'
        path = Path(root)/name
        path.write_text(f'<coverage><packages>{packages}</packages></coverage>')
        return str(path)

    def test_union_uses_source_identity_and_any_hit(self):
        with tempfile.TemporaryDirectory() as root:
            paths = [self.document(root, 'unit.xml', range(1, 6), True), self.document(root, 'integration.xml', range(4, 9))]
            for value in report.measure(paths).values():
                self.assertEqual((value['covered'], value['total']), (8, 10))
                self.assertTrue(value['passes'])

    def test_seventy_percent_is_insufficient(self):
        with tempfile.TemporaryDirectory() as root:
            for value in report.measure([self.document(root, 'exact.xml', range(1, 8))]).values():
                self.assertEqual(value['percent'], 70)
                self.assertFalse(value['passes'])

    def test_missing_layer_fails_closed(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root)/'missing.xml'
            path.write_text('<coverage/>')
            with self.assertRaises(ValueError):
                report.measure([str(path)])

if __name__ == '__main__':
    unittest.main()
