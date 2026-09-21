"""Launcher for the SVoice XTTS service.

Selects the PyTorch runtime pack (CUDA, DirectML or CPU) *before* importing
anything heavy, then hands control to ``svoice_xtts.service.main``.

Installed layout (``%ProgramFiles%\\SVoice``)::

    service\\svoice_xtts_service.py   <- this file
    service\\svoice_xtts\\...
    runtime\\python\\python.exe
    runtime\\packs\\...

Development layout: run with any interpreter that has the dependencies
installed; no ``runtime`` directory is required.
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path


def _bootstrap() -> tuple[list[str], dict]:
    here = Path(__file__).resolve().parent
    if str(here) not in sys.path:
        sys.path.insert(0, str(here))

    pre = argparse.ArgumentParser(add_help=False)
    pre.add_argument("--runtime-dir", type=Path)
    pre.add_argument("--data-dir", type=Path)
    pre.add_argument("--torch-pack")
    known, remaining = pre.parse_known_args(sys.argv[1:])

    from svoice_xtts import hardware, runtime
    from svoice_xtts.paths import default_root

    runtime_dir = known.runtime_dir
    if runtime_dir is None:
        env_dir = os.environ.get("SVOICE_RUNTIME_DIR")
        runtime_dir = Path(env_dir) if env_dir else here.parent / "runtime"
    layout = runtime.discover_layout(runtime_dir if runtime_dir.is_dir() else None)
    data_dir = (known.data_dir or (default_root() / "XTTS")).expanduser()
    config = runtime.load_config_hints(data_dir)
    if known.torch_pack:
        torch_pack, reason = known.torch_pack, "pacote informado na linha de comando"
    else:
        torch_pack, reason = runtime.choose_torch_pack(layout, hardware.detect_system(), config)
    runtime.activate_packs(layout, torch_pack)
    info = runtime.runtime_info(layout, torch_pack, reason)

    if known.data_dir is not None:
        remaining = ["--data-dir", str(known.data_dir), *remaining]
    return remaining, info


def main() -> int:
    argv, info = _bootstrap()
    from svoice_xtts.service import main as service_main

    return service_main(argv, info)


if __name__ == "__main__":
    raise SystemExit(main())
