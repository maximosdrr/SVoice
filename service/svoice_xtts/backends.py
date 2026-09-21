"""Inference backend abstraction.

Backends are selected in two stages:

1. The launcher (``svoice_xtts_service.py``) picks which PyTorch *runtime
   pack* to put on ``sys.path`` (CUDA, DirectML or CPU builds cannot coexist
   in one process).
2. Inside the process, :class:`BackendManager` checks which backends the
   loaded torch build actually supports, validates the preferred one with a
   full XTTS synthesis and falls back to CPU when anything fails.

Backend identifiers are stable strings used by the protocol and the UI:
``cuda`` (NVIDIA CUDA), ``directml`` (AMD/DirectX 12 via DirectML),
``rocm`` (AMD ROCm, reserved) and ``cpu``.
"""

from __future__ import annotations

import importlib
from dataclasses import dataclass, field
from typing import Any

BACKEND_LABELS = {
    "cuda": "NVIDIA CUDA",
    "directml": "AMD DirectML",
    "rocm": "AMD ROCm",
    "cpu": "CPU",
}

BACKEND_ORDER = ("cuda", "rocm", "directml", "cpu")
COMPUTE_MODES = ("auto", "cuda", "directml", "rocm", "cpu")
LEGACY_MODE_ALIASES = {"gpu": "auto"}
EXPERIMENTAL_BACKENDS = {"directml"}


def normalize_compute_mode(value: Any) -> str:
    mode = str(value or "auto").strip().lower()
    mode = LEGACY_MODE_ALIASES.get(mode, mode)
    return mode if mode in COMPUTE_MODES else "auto"


@dataclass
class BackendAvailability:
    backend: str
    available: bool
    reason: str
    device_name: str | None = None
    details: dict[str, Any] = field(default_factory=dict)

    def to_json(self) -> dict[str, Any]:
        return {
            "backend": self.backend,
            "label": BACKEND_LABELS.get(self.backend, self.backend),
            "available": self.available,
            "reason": self.reason,
            "device_name": self.device_name,
            "experimental": self.backend in EXPERIMENTAL_BACKENDS,
            **self.details,
        }


class Backend:
    """A torch execution target."""

    id: str = "cpu"

    @property
    def label(self) -> str:
        return BACKEND_LABELS[self.id]

    def probe(self, torch: Any) -> BackendAvailability:
        raise NotImplementedError

    def device(self, torch: Any) -> Any:
        raise NotImplementedError

    def empty_cache(self, torch: Any) -> None:
        return

    def peak_memory_bytes(self, torch: Any) -> int | None:
        return None

    def reset_peak_memory(self, torch: Any) -> None:
        return

    def conditioning_on_cpu(self) -> bool:
        """Whether conditioning latents must be computed on the CPU copy.

        DirectML does not implement complex tensors used by ``torch.stft``.
        """
        return False

    def bypass_inference_mode(self) -> bool:
        """Run ``Xtts.inference`` under ``torch.no_grad`` instead of
        ``torch.inference_mode``.

        torch-directml fails with "Cannot set version_counter for inference
        tensor" when generation runs inside ``inference_mode``.
        """
        return False


class CpuBackend(Backend):
    id = "cpu"

    def probe(self, torch: Any) -> BackendAvailability:
        return BackendAvailability(
            "cpu", True, "Disponível em qualquer computador.", "CPU",
            {"threads": int(torch.get_num_threads())},
        )

    def device(self, torch: Any) -> Any:
        return torch.device("cpu")


class CudaBackend(Backend):
    id = "cuda"

    def probe(self, torch: Any) -> BackendAvailability:
        cuda_version = getattr(getattr(torch, "version", None), "cuda", None)
        if not cuda_version:
            return BackendAvailability(
                "cuda", False, "O runtime PyTorch carregado não inclui suporte a CUDA."
            )
        try:
            if not torch.cuda.is_available():
                return BackendAvailability(
                    "cuda",
                    False,
                    "Nenhuma GPU NVIDIA compatível foi encontrada ou o driver "
                    "é anterior ao exigido pelo runtime CUDA.",
                    details={"cuda_runtime": cuda_version},
                )
            name = torch.cuda.get_device_name(0)
            capability = torch.cuda.get_device_capability(0)
            total = int(torch.cuda.get_device_properties(0).total_memory)
            return BackendAvailability(
                "cuda",
                True,
                "GPU NVIDIA detectada pelo PyTorch.",
                name,
                {
                    "cuda_runtime": cuda_version,
                    "compute_capability": f"{capability[0]}.{capability[1]}",
                    "memory_bytes": total,
                },
            )
        except Exception as error:  # pragma: no cover - depends on drivers
            return BackendAvailability("cuda", False, f"Falha ao consultar CUDA: {error}")

    def device(self, torch: Any) -> Any:
        return torch.device("cuda", 0)

    def empty_cache(self, torch: Any) -> None:
        try:
            torch.cuda.empty_cache()
        except Exception:
            pass

    def peak_memory_bytes(self, torch: Any) -> int | None:
        try:
            return int(torch.cuda.max_memory_allocated(0))
        except Exception:
            return None

    def reset_peak_memory(self, torch: Any) -> None:
        try:
            torch.cuda.reset_peak_memory_stats(0)
        except Exception:
            pass


