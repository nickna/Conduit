"""Component coverage baselines measured from executable test evidence."""
import argparse
import os
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def dotnet_components(path):
    components = {}
    for node in ET.parse(path).findall('.//class'):
        lines = node.findall('./lines/line')
        if lines:
            components[node.attrib['name']] = 100 * sum(int(line.attrib['hits']) > 0 for line in lines) / len(lines)
    return components


def jest_components(path):
    components = {}
    for name, item in json.loads(Path(path).read_text(encoding='utf-8')).items():
        name = name.replace('\\', '/')
        if '/src/' not in name:
            continue
        statements = item['s']
        components[name.split('/src/', 1)[1]] = 100 * sum(hits > 0 for hits in statements.values()) / max(1, len(statements))
    return components


def validate(actual, baseline):
    failures = []
    for name, minimum in baseline.items():
        if name not in actual or actual[name] + 0.001 < minimum:
            failures.append(f"{name}: {actual.get(name, 'missing')}%, minimum {minimum}%")
    if failures:
        raise ValueError('Coverage regression:\n' + '\n'.join(failures))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('kind', choices=['dotnet', 'jest'])
    parser.add_argument('coverage', type=Path)
    parser.add_argument('--measure', action='store_true')
    args = parser.parse_args()
    actual = (dotnet_components if args.kind == 'dotnet' else jest_components)(args.coverage)
    if args.measure:
        print(json.dumps(actual, indent=2, sort_keys=True))
    else:
        baseline = json.loads(Path(__file__).with_name('coverage-baseline.json').read_text())[args.kind]
        validate(actual, baseline)
        print(json.dumps({name: round(actual[name], 2) for name in baseline}, indent=2))
        if os.environ.get('GITHUB_STEP_SUMMARY'):
            with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as summary:
                summary.write(f'\n### {args.kind} component coverage\n\n| Component | Measured % | Minimum % |\n|---|---:|---:|\n')
                for name, minimum in baseline.items():
                    summary.write(f'| `{name}` | {actual[name]:.2f} | {minimum} |\n')


if __name__ == '__main__':
    main()
