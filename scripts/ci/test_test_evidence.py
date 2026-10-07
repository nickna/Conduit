from io import StringIO
import unittest
from pathlib import Path
from importlib.util import spec_from_file_location, module_from_spec
spec = spec_from_file_location('evidence', Path(__file__).with_name('test-evidence.py'))
evidence = module_from_spec(spec)
spec.loader.exec_module(evidence)
coverage_spec = spec_from_file_location('coverage', Path(__file__).with_name('coverage-ratchet.py'))
coverage = module_from_spec(coverage_spec)
coverage_spec.loader.exec_module(coverage)


class EvidenceTests(unittest.TestCase):
    def test_coverage_regressions_and_missing_components_fail(self):
        coverage.validate({'component': 81.25}, {'component': 81})
        with self.assertRaises(ValueError):
            coverage.validate({'component': 80}, {'component': 81})
        with self.assertRaises(ValueError):
            coverage.validate({}, {'component': 81})

    def test_empty_failed_and_skip_only_suites_fail(self):
        for summary in ({'executed': 0, 'failed': 0, 'skipped': 0},
                        {'executed': 1, 'failed': 1, 'skipped': 0},
                        {'executed': 0, 'failed': 0, 'skipped': 1}):
            with self.assertRaises(ValueError):
                evidence.validate(summary)

    def test_skip_budget_is_explicit(self):
        summary = {'executed': 2, 'failed': 0, 'skipped': 1}
        with self.assertRaises(ValueError):
            evidence.validate(summary)
        evidence.validate(summary, maximum_skips=1)

    def test_display_names_do_not_lose_method_identity(self):
        path = StringIO('''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<TestDefinitions><UnitTest id="1"><TestMethod className="Suite" name="Method" /></UnitTest></TestDefinitions>
<Results><UnitTestResult testId="1" testName="Friendly display" outcome="Passed" /></Results></TestRun>''')
        summary = evidence.summarize_trx(path)
        self.assertEqual(summary['tests'][0]['name'], 'Suite.Method')
        evidence.validate(summary)


if __name__ == '__main__':
    unittest.main()
