import os
import sys


os.environ.setdefault("PYGAME_HIDE_SUPPORT_PROMPT", "1")

if getattr(sys, "frozen", False) and hasattr(sys, "_MEIPASS"):
    os.chdir(sys._MEIPASS)