class RocmBackend(Backend):
    """AMD ROCm (official AMD PyTorch-on-Windows preview).

    Only reported as available when the loaded torch build is a ROCm build and
    a device is visible. No ROCm runtime pack is shipped until a full XTTS
    validation on supported hardware is recorded (see docs/backends.md).
    """

    id = "rocm"

    def probe(self, torch: Any) -> BackendAvailability:
        hip_version = getattr(getattr(torch, "version", None), "hip", None)
        if not hip_version:
            return BackendAvailability(
                "rocm", False, "O runtime PyTorch carregado não é uma build ROCm."
            )
        try:
            if not torch.cuda.is_available():
                return BackendAvailability(
                    "rocm", False, "Nenhuma GPU AMD compatível com ROCm foi encontrada.",
                    details={"hip_runtime": hip_version},
                )
            name = torch.cuda.get_device_name(0)
            return BackendAvailability(
                "rocm", True, "GPU AMD detectada pelo PyTorch ROCm.", name,
                {"hip_runtime": hip_version},
            )
        except Exception as error:  # pragma: no cover
            return BackendAvailability("rocm", False, f"Falha ao consultar ROCm: {error}")

    def device(self, torch: Any) -> Any:
        return torch.device("cuda", 0)

    def empty_cache(self, torch: Any) -> None:
        try:
            torch.cuda.empty_cache()
        except Exception:
            pass


class DirectMlBackend(Backend):
    id = "directml"

    def __init__(self) -> None:
        self._module: Any = None

    def _load(self) -> Any:
        if self._module is None:
            self._module = importlib.import_module("torch_directml")
        return self._module

    def probe(self, torch: Any) -> BackendAvailability:
        try:
            module = self._load()
        except Exception as error:
            return BackendAvailability(
                "directml", False,
                f"O pacote torch-directml não está instalado neste runtime ({error.__class__.__name__}).",
            )
        try:
            if not module.is_available():
                return BackendAvailability(
                    "directml", False, "Nenhum adaptador DirectX 12 disponível para o DirectML."
                )
            count = int(module.device_count())
            index = int(module.default_device())
            name = str(module.device_name(index)).rstrip("\x00").strip()
            return BackendAvailability(
                "directml", True,
                "Adaptador DirectX 12 detectado pelo DirectML (experimental).",
                name,
                {"device_count": count, "device_index": index, "torch_directml": getattr(module, "__version__", None)},
            )
        except Exception as error:
            return BackendAvailability("directml", False, f"Falha ao consultar o DirectML: {error}")

    def device(self, torch: Any) -> Any:
        return self._load().device()

    def conditioning_on_cpu(self) -> bool:
        return True

    def bypass_inference_mode(self) -> bool:
        return True


BACKENDS: dict[str, Backend] = {
    "cuda": CudaBackend(),
    "rocm": RocmBackend(),
    "directml": DirectMlBackend(),
    "cpu": CpuBackend(),
}


def get_backend(backend_id: str) -> Backend:
    try:
        return BACKENDS[backend_id]
    except KeyError as error:
        raise ValueError(f"Backend desconhecido: {backend_id}") from error


def probe_all(torch: Any) -> dict[str, BackendAvailability]:
    return {backend_id: backend.probe(torch) for backend_id, backend in BACKENDS.items()}


def candidates_for_mode(mode: str, availability: dict[str, BackendAvailability]) -> list[str]:
    """Ordered backends to try for a compute mode (always ends with cpu)."""
    mode = normalize_compute_mode(mode)
    if mode == "cpu":
        return ["cpu"]
    if mode != "auto":
        return [mode, "cpu"] if availability.get(mode, BackendAvailability(mode, False, "")).available else ["cpu"]
    ordered = [
        backend_id
        for backend_id in BACKEND_ORDER
        if backend_id != "cpu" and availability.get(backend_id, BackendAvailability(backend_id, False, "")).available
    ]
    return ordered + ["cpu"]
