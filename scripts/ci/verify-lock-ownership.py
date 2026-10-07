"""Prove that the formerly repeated lock selection executed in the main suite."""
import json
import sys
from collections import Counter
from pathlib import Path

evidence = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8'))
inventory = json.loads(Path(__file__).with_name('lock-unit-ownership.json').read_text(encoding='utf-8'))
counts = Counter(test['name'].rsplit('.', 1)[0] for test in evidence['tests'] if test['outcome'] == 'Passed')
missing = [f'{name}: {counts[name]}/{minimum}' for name, minimum in inventory.items() if counts[name] < minimum]
if missing:
    raise ValueError('Main suite lost lock-policy ownership: ' + '; '.join(missing))
print(f'Main suite owns all {sum(inventory.values())} formerly repeated lock cases across {len(inventory)} classes.')
