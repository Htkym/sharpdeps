import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from check_contract import check, ContractError, read


def fixture():
    props = {
        'query': {'type': 'string', 'pattern': '^hocc_[0-9a-f]{64}$'},
        'projectId': {'type': 'string', 'pattern': '^hpr_[0-9a-f]{64}$'},
        'variantId': {'type': 'string', 'pattern': '^hvar_[0-9a-f]{64}$'},
        'scopePolicy': {'type': 'string', 'enum': ['unique'], 'default': 'unique'},
        'responseMode': {'type': 'string', 'enum': ['evidence', 'detail'], 'default': 'evidence'},
        'maxChars': {'type': 'integer', 'minimum': 2048, 'maximum': 12000, 'default': 12000},
    }
    advertised = {'tools': [{'name': 'explore', 'inputSchema': {
        'type': 'object', 'properties': props, 'required': ['query', 'projectId', 'variantId'], 'additionalProperties': False}}]}
    effective = copy.deepcopy(advertised)
    effective.update(format='sharpdeps-effective-contract-v1', enforcement_source='Synthetic independently declared fixture; not runtime evidence')
    return advertised, effective


class ContractTests(unittest.TestCase):
    def test_match(self):
        self.assertEqual(check(*fixture()), ['explore'])

    def test_budget_boundaries(self):
        for field, value in [('maximum', 1000000), ('maximum', 11999), ('minimum', 1), ('default', 22000), ('type', 'number')]:
            with self.subTest(field=field, value=value):
                a, e = fixture(); a['tools'][0]['inputSchema']['properties']['maxChars'][field] = value
                with self.assertRaises(ContractError): check(a, e)

    def test_discovery_and_names_rejected(self):
        a, e = fixture(); a['tools'][0]['inputSchema']['properties']['scopePolicy']['enum'].append('discover')
        with self.assertRaises(ContractError): check(a, e)
        a, e = fixture(); del a['tools'][0]['inputSchema']['properties']['query']['pattern']
        with self.assertRaises(ContractError): check(a, e)

    def test_missing_required_and_extra_property(self):
        for mutation in ('required', 'extra', 'open', 'tool'):
            a, e = fixture(); s = a['tools'][0]['inputSchema']
            if mutation == 'required': s['required'].remove('projectId')
            if mutation == 'extra': s['properties']['pathTo'] = {'type': 'string'}
            if mutation == 'open': s['additionalProperties'] = True
            if mutation == 'tool': a['tools'].append({'name': 'other', 'inputSchema': copy.deepcopy(s)})
            with self.subTest(mutation=mutation), self.assertRaises(ContractError): check(a, e)

    def test_annotations_and_order(self):
        a, e = fixture(); s = a['tools'][0]['inputSchema']; s['description'] = 'Documentation only'
        s['required'].reverse(); s['properties']['responseMode']['enum'].reverse()
        self.assertEqual(check(a, e), ['explore'])

    def test_unknown_constraint_even_if_both_sides_agree(self):
        a, e = fixture()
        for x in (a, e): x['tools'][0]['inputSchema']['allOf'] = []
        with self.assertRaises(ContractError): check(a, e)

    def test_duplicate_tool_and_missing_provenance(self):
        a, e = fixture(); a['tools'].append(copy.deepcopy(a['tools'][0]))
        with self.assertRaises(ContractError): check(a, e)
        a, e = fixture(); e['enforcement_source'] = ''
        with self.assertRaises(ContractError): check(a, e)

    def test_bool_not_numeric(self):
        a, e = fixture(); a['tools'][0]['inputSchema']['properties']['maxChars']['maximum'] = True
        with self.assertRaises(ContractError): check(a, e)

    def test_unique_items_match_true_and_false(self):
        for value in (True, False):
            a, e = fixture()
            for x in (a, e):
                x['tools'][0]['inputSchema']['properties']['selectors'] = {
                    'type': 'array', 'items': {'type': 'string'}, 'uniqueItems': value}
            self.assertEqual(check(a, e), ['explore'])

    def test_unique_items_missing_or_changed_is_mismatch(self):
        for original in (True, False):
            for missing in (True, False):
                a, e = fixture()
                for x in (a, e):
                    x['tools'][0]['inputSchema']['properties']['selectors'] = {
                        'type': 'array', 'items': {'type': 'string'}, 'uniqueItems': original}
                prop = a['tools'][0]['inputSchema']['properties']['selectors']
                if missing: del prop['uniqueItems']
                else: prop['uniqueItems'] = not original
                with self.subTest(original=original, missing=missing), self.assertRaises(ContractError):
                    check(a, e)

    def test_unique_items_rejects_nonbool_even_if_both_agree(self):
        for value in (0, 1, 'true', 'false', None, [], {}):
            a, e = fixture()
            for x in (a, e):
                x['tools'][0]['inputSchema']['properties']['selectors'] = {
                    'type': 'array', 'items': {'type': 'string'}, 'uniqueItems': value}
            with self.subTest(value=value), self.assertRaisesRegex(ContractError, 'invalid uniqueItems'):
                check(a, e)

    def test_duplicate_json_key(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / 'bad.json'; p.write_text('{"tools":[],"tools":[]}')
            with self.assertRaises(ContractError): read(p)

    def test_invalid_default_even_when_both_agree(self):
        a, e = fixture()
        for x in (a, e): x['tools'][0]['inputSchema']['properties']['maxChars']['default'] = 22000
        with self.assertRaises(ContractError): check(a, e)

    def test_nonobject_effective(self):
        with self.assertRaises(ContractError): check({}, [])

    def test_cli_fails_before_caller_can_proceed(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td); a, e = fixture()
            p, q = root / 'a.json', root / 'e.json'
            p.write_text(json.dumps(a)); q.write_text(json.dumps(e))
            command = [sys.executable, '-B', str(Path(__file__).with_name('check_contract.py')), '--advertised', str(p), '--effective', str(q)]
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0); self.assertFalse(json.loads(result.stdout)['paid_execution'])
            a['tools'][0]['inputSchema']['properties']['maxChars']['maximum'] = 1000000; p.write_text(json.dumps(a))
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 2); self.assertFalse(json.loads(result.stdout)['success'])


if __name__ == '__main__': unittest.main()
