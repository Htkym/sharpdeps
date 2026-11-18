"""Offline, fail-closed comparison of advertised and independently specified contracts.

No provider calls, tool execution, or automatic repair. Python standard library only.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import sys


class ContractError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise ContractError(message)


def read(path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, 'Duplicate JSON key: ' + key)
            result[key] = value
        return result
    return json.loads(Path(path).read_text(encoding='utf-8-sig'), object_pairs_hook=unique,
                      parse_constant=lambda value: (_ for _ in ()).throw(ContractError('Nonfinite JSON')))


def canonical(schema, where):
    """Conservative supported subset; unknown constraints never silently ignored."""
    require(type(schema) is dict, where + ': schema must be object')
    annotations = {'description', 'title', 'examples', '$comment'}
    supported = {'type', 'properties', 'required', 'additionalProperties', 'enum',
                 'minimum', 'maximum', 'minLength', 'maxLength', 'pattern', 'default',
                 'items', 'minItems', 'maxItems', 'uniqueItems'}
    require(not set(schema) - supported - annotations, where + ': unsupported schema keywords')
    result = {k: v for k, v in schema.items() if k not in annotations}
    kind = result.get('type')
    require(kind in ('object', 'string', 'integer', 'number', 'boolean', 'array'), where + ': explicit type required')
    if kind == 'object':
        require(result.get('additionalProperties') is False, where + ': explicit closed object required')
        props = result.get('properties')
        required = result.get('required', [])
        require(type(props) is dict and type(required) is list and all(type(x) is str for x in required), where + ': properties/required invalid')
        require(len(set(required)) == len(required) and set(required) <= set(props), where + ': invalid required names')
        result['required'] = sorted(required)
        result['properties'] = {k: canonical(v, where + '.' + k) for k, v in props.items()}
    if 'uniqueItems' in result:
        require(type(result['uniqueItems']) is bool, where + ': invalid uniqueItems')
    if 'items' in result:
        result['items'] = canonical(result['items'], where + '[]')
    if kind == 'array':
        require('items' in result, where + ': items required')
    for lo, hi in [('minimum', 'maximum'), ('minLength', 'maxLength'), ('minItems', 'maxItems')]:
        for key in (lo, hi):
            if key in result:
                require(type(result[key]) in (int, float), where + ': invalid ' + key)
                require(math.isfinite(result[key]), where + ': nonfinite ' + key)
                if key != 'minimum' and key != 'maximum':
                    require(type(result[key]) is int and result[key] >= 0, where + ': invalid ' + key)
        if lo in result and hi in result:
            require(result[lo] <= result[hi], where + ': inverted bounds')
    if 'pattern' in result:
        require(type(result['pattern']) is str, where + ': invalid pattern')
        re.compile(result['pattern'])  # Syntax check only, not a replacement JSON Schema engine.
    if 'enum' in result:
        require(type(result['enum']) is list and bool(result['enum']), where + ': invalid enum')
        result['enum'] = sorted(result['enum'], key=lambda x: json.dumps(x, sort_keys=True))
    if 'default' in result:
        value = result['default']
        expected_types = {'object': (dict,), 'array': (list,), 'string': (str,),
                          'integer': (int,), 'number': (int, float), 'boolean': (bool,)}
        require(type(value) in expected_types[kind], where + ': default type mismatch')
        if 'enum' in result:
            require(any(type(value) is type(x) and value == x for x in result['enum']), where + ': default outside enum')
        if kind in ('integer', 'number'):
            require(math.isfinite(value) and result.get('minimum', -math.inf) <= value <= result.get('maximum', math.inf), where + ': default outside bounds')
        if kind == 'string':
            require(result.get('minLength', 0) <= len(value) <= result.get('maxLength', math.inf), where + ': default length')
            if 'pattern' in result:
                require(re.search(result['pattern'], value) is not None, where + ': default pattern mismatch')
    return result


def tool_map(document):
    tools = document.get('tools') if type(document) is dict else None
    require(type(tools) is list and bool(tools), 'Nonempty tools list required')
    result = {}
    for tool in tools:
        require(type(tool) is dict and type(tool.get('name')) is str and bool(tool['name']), 'Invalid tool')
        name = tool['name']
        require(name not in result, 'Duplicate tool: ' + name)
        result[name] = canonical(tool.get('inputSchema'), name)
    return result


def check(advertised, effective):
    require(type(effective) is dict, 'Effective contract must be object')
    require(effective.get('format') == 'sharpdeps-effective-contract-v1', 'Effective contract version required')
    require(type(effective.get('enforcement_source')) is str and bool(effective['enforcement_source'].strip()), 'Independent enforcement source required')
    actual, expected = tool_map(advertised), tool_map(effective)
    require(set(actual) == set(expected), 'Advertised/effective tool sets differ')
    for name in sorted(expected):
        # JSON distinguishes bool from integer, unlike Python dictionary equality.
        require(json.dumps(actual[name], sort_keys=True) == json.dumps(expected[name], sort_keys=True),
                'Advertised/effective input contract differs: ' + name)
    return sorted(expected)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--advertised', required=True, type=Path)
    parser.add_argument('--effective', required=True, type=Path)
    args = parser.parse_args()
    try:
        names = check(read(args.advertised), read(args.effective))
        print(json.dumps({'success': True, 'tools': names, 'paid_execution': False,
                          'advertised_sha256': hashlib.sha256(args.advertised.read_bytes()).hexdigest(),
                          'effective_sha256': hashlib.sha256(args.effective.read_bytes()).hexdigest(),
                          'scope': 'Contract declaration equality only; runtime enforcement requires separate tests.'}))
        return 0
    except (ValueError, TypeError, KeyError, OSError, re.error) as error:
        print(json.dumps({'success': False, 'paid_execution': False, 'error': str(error)}))
        return 2


if __name__ == '__main__':
    sys.exit(main())
