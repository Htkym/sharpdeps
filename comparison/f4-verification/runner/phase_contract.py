"""Offline F4 guard. No provider, network, execution or approval acquisition.

Inputs are trusted controller exports, NOT self-authorizing user-uploaded JSON.
The caller must authenticate fresh owner approval and set authorization_verified
only after that check. Exact stack hashing is the primary contamination guard;
keyword rejection is supplementary, not a semantic proof of neutrality.

Manifest: run_id (f4-*), manifest_sha256 is computed externally with digest();
neutral_stack {system: str, tools: list, guide: str}, neutral_reviewed: true,
service_tier: default, budget_usd: decimal string, session_ids: unique list,
session_sha256: mapping each session_id to digest of its frozen session spec.
Approval binds run_id, manifest_sha256, authorization_verified, approved_at,
expires_at, budget_usd, approval_id. Ledger: run_id, approval_id, entries: [].
Session: session_id, arm, task_id, repetition, initial_messages, followup
(followup is absent/direct or {role:user,content:str} for recheck arms).
Export: same identities, phase, service_tier, stack, messages (full transcript),
accepted_tools and delivered_tools. Each tool record: call_id, request_id,
payload_sha256; delivery additionally accepted:true. Lists are phase-local,
not cumulative; old call IDs cannot be reused. Native adapters must convert
provider-specific transcripts to this canonical form without altering content
and must audit every native tool event. Both complete raw and evidence_-prefixed
six-tool stacks are accepted, never mixed; exact stack hash still must match. Every delivered record must join
a phase-local tool message {role:tool,tool_call_id,request_id,content:str}; hash
is SHA256 of content UTF-8 bytes. Empty lists require no tool messages.
"""
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
import hashlib
import json
import re

ALLOWED_TOOLS = frozenset(("find_symbols", "read_symbols", "relations", "source_read", "source_search", "source_list"))


class ContractError(ValueError):
    def __init__(self, code):
        self.code = code
        super().__init__(code)


def require(condition, code):
    if not condition:
        raise ContractError(code)


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"),
                                     ensure_ascii=False, allow_nan=False).encode()).hexdigest()


def timestamp(value):
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
        require(result.tzinfo is not None, "TIMESTAMP")
        return result
    except (TypeError, ValueError, AttributeError):
        raise ContractError("TIMESTAMP") from None


def money(value):
    try:
        require(isinstance(value, str), "MONEY")
        result = Decimal(value)
        require(result.is_finite() and result > 0, "MONEY")
        return result
    except (InvalidOperation, ValueError):
        raise ContractError("MONEY") from None


def validate_stack(manifest, observed_stack):
    expected = manifest.get("neutral_stack")
    require(manifest.get("neutral_reviewed") is True, "STACK_NOT_REVIEWED")
    require(isinstance(expected, dict) and set(expected) == {"system", "tools", "guide"}, "STACK_SHAPE")
    require(isinstance(expected["system"], str) and isinstance(expected["guide"], str)
            and isinstance(expected["tools"], list), "STACK_SHAPE")
    names = [item.get("name") if isinstance(item, dict) else None for item in expected["tools"]]
    require(len(names) == len(ALLOWED_TOOLS) and all(isinstance(x, str) for x in names)
            and (set(names) == ALLOWED_TOOLS or set(names) == {"evidence_" + x for x in ALLOWED_TOOLS}), "TOOL_SET")
    require(digest(expected) == digest(observed_stack), "STACK_CHANGED")
    text = json.dumps(expected, ensure_ascii=False).casefold()
    forbidden = ("routing-v5", "before submitting", "before submission", "exception checklist",
                 "exception-checklist", "final-check", "final check", "try/catch/finally",
                 "提出前", "例外経路を確認")
    require(not any(item in text for item in forbidden), "CHECKLIST_CONTAMINATION")


def validate_preflight(manifest, approval, ledger, now_utc):
    run = manifest.get("run_id", "")
    require(isinstance(run, str) and re.fullmatch(r"f4-[A-Za-z0-9-]+", run), "FRESH_RUN_REQUIRED")
    require(manifest.get("service_tier") == "default", "SERVICE_TIER")
    validate_stack(manifest, manifest.get("neutral_stack"))
    sessions = manifest.get("session_ids")
    require(isinstance(sessions, list) and sessions and all(isinstance(x, str) and x for x in sessions)
            and len(sessions) == len(set(sessions)), "SESSION_IDS")
    frozen = manifest.get("session_sha256")
    require(isinstance(frozen, dict) and set(frozen) == set(sessions)
            and all(isinstance(x, str) and re.fullmatch(r"[0-9a-f]{64}", x) for x in frozen.values()), "SESSION_FREEZE")
    require(approval.get("authorization_verified") is True, "FRESH_APPROVAL_REQUIRED")
    require(approval.get("run_id") == run and approval.get("manifest_sha256") == digest(manifest), "APPROVAL_BINDING")
    require(isinstance(approval.get("approval_id"), str) and approval["approval_id"], "APPROVAL_ID")
    now = timestamp(now_utc)
    start, end = timestamp(approval.get("approved_at")), timestamp(approval.get("expires_at"))
    require(start <= now < end and (now - start).total_seconds() <= 86400, "APPROVAL_EXPIRED")
    require(money(manifest.get("budget_usd")) <= money(approval.get("budget_usd")), "BUDGET_CAP")
    require(ledger.get("run_id") == run and ledger.get("approval_id") == approval["approval_id"], "LEDGER_BINDING")
    require(ledger.get("entries") == [], "FRESH_LEDGER_REQUIRED")


