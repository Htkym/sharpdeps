import copy
import unittest
from phase_contract import ALLOWED_TOOLS, ContractError, digest, validate_preflight, validate_phase, validate_stack


def fixture(arm="generic"):
    stack = {"system": "Answer using supported source evidence.", "guide": "Use typed tools or source reads.", "tools": [{"name": name, "schema": {}} for name in sorted(ALLOWED_TOOLS)]}
    manifest = {"run_id": "f4-offline-test", "neutral_stack": stack, "neutral_reviewed": True,
                "service_tier": "default", "budget_usd": "1.00", "session_ids": ["session-1"]}
    session = {"session_id": "session-1", "task_id": "F4-R", "repetition": 1, "arm": arm,
               "initial_messages": [{"role": "user", "content": "Explain the source."}],
               "followup": {"role": "user", "content": "Review the answer."}}
    if arm == "direct":
        session.pop("followup")
    manifest["session_sha256"] = {session["session_id"]: digest(session)}
    phase = {k: session[k] for k in ("session_id", "task_id", "repetition", "arm")}
    phase.update(phase="final" if arm == "direct" else "precheck", service_tier="default",
                 stack=copy.deepcopy(stack), messages=session["initial_messages"] + [{"role": "assistant", "content": "Initial answer"}],
                 accepted_tools=[], delivered_tools=[])
    final = copy.deepcopy(phase)
    final.update(phase="final", messages=copy.deepcopy(phase["messages"] + [session.get("followup", {"role": "user", "content": "unexpected"}), {"role": "assistant", "content": "Final answer"}]))
    approval = {"run_id": manifest["run_id"], "manifest_sha256": digest(manifest), "authorization_verified": True,
                "approved_at": "2026-11-18T00:00:00Z", "expires_at": "2026-11-19T00:00:00Z",
                "approval_id": "fresh-test-only", "budget_usd": "1.00"}
    ledger = {"run_id": manifest["run_id"], "approval_id": approval["approval_id"], "entries": []}
    return manifest, session, phase, final, approval, ledger


