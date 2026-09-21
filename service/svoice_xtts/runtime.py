"""Runtime pack layout and selection (executed before importing torch).

Layout of an installed runtime directory::

    runtime/
      manifest.json            what was built (packs, versions, hashes)
      installed.json           which packs are installed on this machine
      python/                  embeddable CPython (only in installed layout)
      packs/base/              coqui-tts and every dependency except torch
      packs/torch-cpu/         torch CPU build
      packs/torch-cuda/        torch CUDA build (also works on CPU)
      packs/torch-directml/    torch 2.4.1 CPU build + torch-directml

Exactly one ``torch-*`` pack is placed on ``sys.path`` per process.
"""

from __future__ import annotations

import json
import os
import site
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from .backends import normalize_compute_mode
from .hardware import SystemInfo, primary_vendor

TORCH_PACKS = {
    "cuda": "torch-cuda",
    "rocm": "torch-rocm",
    "directml": "torch-directml",
    "cpu": "torch-cpu",
}
PACK_BACKENDS = {
    "torch-cuda": ["cuda", "cpu"],
    "torch-rocm": ["rocm", "cpu"],
    "torch-directml": ["directml", "cpu"],
    "torch-cpu": ["cpu"],
}


@dataclass
class RuntimeLayout:
    root: Path | None
    packs_dir: Path | None
    manifest: dict[str, Any] = field(default_factory=dict)
    installed: dict[str, Any] = field(default_factory=dict)

    @property
    def is_packaged(self) -> bool:
        return self.packs_dir is not None and (self.packs_dir / "base").is_dir()

    def installed_packs(self) -> list[str]:
        if not self.packs_dir:
            return []
        names = []
        for child in sorted(self.packs_dir.iterdir()) if self.packs_dir.is_dir() else []:
            if child.is_dir() and (child / ".svoice-pack.json").is_file():
                names.append(child.name)
        return names

    def pack_version(self, name: str) -> str | None:
        if not self.packs_dir:
            return None
        try:
            payload = json.loads((self.packs_dir / name / ".svoice-pack.json").read_text(encoding="utf-8"))
            return str(payload.get("version"))
        except (OSError, ValueError):
            return None


def discover_layout(root: Path | None) -> RuntimeLayout:
    if root is None:
        return RuntimeLayout(None, None)
    packs_dir = root / "packs"
    manifest: dict[str, Any] = {}
    installed: dict[str, Any] = {}
    for name, target in (("manifest.json", manifest), ("installed.json", installed)):
        try:
            payload = json.loads((root / name).read_text(encoding="utf-8"))
            if isinstance(payload, dict):
                target.update(payload)
        except (OSError, ValueError):
            pass
    return RuntimeLayout(root, packs_dir if packs_dir.is_dir() else None, manifest, installed)


def load_config_hints(data_dir: Path) -> dict[str, Any]:
    try:
        payload = json.loads((data_dir / "config.json").read_text(encoding="utf-8"))
        return payload if isinstance(payload, dict) else {}
    except (OSError, ValueError):
        return {}


def choose_torch_pack(
    layout: RuntimeLayout,
    system: SystemInfo,
    config: dict[str, Any],
) -> tuple[str | None, str]:
    """Return (pack name, reason). ``None`` means "use the current interpreter"."""
    installed = set(layout.installed_packs())
    if not layout.is_packaged:
        return None, "runtime de desenvolvimento (interpretador atual)"
    mode = normalize_compute_mode(config.get("compute_mode"))
    validations = config.get("backend_validation") if isinstance(config.get("backend_validation"), dict) else {}

    def failed(backend: str) -> bool:
        record = validations.get(backend)
        return isinstance(record, dict) and record.get("ok") is False

    def pick(backend: str) -> str | None:
        pack = TORCH_PACKS[backend]
        return pack if pack in installed else None

    if mode != "auto":
        pack = pick(mode)
        if pack:
            return pack, f"modo {mode} selecionado pelo usuário"
        fallback = pick("cpu") or next((p for p in ("torch-cuda", "torch-directml") if p in installed), None)
        return fallback, f"pacote para {mode} não instalado; usando {fallback}"

    vendor = primary_vendor(system)
    order: list[str] = []
    if vendor == "nvidia":
        order = ["cuda", "cpu"]
    elif vendor == "amd":
        order = ["rocm", "directml", "cpu"]
    elif vendor == "intel":
        order = ["directml", "cpu"]
    else:
        order = ["cpu"]
    skipped: list[str] = []
    for backend in order:
        pack = pick(backend)
        if pack is None:
            continue
        if backend != "cpu" and failed(backend):
            skipped.append(backend)
            continue
        reason = f"GPU {vendor}: pacote {pack} selecionado automaticamente"
        if skipped:
            reason += f" (backend {', '.join(skipped)} falhou na validação anterior)"
        return pack, reason
    fallback = pick("cpu") or next((p for p in ("torch-cuda", "torch-directml", "torch-rocm") if p in installed), None)
    return fallback, f"nenhum pacote de GPU validado; usando {fallback}"


def activate_packs(layout: RuntimeLayout, torch_pack: str | None) -> list[str]:
    """Prepend the base pack and the chosen torch pack to ``sys.path``."""
    activated: list[str] = []
    if not layout.is_packaged or layout.packs_dir is None:
        return activated
    for name in (torch_pack, "base"):
        if not name:
            continue
        directory = layout.packs_dir / name
        if directory.is_dir():
            site.addsitedir(str(directory))
            # addsitedir appends; move the pack to the front so it wins over
            # anything the embeddable interpreter may already expose.
            entry = str(directory)
            if entry in sys.path:
                sys.path.remove(entry)
            sys.path.insert(0, entry)
            activated.append(name)
            # Wheels such as mkl/intel-openmp (needed by torch 2.4 on Windows)
            # place DLLs under Libraryin; make them loadable.
            dll_dir = directory / "Library" / "bin"
            if dll_dir.is_dir():
                os.environ["PATH"] = str(dll_dir) + os.pathsep + os.environ.get("PATH", "")
                if hasattr(os, "add_dll_directory"):
                    try:
                        os.add_dll_directory(str(dll_dir))
                    except OSError:
                        pass
    return activated


def runtime_info(layout: RuntimeLayout, torch_pack: str | None, reason: str) -> dict[str, Any]:
    return {
        "packaged": layout.is_packaged,
        "runtime_dir": str(layout.root) if layout.root else None,
        "installed_packs": layout.installed_packs(),
        "torch_pack": torch_pack,
        "torch_pack_version": layout.pack_version(torch_pack) if torch_pack else None,
        "base_pack_version": layout.pack_version("base"),
        "selection_reason": reason,
        "possible_backends": PACK_BACKENDS.get(torch_pack or "", ["cpu"]) if layout.is_packaged else None,
        "python": sys.version.split()[0],
        "executable": sys.executable,
    }
