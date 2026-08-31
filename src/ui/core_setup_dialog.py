"""核心安装向导：首次在受支持平台上运行时，要求阅读并同意 SnowLuma 的
EULA / LICENSE / 隐私说明，然后从官方 release 下载安装核心。"""

from __future__ import annotations

from loguru import logger

from src.core.core_updater import (
    LEGAL_DOCS,
    download_and_install,
    fetch_legal_text,
    mark_eula_accepted,
)
from src.ui.fluent_compat import (
    CaptionLabel,
    CheckBox,
    FluentIcon,
    LineEdit,
    PrimaryPushButton,
    PushButton,
    ScrollArea,
    TitleLabel,
)
from src.ui.qt_compat import (
    QDialog,
    QHBoxLayout,
    QLabel,
    Qt,
    QThread,
    QVBoxLayout,
    QWidget,
    Signal,
)


class _SetupThread(QThread):
    text_ready = Signal(str, str)  # (kind, text)
    installed = Signal(str)  # version
    failed = Signal(str, str)  # (context, message)

    def __init__(self, mode: str, kind: str = "", proxy: str | None = None, parent=None):
        super().__init__(parent)
        self._mode = mode
        self._kind = kind
        self._proxy = proxy

    def run(self):
        try:
            if self._mode == "fetch":
                self.text_ready.emit(self._kind, fetch_legal_text(self._kind, proxy=self._proxy))
            else:
                self.installed.emit(download_and_install(proxy=self._proxy))
        except Exception as exc:  # noqa: BLE001 — 面向用户
            logger.debug("核心向导任务失败 mode=%s", self._mode, exc_info=True)
            self.failed.emit(self._mode, str(exc))


class CoreSetupDialog(QDialog):
    def __init__(self, settings, parent=None):
        super().__init__(parent)
        self._settings = settings
        self._threads: list[_SetupThread] = []
        self._current_kind = "eula"

        self.setWindowTitle("安装监听核心")
        self.resize(640, 620)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(24, 20, 24, 20)
        layout.setSpacing(12)

        layout.addWidget(TitleLabel("安装监听核心"))
        intro = QLabel(
            "本程序的消息监听依赖 SnowLuma 官方核心（专有组件，不随本程序分发）。"
            "首次使用需从 SnowLuma 官方发布处下载安装，并同意其许可条款。"
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)

        switch_row = QHBoxLayout()
        self._doc_buttons = {}
        for kind, (label, _path) in LEGAL_DOCS.items():
            btn = PushButton(label.split("（")[0])
            btn.setIcon(FluentIcon.DOCUMENT)
            btn.clicked.connect(lambda _=False, k=kind: self._load_doc(k))
            switch_row.addWidget(btn)
            self._doc_buttons[kind] = btn
        switch_row.addStretch()
        layout.addLayout(switch_row)

        self._scroll = ScrollArea()
        self._scroll.setWidgetResizable(True)
        self._doc_label = QLabel("正在加载…")
        self._doc_label.setWordWrap(True)
        self._doc_label.setTextInteractionFlags(Qt.TextSelectableByMouse)
        self._doc_label.setAlignment(Qt.AlignTop | Qt.AlignLeft)
        self._doc_label.setContentsMargins(8, 8, 8, 8)
        holder = QWidget()
        holder_layout = QVBoxLayout(holder)
        holder_layout.setContentsMargins(0, 0, 0, 0)
        holder_layout.addWidget(self._doc_label)
        holder_layout.addStretch()
        self._scroll.setWidget(holder)
        layout.addWidget(self._scroll, 1)

        proxy_row = QHBoxLayout()
        proxy_row.addWidget(QLabel("下载源代理"))
        self._proxy_edit = LineEdit()
        self._proxy_edit.setText(
            str(self._settings.get("Core_Download_Proxy", "https://ghfast.top") or "")
        )
        self._proxy_edit.setPlaceholderText("推荐 https://ghfast.top；留空则直连 GitHub 官方")
        proxy_row.addWidget(self._proxy_edit, 1)
        layout.addLayout(proxy_row)

        self._agree = CheckBox("我已阅读并同意 SnowLuma 的 EULA、LICENSE 与隐私说明")
        self._agree.stateChanged.connect(self._on_agree_toggled)
        layout.addWidget(self._agree)

        self._status = CaptionLabel("")
        self._status.setWordWrap(True)
        self._status.setStyleSheet("color: #707070;")
        layout.addWidget(self._status)

        btn_row = QHBoxLayout()
        btn_row.addStretch()
        self._cancel_btn = PushButton("暂不安装")
        self._cancel_btn.setIcon(FluentIcon.CLOSE)
        self._cancel_btn.clicked.connect(self.reject)
        self._install_btn = PrimaryPushButton("同意并安装核心")
        self._install_btn.setIcon(FluentIcon.DOWNLOAD)
        self._install_btn.setEnabled(False)
        self._install_btn.clicked.connect(self._on_install)
        btn_row.addWidget(self._cancel_btn)
        btn_row.addWidget(self._install_btn)
        layout.addLayout(btn_row)

        self._load_doc("eula")

    # ─────────────── 文档加载 ───────────────

    def _load_doc(self, kind: str):
        self._current_kind = kind
        self._doc_label.setText(self.tr("正在加载…"))
        proxy = self._proxy_edit.text().strip() or None
        self._run_thread(_SetupThread("fetch", kind=kind, proxy=proxy, parent=self))

    def _on_text_ready(self, kind: str, text: str):
        if kind == self._current_kind:
            self._doc_label.setText(text)
            self._scroll.verticalScrollBar().setValue(0)

    # ─────────────── 交互 ───────────────

    def _on_agree_toggled(self, *_args):
        self._install_btn.setEnabled(self._agree.isChecked())

    def _on_install(self):
        self._install_btn.setEnabled(False)
        self._cancel_btn.setEnabled(False)
        self._agree.setEnabled(False)
        # 用户已勾选同意即视为接受 EULA（EULA §1.3）。
        mark_eula_accepted(self._settings)
        self._status.setText(self.tr("正在从官方下载并安装核心…"))
        proxy = self._proxy_edit.text().strip() or None
        self._run_thread(_SetupThread("install", proxy=proxy, parent=self))

    def _on_installed(self, version: str):
        self._status.setText(self.tr("核心已安装: {v}").format(v=version))
        self.accept()

    def _on_failed(self, context: str, message: str):
        if context == "fetch":
            self._doc_label.setText(
                self.tr("加载条款失败: {m}\n请检查网络或填写代理后重试。").format(m=message)
            )
            return
        self._status.setText(self.tr("安装失败: {m}").format(m=message))
        self._install_btn.setEnabled(self._agree.isChecked())
        self._cancel_btn.setEnabled(True)
        self._agree.setEnabled(True)

    # ─────────────── 线程管理 ───────────────

    def _run_thread(self, thread: _SetupThread):
        thread.text_ready.connect(self._on_text_ready)
        thread.installed.connect(self._on_installed)
        thread.failed.connect(self._on_failed)
        thread.finished.connect(
            lambda t=thread: self._threads.remove(t) if t in self._threads else None
        )
        self._threads.append(thread)
        thread.start()
