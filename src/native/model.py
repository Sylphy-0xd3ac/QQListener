from dataclasses import dataclass, field


@dataclass
class Segment:
    type: str
    text: str = ""
    url: str = ""
    name: str = ""
    md5: str = ""
    target_id: str = ""
    extra: dict = field(default_factory=dict)


@dataclass
class CapturedMessage:
    scene: str
    peer_id: str
    peer_name: str
    sender_id: str
    sender_name: str
    segments: list[Segment]
    raw_seq: int


def message_text(msg: "CapturedMessage") -> str:
    parts: list[str] = []
    for seg in msg.segments:
        if seg.type == "text":
            parts.append(seg.text)
        elif seg.type == "at":
            parts.append(f"[@{seg.text}]" if seg.text else "[@]")
        elif seg.type == "image":
            parts.append("[图片]")
        elif seg.type == "record":
            parts.append("[语音]")
        elif seg.type == "video":
            parts.append("[视频]")
        elif seg.type == "file":
            parts.append(f"[文件] {seg.name}".rstrip())
        elif seg.type == "reply":
            parts.append("[回复]")
        else:
            parts.append(f"[{seg.type}]")
    return "".join(parts)
