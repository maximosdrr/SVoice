"""Run the service test suite with an installed runtime (no virtualenv needed).

Usage::

    "C:\\Program Files\\SVoice\\runtime\\python\\python.exe" service\\tools\\run_tests.py
    python service\\tools\\run_tests.py --runtime-dir <dir> [--torch-pack torch-cpu] [-v]

When ``--runtime-dir`` is omitted the installed runtime (%ProgramFiles%\\SVoice
\\runtime) or a checkout-local ``service\\runtime`` layout is used; without any
runtime the current interpreter must already provide the dependencies.
"""

from __future__ import annotations

import argparse
import os
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SERVICE_DIR = HERE.parent
if str(SERVICE_DIR) not in sys.path:
    sys.path.insert(0, str(SERVICE_DIR))

from svoice_xtts import runtime  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime-dir", type=Path)
    parser.add_argument("--torch-pack", default="torch-cpu")
    parser.add_argument("-v", "--verbose", action="store_true")
    parser.add_argument("pattern", nargs="?", default="test_*.py")
    args = parser.parse_args()

    candidates = [args.runtime_dir] if args.runtime_dir else [
        SERVICE_DIR / "runtime",
        Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "SVoice" / "runtime",
    ]
    for candidate in candidates:
        layout = runtime.discover_layout(candidate if candidate and candidate.is_dir() else None)
        if layout.is_packaged:
            pack = args.torch_pack if args.torch_pack in layout.installed_packs() else next(
                (name for name in layout.installed_packs() if name.startswith("torch-")), None)
            runtime.activate_packs(layout, pack)
            print(f"runtime: {layout.root} (pack {pack})")
            break
    else:
        print("runtime: interpretador atual")

    suite = unittest.defaultTestLoader.discover(str(SERVICE_DIR / "tests"), pattern=args.pattern)
    result = unittest.TextTestRunner(verbosity=2 if args.verbose else 1).run(suite)
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
