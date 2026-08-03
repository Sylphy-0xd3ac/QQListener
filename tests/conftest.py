import sys
from pathlib import Path

# 保证 `import src.*` 可用（仓库根加入 sys.path）
ROOT = Path(__file__).resolve().parent.parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))
