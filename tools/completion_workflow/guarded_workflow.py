"""Exclusive operation runner for independently reviewed adapters.

There are deliberately NO live adapters enabled. Native UI/installer shortcuts cannot
be represented as an owned verified operation yet and therefore fail closed here.
This module does not click UI, install policies, authenticate, or authorize spending.
"""
from dataclasses import dataclass
from typing import Callable, Mapping

from completion_register import Register, Rejected, hash_value


@dataclass(frozen=True)
class ReviewedAdapter:
    kind: str
    identity_sha256: str
    source_sha256: str
    execute_and_verify: Callable[[dict], dict]


class CompletionWorkflow:
    def __init__(self, register: Register, reviewed_adapters: Mapping[str, ReviewedAdapter],
                 admitted_adapter_pins: Mapping[str, tuple[str, str]]):
        self.register = register
        self.adapters = dict(reviewed_adapters)
        self.pins = dict(admitted_adapter_pins)

    def run(self, route: str, operation_id: str):
        adapter = self.adapters.get(route)
        pins = self.pins.get(route)
        if adapter is None or pins is None:
            raise Rejected("NoReviewedOwningAdapterForRoute")
        hash_value(adapter.identity_sha256)
        hash_value(adapter.source_sha256)
        if pins != (adapter.identity_sha256, adapter.source_sha256):
            raise Rejected("AdapterReviewPinMismatch")
        # No callback can run before the committed claim and fsynced anchor.
        ticket = self.register.claim(adapter.kind, adapter.identity_sha256, operation_id)
        try:
            # Commit/fsync/scheduling may itself cross the deadline. Recheck now;
            # every real adapter must additionally check immediately at actual I/O
            # after ANY platform approval, scheduling delay or waiting period.
            self.register.require_pending_before_effect(operation_id, adapter.identity_sha256)
            receipt = adapter.execute_and_verify(ticket)
            # Same operation, exact identity, verifier evidence. The adapter, NOT
            # the untrusted caller, must derive these from observed outcomes.
            result = self.register.complete(operation_id, receipt)
            if result != "Succeeded":
                raise Rejected("ObservedOperationStoppedRun")
            return result
        except BaseException:
            # Never infer 'unexecuted', refund a slot, retry, or convert an error
            # into success. A durable pending claim blocks restart if no trustworthy
            # completion receipt exists. Exception prose is not persisted/exported.
            raise


def live_workflow(register: Register):
    # Fail closed until an independent review admits actual exclusive adapters.
    return CompletionWorkflow(register, {}, {})
