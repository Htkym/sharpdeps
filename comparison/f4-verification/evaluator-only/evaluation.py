"""Offline F4 receipt verification, blind packet creation, score freeze, aggregation.

No model calls. Receipt protocol attestations must come from a separately reviewed
execution controller; hashes alone do not prove tool delivery or semantic accuracy.
"""
import argparse
from decimal import Decimal, InvalidOperation
import hashlib
import itertools
import json
import os
from pathlib import Path
import secrets
import stat
import tempfile

ARMS = ('direct', 'generic', 'exception_checklist')
TASKS = ('F4-R', 'F4-N')
FIELDS = ('caller_exception', 'cleanup_progress', 'failure_conditions', 'evidence')
RUBRIC_VERSION = 'f4-quality-v1'
# Semantic digest is stable across LF/CRLF checkouts. Changes require review.
RUBRIC_DIGEST = '7020e461d833fe36fed8b04632700f69b96bd022778375b7c3fb5f52bb418f45'
RUBRIC_PATH = Path(__file__).with_name('rubric.json')


def need(ok, message):
    if not ok:
        raise ValueError(message)


def read(path):
    path = no_links(path)
    def pairs(items):
        result = {}
        for key, value in items:
            need(key not in result, 'Duplicate JSON key')
            result[key] = value
        return result
    return json.loads(Path(path).read_text(encoding='utf-8-sig'), object_pairs_hook=pairs,
                      parse_constant=lambda x: (_ for _ in ()).throw(ValueError('Nonfinite JSON')))


def sha(path):
    return hashlib.sha256(no_links(path).read_bytes()).hexdigest()


def no_links(path):
    path = Path(os.path.abspath(path))
    for part in (path, *path.parents):
        if part.exists() or part.is_symlink():
            s = part.lstat()
            need(not stat.S_ISLNK(s.st_mode) and not getattr(s, 'st_file_attributes', 0) & 0x400, 'Linked/reparse path')
    return path


def bound_file(root, ref):
    need(type(ref) is dict and set(ref) == {'path', 'sha256'}, 'File reference schema')
    name = ref['path']
    need(type(name) is str and bool(name) and '\\' not in name and ':' not in name, 'Relative slash path required')
    rel = Path(name)
    need(not rel.is_absolute() and '..' not in rel.parts, 'Path escape')
    path = no_links(root / rel)
    need(path.is_file() and path.is_relative_to(root), 'Missing/nonlocal evidence file')
    need(type(ref['sha256']) is str and sha(path) == ref['sha256'], 'Evidence hash mismatch')
    return path


def money(value):
    if value is None:
        return None
    need(type(value) is str, 'Cost must be a decimal string or null')
    try:
        amount = Decimal(value)
    except InvalidOperation as error:
        raise ValueError('Invalid cost') from error
    need(amount.is_finite() and amount >= 0, 'Invalid cost')
    return amount


def write_new(path, value):
    no_links(path)
    with Path(path).open('x', encoding='utf-8') as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write('\n'); stream.flush(); os.fsync(stream.fileno())