def tool_join(export, suffix):
    accepted, delivered = export.get("accepted_tools"), export.get("delivered_tools")
    require(isinstance(accepted, list) and isinstance(delivered, list), "DELIVERY_RECORDS")
    def index(records, delivery):
        result = {}
        for item in records:
            require(isinstance(item, dict), "DELIVERY_RECORDS")
            call, request, payload = (item.get(k) for k in ("call_id", "request_id", "payload_sha256"))
            require(isinstance(call, str) and call and isinstance(request, str) and request
                    and isinstance(payload, str) and re.fullmatch(r"[0-9a-f]{64}", payload), "DELIVERY_RECORDS")
            require(call not in result, "DUPLICATE_CALL")
            if delivery:
                require(item.get("accepted") is True, "DELIVERY_REJECTED")
            result[call] = (request, payload)
        return result
    left, right = index(accepted, False), index(delivered, True)
    require(left == right, "DELIVERY_JOIN")
    actual = {}
    for item in suffix:
        if item.get("role") != "tool":
            continue
        call, request, content = item.get("tool_call_id"), item.get("request_id"), item.get("content")
        require(isinstance(call, str) and call and isinstance(request, str) and request
                and isinstance(content, str), "TOOL_TRANSCRIPT")
        require(call not in actual, "DUPLICATE_TRANSCRIPT_CALL")
        actual[call] = (request, hashlib.sha256(content.encode("utf-8")).hexdigest())
    require(actual == right, "TRANSCRIPT_DELIVERY_JOIN")
    return len(left)


def validate_phase(manifest, session, phase_export, prior_export=None):
    require(manifest.get("service_tier") == phase_export.get("service_tier") == "default", "SERVICE_TIER")
    validate_stack(manifest, phase_export.get("stack"))
    require(session.get("session_id") in manifest.get("session_ids", []), "SESSION_UNKNOWN")
    require(manifest.get("session_sha256", {}).get(session["session_id"]) == digest(session), "SESSION_SPEC_CHANGED")
    for key in ("session_id", "arm", "task_id", "repetition"):
        require(key in session and phase_export.get(key) == session[key], "SESSION_IDENTITY")
    require(session.get("task_id") in ("F4-R", "F4-N") and type(session.get("repetition")) is int
            and session["repetition"] in (1, 2), "TASK_REPETITION")
    arm = session.get("arm")
    require(arm in ("direct", "generic", "exception_checklist"), "ARM")
    messages = phase_export.get("messages")
    require(isinstance(messages, list) and messages and all(isinstance(x, dict) for x in messages), "TRANSCRIPT")
    initial = session.get("initial_messages")
    require(isinstance(initial, list) and len(initial) == 1 and isinstance(initial[0], dict)
            and set(initial[0]) == {"role", "content"} and initial[0]["role"] == "user"
            and isinstance(initial[0]["content"], str) and initial[0]["content"].strip(), "INITIAL_MESSAGES")
    if arm == "direct":
        require("followup" not in session, "DIRECT_FOLLOWUP")
    else:
        followup = session.get("followup")
        require(isinstance(followup, dict) and set(followup) == {"role", "content"}
                and followup["role"] == "user" and isinstance(followup["content"], str)
                and followup["content"].strip(), "FOLLOWUP")
    if prior_export is None:
        require(phase_export.get("phase") == ("final" if arm == "direct" else "precheck"), "PHASE")
        prefix = initial
    else:
        require(arm != "direct" and phase_export.get("phase") == "final", "PHASE")
        validate_phase(manifest, session, prior_export)
        followup = session.get("followup")
        require(isinstance(followup, dict) and followup.get("role") == "user"
                and isinstance(followup.get("content"), str) and followup["content"], "FOLLOWUP")
        prefix = prior_export["messages"] + [followup]
    require(len(messages) > len(prefix) and messages[:len(prefix)] == prefix, "PREFIX_CHANGED")
    suffix = messages[len(prefix):]
    require(all(item.get("role") in ("assistant", "tool") for item in suffix), "UNEXPECTED_TURN")
    require(suffix[-1].get("role") == "assistant" and isinstance(suffix[-1].get("content"), str)
            and suffix[-1]["content"].strip(), "ANSWER_MISSING")
    count = tool_join(phase_export, suffix)
    if prior_export is not None:
        old = {item["call_id"] for item in prior_export["accepted_tools"]}
        require(not old.intersection(item["call_id"] for item in phase_export["accepted_tools"]), "CALL_REUSED")
    return {"phase": phase_export["phase"], "new_tool_calls": count,
            "transcript_sha256": digest(messages), "answer": suffix[-1]["content"]}
