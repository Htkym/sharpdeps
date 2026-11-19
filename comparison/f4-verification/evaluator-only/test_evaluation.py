import itertools
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import evaluation as e


class EvaluationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name); self.manifest = self.root / 'manifest.json'; self.output = self.root / 'blind'
        self.rows = []
        for n, (task, rep, arm) in enumerate(itertools.product(e.TASKS, (1, 2), e.ARMS)):
            r = dict(study_id='test', mode='synthetic', id=f's{n}', task=task, rep=rep, arm=arm, state='completed', protocol_compliant=True, usage_known=True, host_cost_usd='0.1', settled_cost_usd='0.2', final=None, precheck=None)
            for phase in ('final', 'precheck'):
                if arm == 'direct' and phase == 'precheck': continue
                path = self.root / f's{n}-{phase}.txt'; path.write_text('source-based answer', encoding='utf-8'); r[phase] = self.ref(path)
            path = self.root / f's{n}.json'; self.save(path, r)
            self.rows.append({**{k: r[k] for k in ('id', 'task', 'rep', 'arm')}, 'receipt': self.ref(path)})
        self.refresh()

    def save(self, path, obj): path.write_text(json.dumps(obj), encoding='utf-8')
    def ref(self, path): return {'path': path.relative_to(self.root).as_posix(), 'sha256': e.sha(path)}
    def refresh(self): self.save(self.manifest, dict(format='f4-evaluation-export-v1', protocol_revision='test-v1', study_id='test', mode='synthetic', attempts=self.rows))
    def change_receipt(self, n, **kwargs):
        path = self.root / self.rows[n]['receipt']['path']; r = e.read(path); r.update(kwargs); self.save(path, r); self.rows[n]['receipt'] = self.ref(path); self.refresh()
    def score(self):
        scores = []
        for phase in ('final', 'precheck'):
            for s in e.read(self.output / f'public-{phase}/scores-template.json'):
                s.update(components={f: 2 for f in e.FIELDS}, rationale='All required propositions supported.'); scores.append(s)
        p = self.root / 'scores.json'; self.save(p, scores)
        log = self.root / 'adjudication.json'; self.save(log, [])
        return p, log

    def test_end_to_end(self):
        result = e.prepare(self.manifest, self.output); self.assertEqual((result['final_packets'], result['precheck_packets']), (12, 8))
        for phase in ('final', 'precheck'):
            self.assertTrue(all(set(x) == {'id', 'task', 'answer'} for x in e.read(self.output / f'public-{phase}/answers.json')))
        e.freeze(self.output, *self.score()); result = e.aggregate(self.output)
        self.assertEqual(result['mode'], 'synthetic'); self.assertTrue(result['synthetic_not_measurement'])
        for arm in e.ARMS: self.assertEqual(result['arms'][arm]['final_quality_mean'], 8)
        self.assertEqual(result['arms']['direct']['precheck_deltas_descriptive_only'], [])

    def test_partial_failure_cost_retained(self):
        self.change_receipt(0, state='stopped', protocol_compliant=False, final=None, host_cost_usd='0.5')
        result = e.prepare(self.manifest, self.output); self.assertEqual(result['final_packets'], 11)
        e.freeze(self.output, *self.score()); r = e.aggregate(self.output)['arms']['direct']; self.assertEqual(r['host_cost_usd']['total_all_attempts'], '0.8'); self.assertEqual(r['completed'], 3)

    def test_unknown_usage_not_zero(self):
        self.change_receipt(0, usage_known=False, host_cost_usd=None, settled_cost_usd=None)
        e.prepare(self.manifest, self.output); e.freeze(self.output, *self.score())
        self.assertIsNone(e.aggregate(self.output)['arms']['direct']['host_cost_usd']['total_all_attempts'])

    def test_mismatched_mode(self):
        self.change_receipt(0, mode='live')
        with self.assertRaises(ValueError): e.prepare(self.manifest, self.output)
        self.assertFalse(self.output.exists())

    def test_missing_cell(self):
        self.rows.pop(); self.refresh()
        with self.assertRaises(ValueError): e.prepare(self.manifest, self.output)

    def test_receipt_hash_tamper(self):
        (self.root / 's0.json').write_text('{}')
        with self.assertRaises(ValueError): e.prepare(self.manifest, self.output)

    def test_answer_hash_tamper(self):
        (self.root / 's0-final.txt').write_text('changed')
        with self.assertRaises(ValueError): e.prepare(self.manifest, self.output)

    def test_path_escape(self):
        self.rows[0]['receipt']['path'] = '../outside.json'; self.refresh()
        with self.assertRaises(ValueError): e.prepare(self.manifest, self.output)

    def test_no_overwrite(self):
        e.prepare(self.manifest, self.output)
        with self.assertRaises(ValueError): e.prepare(self.manifest, self.output)
        scores, log = self.score(); e.freeze(self.output, scores, log)
        with self.assertRaises(FileExistsError): e.freeze(self.output, scores, log)
        e.aggregate(self.output)
        with self.assertRaises(FileExistsError): e.aggregate(self.output)

    def test_score_tamper_after_freeze(self):
        e.prepare(self.manifest, self.output); scores, log = self.score(); e.freeze(self.output, scores, log); scores.write_text('[]')
        with self.assertRaises(ValueError): e.aggregate(self.output)

    def test_bad_score_and_missing_score(self):
        e.prepare(self.manifest, self.output); scores, log = self.score(); data = e.read(scores); data[0]['components']['evidence'] = True; self.save(scores, data)
        with self.assertRaises(ValueError): e.freeze(self.output, scores, log)
        data.pop(0); self.save(scores, data)
        with self.assertRaises(ValueError): e.freeze(self.output, scores, log)

    def test_direct_precheck_rejected(self):
        r = e.read(self.root / 's0.json'); self.change_receipt(0, precheck=r['final'])
        with self.assertRaises(ValueError): e.prepare(self.manifest, self.output)


    def assert_provenance_rejected(self, key, value):
        e.prepare(self.manifest, self.output); p = self.output / 'PRIVATE/provenance.json'; data = e.read(p); data[key] = value; self.save(p, data)
        with self.assertRaisesRegex(ValueError, 'Provenance identity changed'): e.freeze(self.output, *self.score())
        self.assertFalse((self.output / 'scores-freeze.json').exists())

    def test_synthetic_cannot_be_relabelled_live(self): self.assert_provenance_rejected('mode', 'live')
    def test_study_cannot_be_relabelled(self): self.assert_provenance_rejected('study_id', 'other')
    def test_protocol_cannot_be_relabelled(self): self.assert_provenance_rejected('protocol_revision', 'other')

    def test_rubric_binding_cannot_change(self):
        e.prepare(self.manifest, self.output); p = self.output / 'PRIVATE/provenance.json'; data = e.read(p); data['rubric']['version'] = 'other'; self.save(p, data)
        with self.assertRaisesRegex(ValueError, 'Rubric binding'): e.freeze(self.output, *self.score())

    def test_rubric_source_must_match_reviewed_version(self):
        p = self.root / 'changed-rubric.json'; data = e.read(e.RUBRIC_PATH); data['quality_max'] = 12; self.save(p, data)
        with patch.object(e, 'RUBRIC_PATH', p), self.assertRaisesRegex(ValueError, 'Reviewed rubric changed'): e.prepare(self.manifest, self.output)


if __name__ == '__main__': unittest.main()
