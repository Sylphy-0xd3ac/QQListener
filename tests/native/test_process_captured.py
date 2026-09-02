from src.native.model import CapturedMessage, Segment
from src.utils.message_processor import MessageProcessor


class FakeSettings:
    def __init__(self, **overrides):
        self.cooldown = 0
        self.blacklist_enabled = False
        self.whitelist_enabled = False
        self.blacklist_groups = []
        self.whitelist_groups = []
        self.blacklist_person_qqs = []
        self.whitelist_person_qqs = []
        self.important_person_qqs = []
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
    mp.last_file_mtime = {}
    return mp


def _group_msg(text="明天带作业", sender_name="张三", segments=None, seq=1, peer_id="123"):
    return CapturedMessage(
        scene="group",
        peer_id=peer_id,
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
    # 默认不摊开号码，只显示名字与群名
    assert data["Sender"] == "张三（高三2班）"
    assert data["Sender_Detail"] == "群号: 123　发送者QQ号: 1001"
    assert data["Message"] == "明天带作业"
    assert data["Segments"] == [
        {
            "type": "text",
            "text": "明天带作业",
            "url": "",
            "name": "",
            "md5": "",
            "target_id": "",
            "size": 0,
            "local_path": "",
        }
    ]
    assert data["Quote"] is None
    assert data["Priority"] == 1  # 非重要


def test_group_sender_title_priority_is_remark_then_card_then_nickname():
    mp = _make_processor(FakeSettings())
    msg = _group_msg(sender_name="推送名称")
    msg.sender_nickname = "基础昵称"
    msg.sender_group_card = "群昵称"
    msg.sender_remark = "好友备注"

    assert mp._sender_label(msg) == "好友备注（高三2班）"
    msg.sender_remark = ""
    assert mp._sender_label(msg) == "群昵称（高三2班）"
    msg.sender_group_card = ""
    assert mp._sender_label(msg) == "基础昵称（高三2班）"


def test_private_sender_title_priority_is_remark_then_nickname():
    mp = _make_processor(FakeSettings())
    msg = CapturedMessage(
        scene="c2c",
        peer_id="1001",
        peer_name="",
        sender_id="1001",
        sender_name="",
        sender_nickname="基础昵称",
        sender_remark="好友备注",
        segments=[Segment(type="text", text="你好")],
        raw_seq=1,
    )

    assert mp._sender_label(msg) == "好友备注"
    assert mp._sender_detail(msg) == "发送者QQ号: 1001"
    msg.sender_remark = ""
    assert mp._sender_label(msg) == "基础昵称"


def test_important_person_qq_raises_priority_by_exact_id():
    mp = _make_processor(FakeSettings(important_person_qqs=["1001"]))
    data = mp.process_captured(_group_msg(sender_name="张三"))
    assert data is not None and data["Priority"] == 0


def test_important_person_name_does_not_match_qq_rule():
    mp = _make_processor(FakeSettings(important_person_qqs=["张三"]))
    data = mp.process_captured(_group_msg(sender_name="张三"))
    assert data is not None and data["Priority"] == 1


def test_group_blacklist_filters_by_exact_group_number():
    mp = _make_processor(FakeSettings(blacklist_enabled=True, blacklist_groups=["123"]))
    assert mp.process_captured(_group_msg()) is None


def test_person_blacklist_filters_group_sender():
    mp = _make_processor(FakeSettings(blacklist_enabled=True, blacklist_person_qqs=["1001"]))
    assert mp.process_captured(_group_msg()) is None


def test_disabled_blacklist_does_not_filter():
    mp = _make_processor(FakeSettings(blacklist_person_qqs=["1001"]))
    assert mp.process_captured(_group_msg()) is not None


def test_group_whitelist_allows_group_or_sender_union():
    by_group = _make_processor(FakeSettings(whitelist_enabled=True, whitelist_groups=["123"]))
    by_person = _make_processor(FakeSettings(whitelist_enabled=True, whitelist_person_qqs=["1001"]))
    assert by_group.process_captured(_group_msg()) is not None
    assert by_person.process_captured(_group_msg()) is not None


def test_blacklist_takes_precedence_over_whitelist():
    mp = _make_processor(
        FakeSettings(
            whitelist_enabled=True,
            blacklist_enabled=True,
            whitelist_groups=["123"],
            blacklist_person_qqs=["1001"],
        )
    )
    assert mp.process_captured(_group_msg()) is None


def test_enabled_empty_whitelist_blocks_all():
    mp = _make_processor(FakeSettings(whitelist_enabled=True))
    assert mp.process_captured(_group_msg()) is None


def test_private_whitelist_matches_peer_qq():
    mp = _make_processor(FakeSettings(whitelist_enabled=True, whitelist_person_qqs=["2002"]))
    msg = CapturedMessage(
        scene="c2c",
        peer_id="2002",
        peer_name="",
        sender_id="2002",
        sender_name="好友",
        segments=[Segment(type="text", text="你好")],
        raw_seq=1,
    )
    assert mp.process_captured(msg) is not None


def test_filtered_message_is_not_marked_seen():
    settings = FakeSettings(whitelist_enabled=True)
    mp = _make_processor(settings)
    msg = _group_msg(seq=20)
    assert mp.process_captured(msg) is None
    settings.whitelist_enabled = False
    assert mp.process_captured(msg) is not None


def test_dedup_same_message_second_time_none():
    mp = _make_processor(FakeSettings())
    first = mp.process_captured(_group_msg(seq=7))
    second = mp.process_captured(_group_msg(seq=7))
    assert first is not None and second is None


def test_same_sequence_and_body_in_different_conversations_are_not_deduplicated():
    mp = _make_processor(FakeSettings())
    first = mp.process_captured(_group_msg(seq=7, peer_id="123"))
    second = mp.process_captured(_group_msg(seq=7, peer_id="456"))
    assert first is not None and second is not None


def test_image_path_sets_pic_path():
    mp = _make_processor(FakeSettings())
    msg = _group_msg(segments=[Segment(type="image", url="http://x")])
    data = mp.process_captured(msg, image_path="/tmp/a.jpg")
    assert data is not None and data["Pic_Path"] == "/tmp/a.jpg"


def test_file_segment_carries_name_url_and_icon():
    mp = _make_processor(FakeSettings())
    msg = _group_msg(
        segments=[Segment(type="file", name="群资料.pdf", url="https://file.qq.com/download?id=1")]
    )

    data = mp.process_captured(msg)

    assert data is not None
    segment = data["Segments"][0]
    assert segment["type"] == "file"
    assert segment["name"] == "群资料.pdf"
    assert segment["url"] == "https://file.qq.com/download?id=1"
    assert segment["icon_file"] == "asset/FileIcon/pdf.png"


def test_file_metadata_renders_when_url_resolution_failed():
    mp = _make_processor(FakeSettings())
    msg = _group_msg(segments=[Segment(type="file", name="群资料.pdf")])

    data = mp.process_captured(msg)

    assert data is not None
    segment = data["Segments"][0]
    assert segment["name"] == "群资料.pdf"
    assert segment["url"] == ""
    assert segment["icon_file"] == "asset/FileIcon/pdf.png"


def test_file_named_like_image_renders_as_image():
    mp = _make_processor(FakeSettings())
    msg = _group_msg(segments=[Segment(type="file", name="板书.png", url="https://x/1")])

    data = mp.process_captured(msg)

    assert data is not None
    assert data["Segments"][0]["type"] == "image"


def test_file_named_like_video_renders_as_video():
    mp = _make_processor(FakeSettings())
    msg = _group_msg(segments=[Segment(type="file", name="实验.mp4", url="https://x/1")])

    data = mp.process_captured(msg)

    assert data is not None
    assert data["Segments"][0]["type"] == "video"


def test_image_local_path_is_exposed_for_inline_rendering():
    mp = _make_processor(FakeSettings())
    msg = _group_msg(
        segments=[
            Segment(type="image", url="https://x/1", extra={"local_path": "/tmp/a.png", "size": 12})
        ]
    )

    data = mp.process_captured(msg)

    assert data is not None
    assert data["Segments"][0]["local_path"] == "/tmp/a.png"
    assert data["Segments"][0]["size"] == 12


def test_quoted_message_is_split_out_of_the_body():
    mp = _make_processor(FakeSettings())
    reply = Segment(
        type="reply",
        extra={
            "seq": 42,
            "sender_id": "1002",
            "sender_name": "李四",
            "segments": [Segment(type="text", text="昨天的作业交了吗")],
        },
    )
    msg = _group_msg(segments=[reply, Segment(type="text", text="交了")])

    data = mp.process_captured(msg)

    assert data is not None
    assert data["Message"] == "交了"
    assert data["Quote"]["sender"] == "李四"
    assert data["Quote"]["text"] == "昨天的作业交了吗"
    assert data["Quote"]["segments"][0]["text"] == "昨天的作业交了吗"
    assert [seg["type"] for seg in data["Segments"]] == ["text"]


def test_reply_route_is_passed_through():
    mp = _make_processor(FakeSettings())
    route = {"pid": 42, "scene": "group", "peer_id": "123", "quote_seq": 7}

    data = mp.process_captured(_group_msg(), reply_route=route)

    assert data is not None and data["Reply"] == route


def test_at_me_raises_priority():
    mp = _make_processor(FakeSettings())
    msg = _group_msg(segments=[Segment(type="at", text="我", target_id="10001")])
    data = mp.process_captured(msg)
    assert data is not None and data["Priority"] == 0