def rubric_binding():
    data = read(RUBRIC_PATH)
    semantic = hashlib.sha256(json.dumps(data, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    need(semantic == RUBRIC_DIGEST, 'Reviewed rubric changed; new version/review required')
    return {'version': RUBRIC_VERSION, 'semantic_sha256': semantic, 'file_sha256': sha(RUBRIC_PATH)}


def load_manifest(path):
    path = no_links(path); root = path.parent; manifest = read(path)
    need(type(manifest) is dict and set(manifest) == {'format', 'protocol_revision', 'study_id', 'mode', 'attempts'}, 'Manifest schema')
    need(manifest['format'] == 'f4-evaluation-export-v1' and type(manifest['protocol_revision']) is str and bool(manifest['protocol_revision']), 'Manifest version')
    need(manifest['mode'] in ('synthetic', 'live') and type(manifest['study_id']) is str and bool(manifest['study_id']), 'Study identity/mode')
    attempts = manifest['attempts']; need(type(attempts) is list and len(attempts) == 12, 'All twelve planned attempts required')
    expected = set(itertools.product(TASKS, (1, 2), ARMS)); cells = set(); ids = set(); rows = []
    for item in attempts:
        need(type(item) is dict and set(item) == {'id', 'task', 'rep', 'arm', 'receipt'}, 'Attempt schema')
        need(type(item['id']) is str and bool(item['id']) and item['id'] not in ids, 'Duplicate/invalid ID')
        need(type(item['rep']) is int and type(item['task']) is str and type(item['arm']) is str, 'Cell types')
        cell = item['task'], item['rep'], item['arm']; need(cell in expected and cell not in cells, 'Duplicate/unknown cell')
        ids.add(item['id']); cells.add(cell)
        receipt_path = bound_file(root, item['receipt']); r = read(receipt_path)
        keys = {'study_id', 'mode', 'id', 'task', 'rep', 'arm', 'state', 'protocol_compliant', 'usage_known', 'host_cost_usd', 'settled_cost_usd', 'final', 'precheck'}
        need(type(r) is dict and set(r) == keys, 'Receipt schema')
        need(r['study_id'] == manifest['study_id'] and r['mode'] == manifest['mode'], 'Receipt study/mode mismatch')
        need(all(r[k] == item[k] and type(r[k]) is type(item[k]) for k in ('id', 'task', 'rep', 'arm')), 'Receipt identity mismatch')
        need(r['state'] in ('completed', 'stopped', 'not-started') and type(r['protocol_compliant']) is bool and type(r['usage_known']) is bool, 'Receipt status')
        host, settled = money(r['host_cost_usd']), money(r['settled_cost_usd'])
        if r['usage_known']:
            need(host is not None and settled is not None, 'Known usage requires both cost instruments')
        if r['state'] == 'not-started':
            need(host == settled == 0 and r['usage_known'] and not r['protocol_compliant'], 'Not-started accounting')
        if r['state'] == 'completed':
            need(r['protocol_compliant'], 'Completed but noncompliant receipt')
        else:
            need(not r['protocol_compliant'], 'Incomplete receipt marked compliant')
        row = dict(r, receipt_sha256=sha(receipt_path), valid_answer=r['state'] == 'completed')
        for phase in ('final', 'precheck'):
            required = r['state'] == 'completed' and (phase == 'final' or r['arm'] != 'direct')
            need(not required or r[phase] is not None, 'Missing completed answer')
            need(not (r['arm'] == 'direct' and phase == 'precheck' and r[phase] is not None), 'Direct must not have PRECHECK')
            if r[phase] is not None:
                answer_path = bound_file(root, r[phase]); text = answer_path.read_text(encoding='utf-8-sig')
                need(bool(text.strip()), 'Empty answer')
                row[phase + '_text'] = text
        rows.append(row)
    need(cells == expected, 'Incomplete factorial plan')
    return manifest, rows


def prepare(manifest_path, output):
    manifest_path = no_links(manifest_path); output = no_links(output)
    manifest, rows = load_manifest(manifest_path); rubric = rubric_binding()
    need(not output.exists(), 'Output exists; never overwrite or resume partial outputs')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.f4-staging-', dir=output.parent) as temp:
        stage = Path(temp) / 'packet'; stage.mkdir(); mapping = []; public_hashes = {}
        for phase in ('final', 'precheck'):
            candidates = [r for r in rows if r['valid_answer'] and phase + '_text' in r]
            secrets.SystemRandom().shuffle(candidates)
            answers = []; templates = []
            for r in candidates:
                blind_id = secrets.token_hex(16)
                answers.append({'id': blind_id, 'task': r['task'], 'answer': r[phase + '_text']})
                templates.append({'id': blind_id, 'task': r['task'], 'components': {f: None for f in FIELDS}, 'critical_errors': [], 'rationale': ''})
                mapping.append({'id': blind_id, 'session': r['id'], 'phase': phase, 'task': r['task'], 'receipt_sha256': r['receipt_sha256']})
            folder = stage / ('public-' + phase); folder.mkdir()
            for name, value in [('answers.json', answers), ('scores-template.json', templates)]:
                file = folder / name; write_new(file, value); public_hashes[file.relative_to(stage).as_posix()] = sha(file)
        private = stage / 'PRIVATE'; private.mkdir()
        write_new(private / 'mapping.json', mapping)
        write_new(private / 'provenance.json', {'manifest_path': str(manifest_path), 'manifest_sha256': sha(manifest_path), 'protocol_revision': manifest['protocol_revision'], 'study_id': manifest['study_id'], 'mode': manifest['mode'], 'rubric': rubric, 'mapping_sha256': sha(private / 'mapping.json'), 'public_hashes': public_hashes})
        # Rename publishes the complete directory, never a partial set of output files.
        need(not output.exists(), 'Output appeared during preparation')
        stage.rename(output)
    return {'final_packets': sum(x['phase'] == 'final' for x in mapping), 'precheck_packets': sum(x['phase'] == 'precheck' for x in mapping), 'planned_attempts': 12}


def provenance(output):
    output = no_links(output); p = read(output / 'PRIVATE/provenance.json')
    manifest_path = no_links(p['manifest_path']); need(sha(manifest_path) == p['manifest_sha256'], 'Manifest changed')
    manifest, rows = load_manifest(manifest_path)
    for key in ('mode', 'study_id', 'protocol_revision'):
        need(p.get(key) == manifest[key], 'Provenance identity changed: ' + key)
    need(p.get('rubric') == rubric_binding(), 'Rubric binding changed')
    need(sha(output / 'PRIVATE/mapping.json') == p['mapping_sha256'], 'Mapping changed')
    for name, digest in p['public_hashes'].items():
        bound_file(output, {'path': name, 'sha256': digest})
    return p, rows, read(output / 'PRIVATE/mapping.json')


def validate_scores(scores, mapping):
    need(type(scores) is list, 'Scores must be list')
    expected = {x['id']: x for x in mapping}; result = {}
    need(len(expected) == len(mapping), 'Duplicate blind mapping')
    for score in scores:
        need(type(score) is dict and set(score) == {'id', 'task', 'components', 'critical_errors', 'rationale'}, 'Score schema')
        ident = score['id']; need(type(ident) is str and ident in expected and ident not in result, 'Unknown/duplicate score')
        need(score['task'] == expected[ident]['task'], 'Score task mismatch')
        c = score['components']; need(type(c) is dict and set(c) == set(FIELDS), 'Four rubric fields required')
        need(all(type(v) is int and 0 <= v <= 2 for v in c.values()), 'Scores must be integer 0/1/2')
        need(type(score['critical_errors']) is list and all(type(x) is str and bool(x.strip()) for x in score['critical_errors']), 'Critical error labels')
        need(type(score['rationale']) is str and bool(score['rationale'].strip()), 'Scoring rationale required')
        result[ident] = dict(score, quality_0_to_8=sum(c.values()))
    need(set(result) == set(expected), 'All final and diagnostic PRECHECK scores required')
    return result


def freeze(output, scores, adjudication):
    output = no_links(output); scores = no_links(scores); adjudication = no_links(adjudication)
    p, _, mapping = provenance(output)
    validate_scores(read(scores), mapping)
    log = read(adjudication); need(type(log) is list, 'Adjudication log must be list (empty allowed)')
    write_new(output / 'scores-freeze.json', {'scores_path': str(scores), 'scores_sha256': sha(scores), 'adjudication_path': str(adjudication), 'adjudication_sha256': sha(adjudication), 'provenance_sha256': sha(output / 'PRIVATE/provenance.json')})


def aggregate(output):
    output = no_links(output); frozen = read(output / 'scores-freeze.json')
    need(sha(output / 'PRIVATE/provenance.json') == frozen['provenance_sha256'], 'Frozen provenance changed')
    for name in ('scores', 'adjudication'):
        need(sha(no_links(frozen[name + '_path'])) == frozen[name + '_sha256'], 'Frozen ' + name + ' changed')
    p, rows, mapping = provenance(output); scores = validate_scores(read(frozen['scores_path']), mapping)
    by_id = {r['id']: r for r in rows}; pairs = set()
    for m in mapping:
        need(m['session'] in by_id and m['phase'] in ('final', 'precheck'), 'Invalid private mapping')
        r = by_id[m['session']]; key = (r['id'], m['phase'])
        need(key not in pairs and r['receipt_sha256'] == m['receipt_sha256'] and r['task'] == m['task'] and r['valid_answer'], 'Mapping provenance mismatch')
        pairs.add(key); r[m['phase'] + '_score'] = scores[m['id']]
    arms = {}
    for arm in ARMS:
        rr = [r for r in rows if r['arm'] == arm]; completed = [r for r in rr if r['valid_answer']]
        for r in completed:
            need('final_score' in r and (arm == 'direct' or 'precheck_score' in r), 'Missing phase score')
        valid = [r for r in completed if not r['final_score']['critical_errors']]
        summary = {'planned': len(rr), 'completed': len(completed), 'valid_final_answers': len(valid),
                   'final_quality_mean': sum(r['final_score']['quality_0_to_8'] for r in completed) / len(completed) if completed else None,
                   'per_task': {t: [r['final_score']['quality_0_to_8'] for r in completed if r['task'] == t] for t in TASKS},
                   'critical_errors': sum(len(r['final_score']['critical_errors']) for r in completed),
                   'precheck_deltas_descriptive_only': [r['final_score']['quality_0_to_8'] - r['precheck_score']['quality_0_to_8'] for r in completed if 'precheck_score' in r]}
        for field in ('host_cost_usd', 'settled_cost_usd'):
            values = [money(r[field]) for r in rr]; unknown = any(not r['usage_known'] or v is None for r, v in zip(rr, values))
            total = None if unknown else sum(values, Decimal(0))
            summary[field] = {'total_all_attempts': str(total) if total is not None else None,
                              'known_lower_bound': str(sum((v for v in values if v is not None), Decimal(0))),
                              'per_valid_answer': str(total / len(valid)) if total is not None and valid else None}
        arms[arm] = summary
    result = {'protocol_revision': p['protocol_revision'], 'study_id': p['study_id'], 'mode': p['mode'], 'synthetic_not_measurement': p['mode'] == 'synthetic', 'rubric': p['rubric'], 'arms': arms, 'scores_freeze_sha256': sha(output / 'scores-freeze.json'),
              'limits': ['Synthetic small diagnostic, not general effectiveness evidence.', 'Independent PRECHECK baselines; deltas are not causal effects.', 'Valid means completed compliant receipt and no scored critical errors, not perfect quality.', 'Host estimates and ledger settlement are separate; neither is a verified provider invoice.', 'Receipt protocol attestation is supplied by the execution controller, not proven by this evaluator.', 'Blindness depends on evaluator isolation; the owner of this private directory can inspect mappings.']}
    write_new(output / 'aggregate-results.json', result)
    return result


def main():
    p = argparse.ArgumentParser(description=__doc__); commands = p.add_subparsers(dest='command', required=True)
    create = commands.add_parser('prepare'); create.add_argument('--manifest', type=Path, required=True); create.add_argument('--output', type=Path, required=True)
    lock = commands.add_parser('freeze'); lock.add_argument('--output', type=Path, required=True); lock.add_argument('--scores', type=Path, required=True); lock.add_argument('--adjudication', type=Path, required=True)
    finish = commands.add_parser('aggregate'); finish.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    if a.command == 'prepare': result = prepare(a.manifest, a.output)
    elif a.command == 'freeze': freeze(a.output, a.scores, a.adjudication); result = {'scores_frozen': True}
    else: result = aggregate(a.output)
    print(json.dumps({'success': True, 'paid_execution': False, 'result': result}, ensure_ascii=False))


if __name__ == '__main__':
    main()
