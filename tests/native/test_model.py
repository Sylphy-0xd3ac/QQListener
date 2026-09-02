from src.native.model import CapturedMessage, Segment, message_text, quoted_message


def test_message_text_renders_segments():
    msg = CapturedMessage(
        scene="group",
        peer_id="123",
        peer_name="高三2班",
        sender_id="1001",
        sender_name="张三",
        segments=[
            Segment(type="text", text="明天带这个"),
            Segment(type="image", url="http://x"),
            Segment(type="file", name="作业.docx"),
            Segment(type="at", text="李四", target_id="1002"),
        ],
        raw_seq=7,
    )
    assert message_text(msg) == "明天带这个[图片][文件] 作业.docx[@李四]"


def test_wire_style_mention_text_is_not_double_prefixed():
    """线上的 @ 文本自带 "@" 和尾随空格。"""
    msg = CapturedMessage(
        scene="group",
        peer_id="123",
        peer_name="高三2班",
        sender_id="1001",
        sender_name="张三",
        segments=[Segment(type="at", text="@李四 ", target_id="1002")],
        raw_seq=1,
    )
    assert message_text(msg) == "[@李四]"


def test_reply_segment_is_excluded_from_the_body_and_exposed_separately():
    quoted = [Segment(type="text", text="昨天的作业交了吗")]
    msg = CapturedMessage(
        scene="group",
        peer_id="123",
        peer_name="高三2班",
        sender_id="1001",
        sender_name="张三",
        segments=[
            Segment(
                type="reply",
                extra={
                    "seq": 42,
                    "time": 1700000000,
                    "sender_id": "1002",
                    "sender_name": "李四",
                    "segments": quoted,
                },
            ),
            Segment(type="text", text="交了"),
        ],
        raw_seq=7,
    )

    assert message_text(msg) == "交了"
    quote = quoted_message(msg)
    assert quote is not None
    assert quote.sender_id == "1002"
    assert quote.sender_name == "李四"
    assert quote.seq == 42
    assert quote.time == 1700000000
    assert quote.segments == quoted


def test_quoted_message_is_none_without_a_reply():
    msg = CapturedMessage(
        scene="c2c",
        peer_id="1",
        peer_name="",
        sender_id="1",
        sender_name="",
        segments=[Segment(type="text", text="你好")],
        raw_seq=1,
    )
    assert quoted_message(msg) is None
