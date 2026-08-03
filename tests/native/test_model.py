from src.native.model import CapturedMessage, Segment, message_text


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
