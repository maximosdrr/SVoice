"""GPU and system detection that does not import PyTorch.

The launcher must decide which PyTorch runtime pack to load *before* torch is
imported, so detection relies on the Windows display-class registry and on
``nvidia-smi`` when present.
"""

from __future__ import annotations

import ctypes
import os
import platform
import re
import subprocess
import sys
from dataclasses import asdict, dataclass, field
from typing import Any

DISPLAY_CLASS_KEY = (
    r"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"
)

VENDOR_IDS = {
    "10de": "nvidia",
    "1002": "amd",
    "1022": "amd",
    "8086": "intel",
}


@dataclass
class GpuInfo:
    name: str
    vendor: str  # nvidia | amd | intel | unknown
    driver_version: str | None = None
    memory_bytes: int | None = None
    device_id: str | None = None
    nvidia_driver: str | None = None

    def to_json(self) -> dict[str, Any]:
        return asdict(self)


@dataclass
class SystemInfo:
    os_version: str
    machine: str
    python_version: str
    cpu_count: int
    total_memory_bytes: int | None
    gpus: list[GpuInfo] = field(default_factory=list)

    def to_json(self) -> dict[str, Any]:
        return {
            "os_version": self.os_version,
            "machine": self.machine,
            "python_version": self.python_version,
            "cpu_count": self.cpu_count,
            "total_memory_bytes": self.total_memory_bytes,
            "gpus": [gpu.to_json() for gpu in self.gpus],
        }


def _read_registry_gpus() -> list[GpuInfo]:
    if sys.platform != "win32":
        return []
    import winreg

    gpus: list[GpuInfo] = []
    try:
        root = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, DISPLAY_CLASS_KEY)
    except OSError:
        return gpus
    with root:
        index = 0
        while True:
            try:
                sub_name = winreg.EnumKey(root, index)
            except OSError:
                break
            index += 1
            if not re.fullmatch(r"\d{4}", sub_name):
                continue
            try:
                with winreg.OpenKey(root, sub_name) as key:
                    gpu = _gpu_from_key(winreg, key)
                    if gpu is not None:
                        gpus.append(gpu)
            except OSError:
                continue
    return gpus


def _gpu_from_key(winreg: Any, key: Any) -> GpuInfo | None:
    def value(name: str) -> Any:
        try:
            return winreg.QueryValueEx(key, name)[0]
        except OSError:
            return None

    description = value("DriverDesc")
    if not description:
        return None
    matching = str(value("MatchingDeviceId") or "").lower()
    vendor = "unknown"
    match = re.search(r"ven_([0-9a-f]{4})", matching)
    if match:
        vendor = VENDOR_IDS.get(match.group(1), "unknown")
    provider = str(value("ProviderName") or "").lower()
    if vendor == "unknown":
        if "nvidia" in provider:
            vendor = "nvidia"
        elif "advanced micro devices" in provider or "amd" in provider:
            vendor = "amd"
        elif "intel" in provider:
            vendor = "intel"
    memory = value("HardwareInformation.qwMemorySize")
    if isinstance(memory, bytes):
        memory = int.from_bytes(memory[:8], "little")
    return GpuInfo(
        name=str(description),
        vendor=vendor,
        driver_version=str(value("DriverVersion") or "") or None,
        memory_bytes=int(memory) if isinstance(memory, int) and memory > 0 else None,
        device_id=matching or None,
    )


def _nvidia_smi_driver() -> str | None:
    if sys.platform != "win32":
        return None
    candidates = [
        os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "nvidia-smi.exe"),
        "nvidia-smi",
    ]
    for candidate in candidates:
        try:
            result = subprocess.run(
                [candidate, "--query-gpu=driver_version", "--format=csv,noheader"],
                capture_output=True,
                text=True,
                timeout=10,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                check=False,
            )
        except (OSError, subprocess.TimeoutExpired):
            continue
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip().splitlines()[0].strip()
    return None


def _total_memory_bytes() -> int | None:
    if sys.platform != "win32":
        return None

    class MemoryStatus(ctypes.Structure):
        _fields_ = [
            ("dwLength", ctypes.c_uint32),
            ("dwMemoryLoad", ctypes.c_uint32),
            ("ullTotalPhys", ctypes.c_uint64),
            ("ullAvailPhys", ctypes.c_uint64),
            ("ullTotalPageFile", ctypes.c_uint64),
            ("ullAvailPageFile", ctypes.c_uint64),
            ("ullTotalVirtual", ctypes.c_uint64),
            ("ullAvailVirtual", ctypes.c_uint64),
            ("ullAvailExtendedVirtual", ctypes.c_uint64),
        ]

    status = MemoryStatus()
    status.dwLength = ctypes.sizeof(MemoryStatus)
    try:
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return int(status.ullTotalPhys)
    except Exception:
        pass
    return None


def process_memory_bytes() -> int | None:
    """Working set of the current process (Windows) or None."""
    if sys.platform != "win32":
        return None

    class ProcessMemoryCounters(ctypes.Structure):
        _fields_ = [
            ("cb", ctypes.c_uint32),
            ("PageFaultCount", ctypes.c_uint32),
            ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t),
            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
            ("PagefileUsage", ctypes.c_size_t),
            ("PeakPagefileUsage", ctypes.c_size_t),
        ]

    counters = ProcessMemoryCounters()
    counters.cb = ctypes.sizeof(ProcessMemoryCounters)
    try:
        kernel32 = ctypes.windll.kernel32
        kernel32.GetCurrentProcess.restype = ctypes.c_void_p
        query = ctypes.windll.psapi.GetProcessMemoryInfo
        query.argtypes = [ctypes.c_void_p, ctypes.POINTER(ProcessMemoryCounters), ctypes.c_uint32]
        query.restype = ctypes.c_int
        if query(kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
            return int(counters.WorkingSetSize)
    except Exception:
        pass
    return None


def detect_system() -> SystemInfo:
    gpus = _read_registry_gpus()
    nvidia_driver = _nvidia_smi_driver() if any(g.vendor == "nvidia" for g in gpus) else None
    for gpu in gpus:
        if gpu.vendor == "nvidia":
            gpu.nvidia_driver = nvidia_driver
    return SystemInfo(
        os_version=platform.platform(),
        machine=platform.machine(),
        python_version=platform.python_version(),
        cpu_count=os.cpu_count() or 1,
        total_memory_bytes=_total_memory_bytes(),
        gpus=gpus,
    )


def primary_vendor(system: SystemInfo) -> str:
    """Vendor of the most capable discrete GPU (nvidia > amd > intel)."""
    for vendor in ("nvidia", "amd", "intel"):
        if any(gpu.vendor == vendor for gpu in system.gpus):
            return vendor
    return "none"


def parse_driver_version(value: str | None) -> tuple[int, ...]:
    if not value:
        return ()
    parts = re.findall(r"\d+", value)
    return tuple(int(part) for part in parts)
