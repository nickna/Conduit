"""Validate and retain test execution evidence. Uses only Python's standard library."""
import argparse
import json
import os
from pathlib import Path
import xml.etree.ElementTree as ET


def summarize_trx(path):
    root = ET.parse(path).getroot()
    results = root.findall('.//{*}UnitTestResult')
    definitions = {node.attrib['id']: node.find('{*}TestMethod').attrib
                   for node in root.findall('.//{*}UnitTest')}
    tests = []
    for result in results:
        method = definitions.get(result.attrib['testId'], {})
        tests.append({'name': f"{method.get('className', '')}.{method.get('name', result.attrib['testName'])}",
                      'outcome': result.attrib['outcome'], 'duration': result.attrib.get('duration')})
    return {'discovered': len(results), 'executed': sum(t['outcome'] in ('Passed', 'Failed') for t in tests),
            'passed': sum(t['outcome'] == 'Passed' for t in tests),
            'failed': sum(t['outcome'] == 'Failed' for t in tests),
            'skipped': sum(t['outcome'] not in ('Passed', 'Failed') for t in tests), 'tests': tests}


def validate(summary, minimum=1, maximum_skips=0):
    if summary['executed'] < minimum:
        raise ValueError(f"Expected at least {minimum} executed tests, found {summary['executed']}")
    if summary['failed']:
        raise ValueError(f"{summary['failed']} tests failed")
    if summary['skipped'] > maximum_skips:
        raise ValueError(f"Skip budget {maximum_skips} exceeded: {summary['skipped']}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('trx', type=Path)
    parser.add_argument('--minimum', type=int, default=1)
    parser.add_argument('--maximum-skips', type=int, default=0)
    args = parser.parse_args()
    summary = summarize_trx(args.trx)
    # Write evidence even if validation fails.
    args.trx.with_suffix('.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print(json.dumps({key: value for key, value in summary.items() if key != 'tests'}))
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as output:
            output.write(f"\n{args.trx.stem}: discovered {summary['discovered']}, executed {summary['executed']}, passed {summary['passed']}, failed {summary['failed']}, skipped {summary['skipped']}.\n")
    validate(summary, args.minimum, args.maximum_skips)


if __name__ == '__main__':
    main()