class PhaseContractTests(unittest.TestCase):
    def rejects(self, code, function, *args):
        with self.assertRaises(ContractError) as raised:
            function(*args)
        self.assertEqual(code, raised.exception.code)

    def test_three_arms_zero_new_calls_valid(self):
        for arm in ("direct", "generic", "exception_checklist"):
            m, s, p, f, a, l = fixture(arm)
            validate_preflight(m, a, l, "2026-11-18T01:00:00Z")
            self.assertEqual(0, validate_phase(m, s, p)["new_tool_calls"])
            if arm != "direct":
                self.assertEqual(0, validate_phase(m, s, f, p)["new_tool_calls"])
            else:
                self.rejects("PHASE", validate_phase, m, s, f, p)

    def test_prefix_and_session_integrity(self):
        for field in ("session_id", "task_id", "repetition", "arm"):
            m, s, p, f, _, _ = fixture()
            f[field] = "changed"
            self.rejects("SESSION_IDENTITY", validate_phase, m, s, f, p)
        m, s, p, f, _, _ = fixture()
        f["messages"][1]["content"] = "altered provisional"
        self.rejects("PREFIX_CHANGED", validate_phase, m, s, f, p)

    def test_followup_and_extra_turn_rejected(self):
        m, s, p, f, _, _ = fixture()
        f["messages"][2]["content"] = "wrong prompt"
        self.rejects("PREFIX_CHANGED", validate_phase, m, s, f, p)
        m, s, p, f, _, _ = fixture()
        f["messages"].insert(-1, {"role": "user", "content": "extra turn"})
        self.rejects("UNEXPECTED_TURN", validate_phase, m, s, f, p)

    def test_session_spec_is_frozen(self):
        m, s, p, f, _, _ = fixture()
        s["followup"]["content"] = "changed instruction"
        self.rejects("SESSION_SPEC_CHANGED", validate_phase, m, s, p)

    def test_delivery_conditional_join(self):
        m, s, p, _, _, _ = fixture("direct")
        record = {"call_id": "c1", "request_id": "r1", "payload_sha256": __import__("hashlib").sha256(b"evidence").hexdigest()}
        p["accepted_tools"] = [record]
        self.rejects("DELIVERY_JOIN", validate_phase, m, s, p)
        p["delivered_tools"] = [dict(record, accepted=True)]
        self.rejects("TRANSCRIPT_DELIVERY_JOIN", validate_phase, m, s, p)
        p["messages"].insert(-1, {"role": "tool", "tool_call_id": "c1", "request_id": "r1", "content": "evidence"})
        self.assertEqual(1, validate_phase(m, s, p)["new_tool_calls"])
        p["delivered_tools"][0]["payload_sha256"] = "b" * 64
        self.rejects("DELIVERY_JOIN", validate_phase, m, s, p)
        p["delivered_tools"][0]["accepted"] = False
        self.rejects("DELIVERY_REJECTED", validate_phase, m, s, p)
        p["accepted_tools"].append(record)
        self.rejects("DUPLICATE_CALL", validate_phase, m, s, p)

    def test_stack_contamination_and_schema_change(self):
        m, _, _, _, _, _ = fixture()
        changed = copy.deepcopy(m["neutral_stack"])
        changed["tools"].append({"name": "explore"})
        self.rejects("STACK_CHANGED", validate_stack, m, changed)
        m["neutral_stack"]["system"] += " routing-v5 before submitting"
        self.rejects("CHECKLIST_CONTAMINATION", validate_stack, m, m["neutral_stack"])

    def test_tool_set_no_explore_partial_or_mixed(self):
        for change in ("explore", "partial", "mixed"):
            m, _, _, _, _, _ = fixture()
            tools = m["neutral_stack"]["tools"]
            if change == "explore": tools[0]["name"] = "explore"
            elif change == "partial": tools.pop()
            else: tools[0]["name"] = "evidence_" + tools[0]["name"]
            self.rejects("TOOL_SET", validate_stack, m, m["neutral_stack"])
        m, _, _, _, _, _ = fixture()
        for tool in m["neutral_stack"]["tools"]: tool["name"] = "evidence_" + tool["name"]
        validate_stack(m, m["neutral_stack"])

    def test_invalid_frozen_protocol(self):
        for mutation, code in ((lambda s: s.update(task_id="other"), "TASK_REPETITION"),
                               (lambda s: s.update(repetition=True), "TASK_REPETITION"),
                               (lambda s: s.update(followup={"role": "user", "content": "extra"}), "DIRECT_FOLLOWUP"),
                               (lambda s: s["initial_messages"].append({"role": "user", "content": "extra"}), "INITIAL_MESSAGES")):
            m, s, p, _, _, _ = fixture("direct")
            mutation(s)
            for key in ("task_id", "repetition"): p[key] = s[key]
            m["session_sha256"][s["session_id"]] = digest(s)
            self.rejects(code, validate_phase, m, s, p)

    def test_tool_message_without_records_rejected(self):
        m, s, p, _, _, _ = fixture("direct")
        p["messages"].insert(-1, {"role": "tool", "tool_call_id": "c1", "request_id": "r1", "content": "evidence"})
        self.rejects("TRANSCRIPT_DELIVERY_JOIN", validate_phase, m, s, p)

    def test_previous_phase_call_id_cannot_recur(self):
        m, s, p, f, _, _ = fixture()
        record = {"call_id": "c1", "request_id": "r1", "payload_sha256": __import__("hashlib").sha256(b"evidence").hexdigest()}
        message = {"role": "tool", "tool_call_id": "c1", "request_id": "r1", "content": "evidence"}
        p["accepted_tools"] = [record]; p["delivered_tools"] = [dict(record, accepted=True)]
        p["messages"].insert(-1, message)
        f["messages"] = copy.deepcopy(p["messages"] + [s["followup"], message, {"role": "assistant", "content": "Final"}])
        f["accepted_tools"] = [record]; f["delivered_tools"] = [dict(record, accepted=True)]
        self.rejects("CALL_REUSED", validate_phase, m, s, f, p)

    def test_preflight_fail_closed(self):
        changes = [("authorization_verified", False, "FRESH_APPROVAL_REQUIRED"),
                   ("manifest_sha256", "bad", "APPROVAL_BINDING"),
                   ("budget_usd", "0.5", "BUDGET_CAP"),
                   ("expires_at", "2026-11-18T00:30:00Z", "APPROVAL_EXPIRED")]
        for key, value, code in changes:
            m, _, _, _, a, l = fixture()
            a[key] = value
            self.rejects(code, validate_preflight, m, a, l, "2026-11-18T01:00:00Z")
        m, _, _, _, a, l = fixture()
        l["entries"] = [{"cost": "0"}]
        self.rejects("FRESH_LEDGER_REQUIRED", validate_preflight, m, a, l, "2026-11-18T01:00:00Z")
        m, _, _, _, a, l = fixture()
        m["service_tier"] = "priority"
        self.rejects("SERVICE_TIER", validate_preflight, m, a, l, "2026-11-18T01:00:00Z")


if __name__ == "__main__":
    unittest.main()
