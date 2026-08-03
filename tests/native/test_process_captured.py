from src.native.model import CapturedMessage, Segment
from src.utils.message_processor import MessageProcessor


class FakeSettings:
    def __init__(self, **overrides):
        self.cooldown = 0
        self.blacklist = []
        self.whitelist = []
        self.important_persons = []
        self.important_keywords = []
        self.someone_at_me = True
        self.calling_enabled = False
        self.calling_keyword = ""
        self.calling_duration = 30
        self.duration_everyone = 5
        self.duration_important = 15
        self._data = {"User_QQ": "10001"}
        for k, v in overrides.items():
            setattr(self, k, v)

    def get(self, key, default=None):
        return self._data.get(key, default)


def _make_processor(settings):
    mp = MessageProcessor.__new__(MessageProcessor)
    mp.settings = settings
    mp.seen = {}
    mp.active_toasts = set()
    mp.last_file_mtime = {}
    return mp


def _group_msg(text="明天带作业", sender_name="张三", segments=None, seq=1):
    return CapturedMessage(
        scene="group",
        peer_id="123",
        peer_name="高三2班",
        sender_id="1001",
        sender_name=sender_name,
        segments=segments if segments is not None else [Segment(type="text", text=text)],
        raw_seq=seq,
    )


def test_basic_group_text_builds_notify_data():
    mp = _make_processor(FakeSettings())
    data = mp.process_captured(_group_msg())
    assert data is not None
    assert "高三2班" in data["Sender"] and "张三" in data["Sender"]
    assert data["Message"] == "明天带作业"
    assert data["Priority"] == 1  # 非重要


def test_important_person_raises_priority():
    mp = _make_processor(FakeSettings(important_persons=["张三"]))
    data = mp.process_captured(_group_msg(sender_name="张三"))
    assert data is not None and data["Priority"] == 0


def test_blacklist_filters_out():
    mp = _make_processor(FakeSettings(blacklist=["广告"]))
    assert mp.process_captured(_group_msg(text="这是广告")) is None


def test_whitelist_requires_keyword():
    mp = _make_processor(FakeSettings(whitelist=["作业"]))
    assert mp.process_captured(_group_msg(text="闲聊")) is None
    assert mp.process_captured(_group_msg(text="交作业", seq=2)) is not None


def test_dedup_same_message_second_time_none():
    mp = _make_processor(FakeSettings())
    first = mp.process_captured(_group_msg(seq=7))
    second = mp.process_captured(_group_msg(seq=7))
    assert first is not None and second is None


def test_image_path_sets_pic_path():
    mp = _make_processor(FakeSettings())
    msg = _group_msg(segments=[Segment(type="image", url="http://x")])
    data = mp.process_captured(msg, image_path="/tmp/a.jpg")
    assert data is not None and data["Pic_Path"] == "/tmp/a.jpg"


def test_at_me_raises_priority():
    mp = _make_processor(FakeSettings())
    msg = _group_msg(segments=[Segment(type="at", text="我", target_id="10001")])
    data = mp.process_captured(msg)
    assert data is not None and data["Priority"] == 0
