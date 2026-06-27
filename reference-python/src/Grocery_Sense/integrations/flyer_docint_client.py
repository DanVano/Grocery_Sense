from __future__ import annotations

import os
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict

from azure.ai.documentintelligence import DocumentIntelligenceClient
from azure.core.credentials import AzureKeyCredential


@dataclass(frozen=True)
class AzureLayoutResult:
    operation_id: str
    analyze_result: Dict[str, Any]


class FlyerDocIntClient:
    """
    Azure Document Intelligence client for flyers.

    Uses prebuilt-layout to extract text + layout from PDFs/images.

    Env vars:
      DOCUMENTINTELLIGENCE_ENDPOINT
      DOCUMENTINTELLIGENCE_API_KEY
    """

    def __init__(self) -> None:
        self.endpoint = os.environ.get("DOCUMENTINTELLIGENCE_ENDPOINT", "").strip()
        self.api_key = os.environ.get("DOCUMENTINTELLIGENCE_API_KEY", "").strip()
        self.locale = "en-US"

        if not self.endpoint or not self.api_key:
            raise RuntimeError(
                "Missing Azure Document Intelligence credentials.\n"
                "Set DOCUMENTINTELLIGENCE_ENDPOINT and DOCUMENTINTELLIGENCE_API_KEY environment variables."
            )

        self.client = DocumentIntelligenceClient(
            endpoint=self.endpoint,
            credential=AzureKeyCredential(self.api_key),
        )

    def analyze_layout_file(self, file_path: str | Path) -> AzureLayoutResult:
        p = Path(file_path)
        if not p.exists():
            raise FileNotFoundError(str(p))

        with p.open("rb") as f:
            poller = self.client.begin_analyze_document(
                "prebuilt-layout",
                body=f,
                locale=self.locale,
            )

        result = poller.result()

        operation_id = str(poller.details.get("operation_id") or "").strip()
        if not operation_id:
            ts = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
            operation_id = f"layout_{ts}_{p.stem}"

        return AzureLayoutResult(operation_id=operation_id, analyze_result=result.as_dict())
