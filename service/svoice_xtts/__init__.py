"""SVoice XTTS v2 local service.

The service is a standalone per-user process. It is started on demand by the
Xbox Game Bar bridge (or by the installer/diagnostics helper), listens only on
``127.0.0.1`` with a bearer token and exits after a period of inactivity.
"""

SERVICE_VERSION = "2.2.0"
PROTOCOL_VERSION = 2
MODEL_NAME = "tts_models/multilingual/multi-dataset/xtts_v2"
