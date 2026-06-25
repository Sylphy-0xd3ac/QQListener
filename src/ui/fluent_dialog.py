from PySide6.QtWidgets import QDialog, QWidget
from qfluentwidgets import MessageBox


def show_fluent_message(
    parent: QWidget | None,
    title: str,
    content: str,
    yes_text: str = "确认",
    cancel_text: str | None = None,
) -> bool:
    """Show a Fluent modal message with a dimmed mask."""
    dialog = MessageBox(title, content, parent)
    dialog.yesButton.setText(yes_text)
    dialog.yesButton.setFixedHeight(40)

    if cancel_text is None:
        dialog.cancelButton.hide()
    else:
        dialog.cancelButton.setText(cancel_text)
        dialog.cancelButton.setFixedHeight(40)

    dialog.widget.setStyleSheet("""
        #centerWidget {
            background: white;
            border-radius: 8px;
        }
        #buttonGroup {
            background: #f7f7f7;
            border-top: 1px solid #e5e5e5;
        }
        QLabel#titleLabel {
            color: #111111;
            font-size: 20px;
            font-weight: 600;
        }
        QLabel#contentLabel {
            color: #202020;
            font-size: 14px;
        }
    """)

    return dialog.exec() == QDialog.DialogCode.Accepted
