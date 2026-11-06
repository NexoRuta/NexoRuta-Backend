#!/usr/bin/env python3
"""Union Cobertura source lines across suites; require >70% in each backend layer."""
import argparse
import glob
import json
import os
from pathlib import Path
import xml.etree.ElementTree as ET

LAYERS = ('NexoRuta.Domain', 'NexoRuta.Application')

def measure(paths):
    layers = {name: {} for name in LAYERS}
    for path in paths:
        for package in ET.parse(path).findall('.//package'):
            name = package.get('name')
            if name not in layers:
                continue
            for cls in package.findall('./classes/class'):
                # Collector emits absolute paths locally and relative paths in CI.
                filename = cls.get('filename', '').replace('\\', '/')
                marker = f'{name}/'
                filename = filename.split(marker, 1)[-1]
                for line in cls.findall('./lines/line'):
                    key = (filename, int(line.get('number')))
                    layers[name][key] = layers[name].get(key, False) or int(line.get('hits')) > 0
    result = {}
    for name, lines in layers.items():
        if not lines:
            raise ValueError(f'Missing coverage for {name}')
        covered = sum(lines.values())
        result[name] = dict(covered=covered, total=len(lines), percent=100*covered/len(lines), passes=100*covered > 70*len(lines))
    return result

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('reports', nargs='+')
    parser.add_argument('--output', required=True)
    parser.add_argument('--minimum-reports', type=int, default=1)
    parser.add_argument('--baseline', action='store_true', help='Report without enforcing the final gate')
    args = parser.parse_args()
    paths = sorted({p for pattern in args.reports for p in glob.glob(pattern, recursive=True)})
    if len(paths) < args.minimum_reports:
        raise ValueError(f'Expected at least {args.minimum_reports} reports, found {len(paths)}')
    result = measure(paths)
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    (output/'summary.json').write_text(json.dumps(dict(metric='source line coverage; union across suites', threshold='strictly greater than 70% per layer', exclusions='No custom exclusions; collector defaults only', reports=len(paths), layers=result), indent=2)+'\n')
    summary = '| Backend layer | Covered / executable lines | Coverage | >70% |\n|---|---:|---:|---|\n'
    for name, value in result.items():
        summary += f"| {name} | {value['covered']} / {value['total']} | {value['percent']:.2f}% | {'PASS' if value['passes'] else 'FAIL'} |\n"
    (output/'summary.md').write_text(summary)
    print(summary)
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a') as stream:
            stream.write(summary)
    if not args.baseline and not all(v['passes'] for v in result.values()):
        raise SystemExit(1)

if __name__ == '__main__':
    main()
