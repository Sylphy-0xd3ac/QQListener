"""暂停期间的积压队列与恢复时的摘要合并。"""

import pytest

from src.core.core_controller import CoreState
from src.core.pending_queue import (
    clear_pending,
    drain_pending,
    pending_count,
    push_pending,
)
from src.core.worker import NotificationWorker
from src.utils.message_processor import build_digest_payload, digest_entries


@pytest.fixture(autouse=True)
def _clean_queue():
    clear_pending()
    yield
    clear_pending()


def _payload(sender: str, text: str, **overrides) -> dict:
    data = {
        "Sender": sender,
        "Sender_Detail": f"发送者QQ号: {sender}",
        "Message": text,
        "Segments": [{"type": "text", "text": text}],
        "Quote": None,
        "Messages": [
            {
                "sender": sender,
                "detail": f"发送者QQ号: {sender}",
                "text": text,
                "segments": [{"type": "text", "text": text}],
                "quote": None,
            }
        ],
        "Reply": {"pid": 1, "scene": "group", "peer_id": sender},
        "Duration": 5000,
        "Priority": 1,
        "Calling": False,
    }
    data.update(overrides)
    return data


# ---------- 队列 ----------


def test_push_and_drain_preserves_order():
    push_pending(_payload("1001", "第一条"))
    push_pending(_payload("1002", "第二条"))
    assert pending_count() == 2

    items, dropped = drain_pending()

    assert [p["Message"] for p in items] == ["第一条", "第二条"]
    assert dropped == 0
    assert pending_count() == 0


def test_queue_is_bounded_and_drops_the_oldest():
    for i in range(5):
        push_pending(_payload(str(i), f"第{i}条"), max_items=3)

    items, dropped = drain_pending()

    assert [p["Message"] for p in items] == ["第2条", "第3条", "第4条"]
    assert dropped == 2


def test_empty_payload_is_ignored():
    push_pending({})
    assert pending_count() == 0


def test_clear_resets_both_items_and_drop_count():
    for i in range(4):
        push_pending(_payload(str(i), str(i)), max_items=2)
    clear_pending()

    assert drain_pending() == ([], 0)


# ---------- worker 出口 ----------


class _Worker:
    """只借 _dispatch 的逻辑，不起真线程。"""

    _dispatch = NotificationWorker._dispatch

    def __init__(self, state, *, enabled=True, max_items=50):
        self.emitted: list[dict] = []
        self.notification_ready = type("_S", (), {"emit": lambda _s, d: self.emitted.append(d)})()
        self.settings = type(
            "_Cfg", (), {"pause_queue_enabled": enabled, "pause_queue_max": max_items}
        )()
        self._state = state


def _with_state(monkeypatch, state):
    monkeypatch.setattr("src.core.worker.get_core_state", lambda: state)


def test_running_state_emits_immediately(monkeypatch):
    _with_state(monkeypatch, CoreState.RUNNING)
    worker = _Worker(CoreState.RUNNING)

    worker._dispatch(_payload("1001", "在线"))

    assert [d["Message"] for d in worker.emitted] == ["在线"]
    assert pending_count() == 0


def test_paused_state_queues_instead_of_emitting(monkeypatch):
    _with_state(monkeypatch, CoreState.PAUSED)
    worker = _Worker(CoreState.PAUSED)

    worker._dispatch(_payload("1001", "暂停中"))

    assert worker.emitted == []
    assert pending_count() == 1


def test_paused_with_queue_disabled_drops_the_message(monkeypatch):
    _with_state(monkeypatch, CoreState.PAUSED)
    worker = _Worker(CoreState.PAUSED, enabled=False)

    worker._dispatch(_payload("1001", "丢掉"))

    assert worker.emitted == []
    assert pending_count() == 0


# ---------- 摘要 ----------


def test_digest_entries_falls_back_to_the_legacy_single_message_shape():
    legacy = {
        "Sender": "张三",
        "Sender_Detail": "发送者QQ号: 1001",
        "Message": "你好",
        "Segments": [{"type": "text", "text": "你好"}],
        "Quote": None,
    }

    entries = digest_entries(legacy)

    assert len(entries) == 1
    assert entries[0]["sender"] == "张三"
    assert entries[0]["segments"] == [{"type": "text", "text": "你好"}]


def test_single_queued_message_is_shown_as_is():
    payload = _payload("1001", "只有一条")

    assert build_digest_payload([payload]) is payload


def test_digest_merges_entries_and_takes_the_strongest_attributes():
    first = _payload("1001", "第一条", Duration=5000, Priority=1)
    second = _payload("1002", "第二条", Duration=15000, Priority=0, Calling=True)

    digest = build_digest_payload([first, second])

    assert digest["Digest"] is True
    assert digest["Sender"] == "暂停期间的 2 条消息"
    assert [entry["text"] for entry in digest["Messages"]] == ["第一条", "第二条"]
    assert digest["Duration"] == 15000
    assert digest["Priority"] == 0  # 有重要的就按重要算
    assert digest["Calling"] is True
    # 摘要可能跨会话，回复目标取最后一条
    assert digest["Reply"] == second["Reply"]


def test_digest_keeps_each_message_quote_intact():
    quote = {"sender": "王五", "detail": "", "text": "原文", "segments": []}
    quoted = _payload("1002", "回你了")
    quoted["Messages"][0]["quote"] = quote

    digest = build_digest_payload([_payload("1001", "普通一条"), quoted])

    assert digest["Messages"][0]["quote"] is None
    assert digest["Messages"][1]["quote"] == quote


def test_digest_reports_messages_dropped_by_the_cap():
    digest = build_digest_payload([_payload("1001", "a"), _payload("1002", "b")], dropped=7)

    assert "更早的 7 条已丢弃" in digest["Sender"]
    assert "超出上限" in digest["TTS_Text"]


def test_digest_tts_is_bounded_but_reports_the_total():
    payloads = [_payload(str(i), f"消息{i}") for i in range(6)]

    digest = build_digest_payload(payloads)

    assert digest["TTS_Text"].startswith("暂停期间收到6条消息。")
    assert "消息0" in digest["TTS_Text"] and "消息5" not in digest["TTS_Text"]
    assert "另有3条未念" in digest["TTS_Text"]


def test_empty_input_yields_no_digest():
    assert build_digest_payload([]) is None
