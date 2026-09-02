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
class QuotedMessage:
    """被引用的那条消息（来自 SrcMsg）。"""

    sender_id: str = ""
    sender_name: str = ""
    time: int = 0
    seq: int = 0
    segments: list[Segment] = field(default_factory=list)


@dataclass
class CapturedMessage:
    scene: str
    peer_id: str
    peer_name: str
    sender_id: str
    sender_name: str
    segments: list[Segment]
    raw_seq: int
    account_uid: str = ""
    source_pid: int = 0
    sender_nickname: str = ""
    sender_remark: str = ""
    sender_group_card: str = ""
    peer_uid: str = ""
    timestamp: int = 0


def segments_text(segments: list[Segment]) -> str:
    parts: list[str] = []
    for seg in segments:
        if seg.type == "text":
            parts.append(seg.text)
        elif seg.type == "at":
            # 线上的 @ 文本本来就带 "@" 和尾随空格，别渲染成 [@@李四 ]。
            name = seg.text.strip().lstrip("@")
            parts.append(f"[@{name}]" if name else "[@]")
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


def message_text(msg: "CapturedMessage") -> str:
    """本条消息正文。引用块单独渲染，不混进正文里。"""
    return segments_text([seg for seg in msg.segments if seg.type != "reply"])


def quoted_message(msg: "CapturedMessage") -> QuotedMessage | None:
    """取出被引用的消息；没有引用时返回 None。"""
    for seg in msg.segments:
        if seg.type != "reply":
            continue
        return QuotedMessage(
            sender_id=str(seg.extra.get("sender_id", "") or ""),
            sender_name=str(seg.extra.get("sender_name", "") or ""),
            time=int(seg.extra.get("time", 0) or 0),
            seq=int(seg.extra.get("seq", 0) or 0),
            segments=list(seg.extra.get("segments") or []),
        )
    return None
