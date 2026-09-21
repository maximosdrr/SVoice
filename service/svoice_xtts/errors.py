from __future__ import annotations

from http import HTTPStatus


class ServiceError(Exception):
    """Error reported to clients with a stable code and a suggested action."""

    def __init__(
        self,
        message: str,
        status: int = HTTPStatus.BAD_REQUEST,
        *,
        code: str = "bad_request",
        action: str | None = None,
    ):
        super().__init__(message)
        self.status = int(status)
        self.code = code
        self.action = action

    def to_json(self) -> dict:
        payload = {"error": str(self), "code": self.code}
        if self.action:
            payload["action"] = self.action
        return payload


class CancelledError(ServiceError):
    def __init__(self, message: str = "Operação cancelada."):
        super().__init__(message, 499, code="cancelled")
