"""Synthetic end-to-end F4 contract/scoring exercise. Never invokes a model or host."""
import argparse
import hashlib
import importlib.util
import itertools
import json
from pathlib import Path
import tempfile

from phase_contract import validate_phase, digest


def write(path, value):
    with path.open('x', encoding='utf-8') as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)


def ref(root, path):
    return {'path': path.relative_to(root).as_posix(),
            'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}


def run(output):
    output = Path(output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    spec = importlib.util.spec_from_file_location('f4_evaluation',
        Path(__file__).resolve().parents[1] / 'evaluator-only' / 'evaluation.py')
    evaluation = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(evaluation)
    rows = list(itertools.product(('F4-R', 'F4-N'), (1, 2),
                                 ('direct', 'generic', 'exception_checklist')))
    ids = [f'f4-synthetic-{task}-{rep}-{arm}' for task, rep, arm in rows]
    tool_names = ('find_symbols', 'read_symbols', 'relations', 'source_read', 'source_search', 'source_list')
    tools = [{'name': name, 'description': 'Synthetic neutral tool.',
              'inputSchema': {'type': 'object', 'properties': {}, 'additionalProperties': False}}
             for name in tool_names]
    stack = {'system': 'Use only provided evidence.', 'tools': tools,
             'guide': 'Use typed/source evidence when needed.'}
    contract = {'run_id': 'f4-synthetic', 'service_tier': 'default',
                'neutral_reviewed': True, 'neutral_stack': stack, 'session_ids': ids}
    attempts = []
    for ident, (task, rep, arm) in zip(ids, rows):
        directory = output / ident
        directory.mkdir()
        session = {'session_id': ident, 'arm': arm, 'task_id': task, 'repetition': rep,
                   'initial_messages': [{'role': 'user', 'content': 'Synthetic task.'}]}
        if arm != 'direct':
            session['followup'] = {'role': 'user', 'content': 'Synthetic assigned follow-up.'}
        contract.setdefault('session_sha256', {})[ident] = digest(session)
        first = {k: session[k] for k in ('session_id', 'arm', 'task_id', 'repetition')}
        first.update(phase='final' if arm == 'direct' else 'precheck', service_tier='default',
                     stack=stack, messages=session['initial_messages'] +
                     [{'role': 'assistant', 'content': 'SYNTHETIC TEST ANSWER, not model output.'}],
                     accepted_tools=[], delivered_tools=[])
        record = {'call_id': 'synthetic-call', 'request_id': 'synthetic-request',
                  'payload_sha256': hashlib.sha256(b'SYNTHETIC EVIDENCE').hexdigest()}
        first['messages'].insert(-1, {'role': 'tool', 'tool_call_id': record['call_id'],
                                     'request_id': record['request_id'], 'content': 'SYNTHETIC EVIDENCE'})
        first['accepted_tools'] = [record]
        first['delivered_tools'] = [dict(record, accepted=True)]
        validate_phase(contract, session, first)
        precheck = None
        if arm != 'direct':
            pre = directory / 'precheck.txt'
            pre.write_text(first['messages'][-1]['content'], encoding='utf-8')
            precheck = ref(output, pre)
            session['followup'] = {'role': 'user', 'content': 'Synthetic assigned follow-up.'}
            final = dict(first, phase='final', accepted_tools=[], delivered_tools=[], messages=first['messages'] +
                         [session['followup'], {'role': 'assistant', 'content': 'SYNTHETIC FINAL.'}])
            # A valid zero-new-call recheck is important: no forced evidence retrieval.
            validate_phase(contract, session, final, first)
        else:
            final = first
        answer = directory / 'final.txt'
        answer.write_text(final['messages'][-1]['content'], encoding='utf-8')
        receipt = {'study_id': 'f4-synthetic', 'mode': 'synthetic', 'id': ident,
                   'task': task, 'rep': rep, 'arm': arm, 'state': 'completed',
                   'protocol_compliant': True, 'usage_known': True,
                   'host_cost_usd': '0', 'settled_cost_usd': '0',
                   'final': ref(output, answer), 'precheck': precheck}
        path = directory / 'receipt.json'
        write(path, receipt)
        attempts.append(dict(id=ident, task=task, rep=rep, arm=arm, receipt=ref(output, path)))
    manifest = output / 'manifest.json'
    write(manifest, {'format': 'f4-evaluation-export-v1', 'protocol_revision': 'f4-pilot-v1',
                     'study_id': 'f4-synthetic', 'mode': 'synthetic', 'attempts': attempts})
    packets = output / 'packets'
    counts = evaluation.prepare(manifest, packets)
    scores = []
    for phase in ('final', 'precheck'):
        for score in evaluation.read(packets / ('public-' + phase) / 'scores-template.json'):
            score['components'] = {key: 2 for key in evaluation.FIELDS}
            score['rationale'] = 'Synthetic fixture score, not a quality measurement.'
            scores.append(score)
    score_path, log_path = output / 'synthetic-scores.json', output / 'synthetic-adjudication.json'
    write(score_path, scores)
    write(log_path, [])
    evaluation.freeze(packets, score_path, log_path)
    result = evaluation.aggregate(packets)
    assert counts['final_packets'] == 12 and counts['precheck_packets'] == 8
    assert result['synthetic_not_measurement'] is True
    return {'success': True, 'synthetic_not_measurement': True, 'model_calls': 0,
            'paid_execution': False, **counts, 'output': str(output)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, help='New directory only; existing outputs are preserved.')
    args = parser.parse_args()
    if args.output:
        result = run(args.output)
    else:
        with tempfile.TemporaryDirectory(prefix='f4-synthetic-') as temporary:
            result = run(Path(temporary) / 'run')
            result['output'] = 'temporary synthetic outputs removed'
    print(json.dumps(result))


if __name__ == '__main__':
    main()
