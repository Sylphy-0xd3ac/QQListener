from __future__ import annotations

import os
import sys
from importlib import import_module
from pathlib import Path
from types import SimpleNamespace


def _normalize_qt_api(value: object) -> str:
    api = str(value or "").strip().lower().replace("-", "").replace("_", "")
    if api in {"pyside2", "qt5"}:
        return "pyside2"
    return "pyside6"


def _configured_qt_api() -> str:
    env_api = os.environ.get("QQLISTENER_QT_API") or os.environ.get("QT_API")
    if env_api:
        return _normalize_qt_api(env_api)

    ui_package = sys.modules.get("src.ui")
    if ui_package is None:
        return "pyside6"

    return _normalize_qt_api(
        getattr(ui_package, "QT_API", getattr(ui_package, "qtapi", "pyside6"))
    )


QT_API = _configured_qt_api()
IS_PYSIDE2 = QT_API == "pyside2"
IS_PYSIDE6 = QT_API == "pyside6"

_BINDING = "PySide2" if IS_PYSIDE2 else "PySide6"
QtCore = import_module(f"{_BINDING}.QtCore")
QtGui = import_module(f"{_BINDING}.QtGui")
QtWidgets = import_module(f"{_BINDING}.QtWidgets")
try:
    QtSvg = import_module(f"{_BINDING}.QtSvg")
except Exception:
    QtSvg = None

QEasingCurve = QtCore.QEasingCurve
QFileSystemWatcher = QtCore.QFileSystemWatcher
QObject = QtCore.QObject
QPoint = QtCore.QPoint
QPropertyAnimation = QtCore.QPropertyAnimation
QRect = QtCore.QRect
QRectF = QtCore.QRectF
QSize = QtCore.QSize
Qt = QtCore.Qt
QThread = QtCore.QThread
QTimer = QtCore.QTimer
QTranslator = QtCore.QTranslator
QUrl = QtCore.QUrl
QVariantAnimation = QtCore.QVariantAnimation
Signal = QtCore.Signal
qVersion = QtCore.qVersion

QColor = QtGui.QColor
QCursor = QtGui.QCursor
QDesktopServices = QtGui.QDesktopServices
QFont = QtGui.QFont
QFontDatabase = QtGui.QFontDatabase
QIcon = QtGui.QIcon
QPainter = QtGui.QPainter
QPainterPath = QtGui.QPainterPath
QPen = QtGui.QPen
QPixmap = QtGui.QPixmap

QAction = getattr(QtGui, "QAction", None) or QtWidgets.QAction
QApplication = QtWidgets.QApplication
QDialog = QtWidgets.QDialog
QFileDialog = QtWidgets.QFileDialog
QFormLayout = QtWidgets.QFormLayout
QFrame = QtWidgets.QFrame
QGraphicsDropShadowEffect = QtWidgets.QGraphicsDropShadowEffect
QHBoxLayout = QtWidgets.QHBoxLayout
QLabel = QtWidgets.QLabel
QLineEdit = QtWidgets.QLineEdit
QMenu = QtWidgets.QMenu
QSizePolicy = QtWidgets.QSizePolicy
QStackedWidget = QtWidgets.QStackedWidget
QSystemTrayIcon = QtWidgets.QSystemTrayIcon
QVBoxLayout = QtWidgets.QVBoxLayout
QWidget = QtWidgets.QWidget


def _ensure_namespace(owner: object, name: str, values: dict[str, object]) -> None:
    if hasattr(owner, name):
        return

    try:
        setattr(owner, name, SimpleNamespace(**values))
    except Exception:
        pass


def _patch_pyside2_namespaces() -> None:
    if not IS_PYSIDE2:
        return

    if not hasattr(QApplication, "exec") and hasattr(QApplication, "exec_"):
        QApplication.exec = QApplication.exec_
    if not hasattr(QDialog, "exec") and hasattr(QDialog, "exec_"):
        QDialog.exec = QDialog.exec_

    _ensure_namespace(
        Qt,
        "WindowType",
        {
            "FramelessWindowHint": Qt.FramelessWindowHint,
            "WindowStaysOnTopHint": Qt.WindowStaysOnTopHint,
            "Tool": Qt.Tool,
        },
    )
    _ensure_namespace(
        Qt,
        "WidgetAttribute",
        {
            "WA_TranslucentBackground": Qt.WA_TranslucentBackground,
            "WA_ShowWithoutActivating": getattr(Qt, "WA_ShowWithoutActivating", 98),
            "WA_TransparentForMouseEvents": Qt.WA_TransparentForMouseEvents,
        },
    )
    _ensure_namespace(
        Qt,
        "MouseButton",
        {
            "LeftButton": Qt.LeftButton,
            "RightButton": Qt.RightButton,
        },
    )
    _ensure_namespace(
        Qt,
        "CursorShape",
        {
            "PointingHandCursor": Qt.PointingHandCursor,
            "OpenHandCursor": Qt.OpenHandCursor,
        },
    )
    _ensure_namespace(Qt, "AlignmentFlag", {"AlignCenter": Qt.AlignCenter})
    _ensure_namespace(Qt, "PenStyle", {"SolidLine": Qt.SolidLine})
    _ensure_namespace(Qt, "PenCapStyle", {"RoundCap": Qt.RoundCap})
    _ensure_namespace(Qt, "FillRule", {"WindingFill": Qt.WindingFill})
    _ensure_namespace(
        QPainter,
        "RenderHint",
        {
            "Antialiasing": QPainter.Antialiasing,
        },
    )
    _ensure_namespace(
        QPainter,
        "CompositionMode",
        {
            "CompositionMode_SourceIn": QPainter.CompositionMode_SourceIn,
        },
    )
    _ensure_namespace(QDialog, "DialogCode", {"Accepted": QDialog.Accepted})
    _ensure_namespace(QLineEdit, "EchoMode", {"Password": QLineEdit.Password})


_patch_pyside2_namespaces()


def event_position(event) -> QPoint:
    if hasattr(event, "position"):
        return event.position().toPoint()
    return event.pos()


def event_global_position(event) -> QPoint:
    if hasattr(event, "globalPosition"):
        return event.globalPosition().toPoint()
    return event.globalPos()


def screen_at(pos: QPoint):
    if hasattr(QApplication, "screenAt"):
        screen = QApplication.screenAt(pos)
        if screen:
            return screen
    return QApplication.primaryScreen()


def load_icon(
    path: str | os.PathLike | None,
    fallback_path: str | os.PathLike | None = None,
) -> QIcon:
    seen: set[str] = set()
    for candidate in (path, fallback_path):
        if not candidate:
            continue

        candidate_path = str(Path(candidate))
        if candidate_path in seen:
            continue
        seen.add(candidate_path)

        icon = QIcon(candidate_path)
        if not icon.isNull():
            return icon

        pixmap = QPixmap(candidate_path)
        if not pixmap.isNull():
            return QIcon(pixmap)

    return QIcon()
