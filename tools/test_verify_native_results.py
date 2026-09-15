"""Regression tests for the acceptance evidence gate (standard library only)."""
import unittest
import xml.etree.ElementTree as ET
from verify_native_results import NS, PREFIX, REQUIRED, SFTP, VAULT, discovery_names, verify


def trx(cases, completed=True, total=None):
    ns = '{' + NS['t'] + '}'
    root = ET.Element(ns + 'TestRun')
    results = ET.SubElement(root, ns + 'Results')
    for name, outcome in cases:
        ET.SubElement(results, ns + 'UnitTestResult', testName=name, outcome=outcome)
    summary = ET.SubElement(root, ns + 'ResultSummary', outcome='Completed' if completed else 'Aborted')
    ET.SubElement(summary, ns + 'Counters', total=str(len(cases) if total is None else total),
                  executed=str(sum(o != 'NotExecuted' for _, o in cases)),
                  passed=str(sum(o == 'Passed' for _, o in cases)),
                  failed=str(sum(o == 'Failed' for _, o in cases)))
    return ET.tostring(root)


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.names = sorted(REQUIRED | {PREFIX + 'Example.Test(value: "zażółć")'})
        self.cases = [(name, 'Passed') for name in self.names]

    def check(self, names=None, cases=None, allowed=None, **kwargs):
        return verify(self.names if names is None else names,
                      trx(self.cases if cases is None else cases, **kwargs), allowed or set())

    def test_complete_success(self):
        self.assertTrue(self.check()['passed'])

    def test_missing_pty_result_fails_even_when_counters_look_successful(self):
        cases = [(n, o) for n, o in self.cases if n not in REQUIRED]
        result = self.check(cases=cases)
        self.assertFalse(result['passed'])
        self.assertEqual(len(REQUIRED), len([p for p in result['problems'] if p.startswith('Missing result:')]))

    def test_discovery_cannot_hide_missing_pty_tests(self):
        self.assertFalse(self.check(names=[], cases=[])['passed'])

    def test_duplicate_discovery(self):
        self.assertFalse(self.check(names=self.names + [self.names[0]])['passed'])

    def test_duplicate_result(self):
        self.assertFalse(self.check(cases=self.cases + [self.cases[0]])['passed'])

    def test_unexpected_result(self):
        self.assertFalse(self.check(cases=self.cases + [(PREFIX + 'Extra.Test', 'Passed')])['passed'])

    def test_failure_timeout_and_unknown_outcome(self):
        for outcome in ('Failed', 'Timeout', 'NotExecuted', ''):
            with self.subTest(outcome=outcome):
                self.assertFalse(self.check(cases=[(self.names[0], outcome)] + self.cases[1:])['passed'])

    def test_required_tests_cannot_be_allowlisted_for_skipping(self):
        self.assertFalse(self.check(cases=[(n, 'NotExecuted') for n in self.names], allowed=set(self.names))['passed'])

    def test_only_known_fixture_skips_are_allowed(self):
        for name in SFTP | {VAULT}:
            self.assertTrue(self.check(names=self.names + [name],
                                      cases=self.cases + [(name, 'NotExecuted')], allowed={name})['passed'])

    def test_aborted_run(self):
        self.assertFalse(self.check(completed=False)['passed'])

    def test_inconsistent_counters(self):
        self.assertFalse(self.check(total=42)['passed'])

    def test_discovery_parser_retains_case_arguments(self):
        text = 'build output\nThe following Tests are available:\n' + '\n'.join('    ' + n for n in self.names)
        self.assertEqual(self.names, discovery_names('\x1b[32m' + text + '\x1b[0m'))

    def test_integration_requires_every_external_case(self):
        names = sorted(SFTP | {VAULT})
        self.assertTrue(verify(names, trx([(n, 'Passed') for n in names]), set(), set(names))['passed'])
        self.assertFalse(verify(names, trx([(n, 'NotExecuted') for n in names]), set(), set(names))['passed'])


if __name__ == '__main__':
    unittest.main()
