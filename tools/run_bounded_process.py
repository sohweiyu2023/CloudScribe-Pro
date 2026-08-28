#!/usr/bin/env python3
"""Run one process with wall-clock and stdout/stderr byte bounds.

The helper intentionally avoids a shell, streams output to bounded files, can
mirror output to the caller, and terminates the process tree on timeout or
output-cap breach. It is used by promotion scripts so a faulty tool cannot hang
or exhaust the evidence volume indefinitely.
"""

from __future__ import annotations

import argparse
import math
import os
import pathlib
import signal
import stat
import subprocess
import sys
import threading
import time
from dataclasses import dataclass
from typing import BinaryIO

DEFAULT_TIMEOUT_SECONDS = 900.0
DEFAULT_MAX_OUTPUT_BYTES = 64 * 1024 * 1024
TERMINATION_GRACE_SECONDS = 5.0
READ_BLOCK_BYTES = 64 * 1024


class RunnerError(ValueError):
    pass


@dataclass
class StreamState:
    label: str
    count: int = 0
    exceeded: bool = False
    failure: BaseException | None = None


def positive_float(value: str) -> float:
    parsed = float(value)
    if not math.isfinite(parsed) or parsed <= 0:
        raise argparse.ArgumentTypeError("must be a finite number greater than zero")
    return parsed


def positive_int(value: str) -> int:
    parsed = int(value)
    if parsed <= 0:
        raise argparse.ArgumentTypeError("must be greater than zero")
    return parsed


def absolute_lexical_path(path: pathlib.Path) -> pathlib.Path:
    """Return an absolute path without resolving links or requiring existence."""

    return pathlib.Path(os.path.abspath(os.fspath(path.expanduser())))


def path_is_link_or_reparse(metadata: os.stat_result) -> bool:
    if stat.S_ISLNK(metadata.st_mode):
        return True
    attributes = getattr(metadata, "st_file_attributes", 0)
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
    return bool(reparse_flag and attributes & reparse_flag)


def lexical_components(path: pathlib.Path) -> tuple[pathlib.Path, ...]:
    """Return root-to-leaf lexical components without following links."""

    return tuple(reversed((path, *path.parents)))


def lstat_if_present(path: pathlib.Path) -> os.stat_result | None:
    try:
        return path.lstat()
    except FileNotFoundError:
        return None


def validate_existing_component(
    component: pathlib.Path,
    metadata: os.stat_result,
    label: str,
    *,
    leaf: bool,
) -> None:
    if path_is_link_or_reparse(metadata):
        if leaf:
            raise RunnerError(f"{label} path must not be a symbolic-link or reparse point")
        raise RunnerError(f"{label} path must not traverse a symbolic-link ancestor")
    if not leaf and not stat.S_ISDIR(metadata.st_mode):
        raise RunnerError(f"{label} path has a non-directory ancestor: {component}")


def create_validated_parent(parent: pathlib.Path, label: str) -> None:
    """Create missing parent components only after every prefix is link-safe."""

    components = lexical_components(parent)
    for component in components:
        metadata = lstat_if_present(component)
        if metadata is None:
            try:
                component.mkdir()
            except FileExistsError:
                # A racing creator must still pass the same physical-path gate.
                pass
            metadata = lstat_if_present(component)
            if metadata is None:
                raise RunnerError(f"{label} parent could not be created: {component}")
        validate_existing_component(component, metadata, label, leaf=False)


def validate_output_path(path: pathlib.Path | None, label: str) -> pathlib.Path | None:
    if path is None:
        return None
    candidate = absolute_lexical_path(path)
    create_validated_parent(candidate.parent, label)
    metadata = lstat_if_present(candidate)
    if metadata is not None:
        validate_existing_component(candidate, metadata, label, leaf=True)
        raise RunnerError(f"{label} path already exists; refusing to overwrite evidence")
    return candidate


def output_paths_are_same(first: pathlib.Path | None, second: pathlib.Path | None) -> bool:
    if first is None or second is None:
        return False
    first_key = os.path.normcase(os.fspath(absolute_lexical_path(first)))
    second_key = os.path.normcase(os.fspath(absolute_lexical_path(second)))
    return first_key == second_key


def open_exclusive_output(path: pathlib.Path | None) -> BinaryIO | None:
    if path is None:
        return None
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    if hasattr(os, "O_BINARY"):
        flags |= os.O_BINARY
    if hasattr(os, "O_NOINHERIT"):
        flags |= os.O_NOINHERIT
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    descriptor = os.open(path, flags, 0o600)
    return os.fdopen(descriptor, "wb")


class WindowsJob:
    """Own a kill-on-close Windows Job Object for one command tree."""

    def __init__(self, process: subprocess.Popen[bytes]) -> None:
        self._handle: int | None = None
        if os.name != "nt":
            return

        import ctypes
        from ctypes import wintypes

        class JobObjectBasicLimitInformation(ctypes.Structure):
            _fields_ = [
                ("PerProcessUserTimeLimit", ctypes.c_longlong),
                ("PerJobUserTimeLimit", ctypes.c_longlong),
                ("LimitFlags", wintypes.DWORD),
                ("MinimumWorkingSetSize", ctypes.c_size_t),
                ("MaximumWorkingSetSize", ctypes.c_size_t),
                ("ActiveProcessLimit", wintypes.DWORD),
                ("Affinity", ctypes.c_size_t),
                ("PriorityClass", wintypes.DWORD),
                ("SchedulingClass", wintypes.DWORD),
            ]

        class IoCounters(ctypes.Structure):
            _fields_ = [
                ("ReadOperationCount", ctypes.c_ulonglong),
                ("WriteOperationCount", ctypes.c_ulonglong),
                ("OtherOperationCount", ctypes.c_ulonglong),
                ("ReadTransferCount", ctypes.c_ulonglong),
                ("WriteTransferCount", ctypes.c_ulonglong),
                ("OtherTransferCount", ctypes.c_ulonglong),
            ]

        class JobObjectExtendedLimitInformation(ctypes.Structure):
            _fields_ = [
                ("BasicLimitInformation", JobObjectBasicLimitInformation),
                ("IoInfo", IoCounters),
                ("ProcessMemoryLimit", ctypes.c_size_t),
                ("JobMemoryLimit", ctypes.c_size_t),
                ("PeakProcessMemoryUsed", ctypes.c_size_t),
                ("PeakJobMemoryUsed", ctypes.c_size_t),
            ]

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CreateJobObjectW.argtypes = (ctypes.c_void_p, wintypes.LPCWSTR)
        kernel32.CreateJobObjectW.restype = wintypes.HANDLE
        kernel32.SetInformationJobObject.argtypes = (
            wintypes.HANDLE,
            ctypes.c_int,
            ctypes.c_void_p,
            wintypes.DWORD,
        )
        kernel32.SetInformationJobObject.restype = wintypes.BOOL
        kernel32.AssignProcessToJobObject.argtypes = (wintypes.HANDLE, wintypes.HANDLE)
        kernel32.AssignProcessToJobObject.restype = wintypes.BOOL
        kernel32.TerminateJobObject.argtypes = (wintypes.HANDLE, wintypes.UINT)
        kernel32.TerminateJobObject.restype = wintypes.BOOL
        kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
        kernel32.CloseHandle.restype = wintypes.BOOL

        job = kernel32.CreateJobObjectW(None, None)
        if not job:
            raise OSError(ctypes.get_last_error(), "CreateJobObjectW failed")
        information = JobObjectExtendedLimitInformation()
        information.BasicLimitInformation.LimitFlags = 0x00002000  # KILL_ON_JOB_CLOSE
        if not kernel32.SetInformationJobObject(
            job,
            9,  # JobObjectExtendedLimitInformation
            ctypes.byref(information),
            ctypes.sizeof(information),
        ):
            error = ctypes.get_last_error()
            kernel32.CloseHandle(job)
            raise OSError(error, "SetInformationJobObject failed")
        if not kernel32.AssignProcessToJobObject(job, wintypes.HANDLE(int(process._handle))):
            error = ctypes.get_last_error()
            # A very short-lived child can exit in the narrow interval between CreateProcess
            # and AssignProcessToJobObject. Windows reports ERROR_ACCESS_DENIED for an exited
            # process handle. That is already a bounded terminal state, so do not turn a
            # successful fast command into a verifier failure. A still-running process must
            # still be job-bound; otherwise fail closed and let the caller tear it down.
            if error == 5 and process.poll() is not None:  # ERROR_ACCESS_DENIED
                kernel32.CloseHandle(job)
                return
            kernel32.CloseHandle(job)
            raise OSError(error, "AssignProcessToJobObject failed")
        self._handle = int(job)
        self._kernel32 = kernel32

    def terminate(self) -> None:
        if self._handle is not None:
            self._kernel32.TerminateJobObject(self._handle, 1)

    def close(self) -> None:
        if self._handle is not None:
            self._kernel32.CloseHandle(self._handle)
            self._handle = None


def stream_reader(
    source: BinaryIO,
    destination: BinaryIO | None,
    mirror: BinaryIO | None,
    maximum_bytes: int,
    state: StreamState,
    cap_event: threading.Event,
) -> None:
    try:
        read_available = getattr(source, "read1", source.read)
        while True:
            block = read_available(READ_BLOCK_BYTES)
            if not block:
                break
            state.count += len(block)
            if state.count > maximum_bytes:
                state.exceeded = True
                cap_event.set()
                break
            if destination is not None:
                destination.write(block)
                destination.flush()
            if mirror is not None:
                mirror.write(block)
                mirror.flush()
    except BaseException as exc:  # preserve reader failure for the controlling thread
        state.failure = exc
        cap_event.set()


def posix_process_group_exists(group_id: int) -> bool:
    try:
        os.killpg(group_id, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


def terminate_tree(
    process: subprocess.Popen[bytes],
    windows_job: WindowsJob | None = None,
) -> None:
    if os.name == "nt":
        if windows_job is not None:
            windows_job.terminate()
        else:
            try:
                subprocess.run(
                    ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    timeout=TERMINATION_GRACE_SECONDS,
                    check=False,
                )
            except (OSError, subprocess.SubprocessError):
                if process.poll() is None:
                    try:
                        process.kill()
                    except OSError:
                        pass
        try:
            process.wait(timeout=TERMINATION_GRACE_SECONDS)
        except subprocess.TimeoutExpired:
            try:
                process.kill()
            except OSError:
                pass
        return

    group_id = process.pid
    try:
        os.killpg(group_id, signal.SIGTERM)
    except ProcessLookupError:
        return
    except OSError:
        if process.poll() is None:
            try:
                process.terminate()
            except OSError:
                pass

    deadline = time.monotonic() + TERMINATION_GRACE_SECONDS
    while posix_process_group_exists(group_id) and time.monotonic() < deadline:
        time.sleep(0.05)
    if posix_process_group_exists(group_id):
        try:
            os.killpg(group_id, signal.SIGKILL)
        except ProcessLookupError:
            pass
    if process.poll() is None:
        try:
            process.wait(timeout=TERMINATION_GRACE_SECONDS)
        except subprocess.TimeoutExpired:
            try:
                process.kill()
            except OSError:
                pass


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout-seconds", type=positive_float, default=DEFAULT_TIMEOUT_SECONDS)
    parser.add_argument("--max-output-bytes", type=positive_int, default=DEFAULT_MAX_OUTPUT_BYTES)
    parser.add_argument("--stdout-file", type=pathlib.Path)
    parser.add_argument("--stderr-file", type=pathlib.Path)
    parser.add_argument("--tee", action="store_true")
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.command and args.command[0] == "--":
        args.command = args.command[1:]
    if not args.command:
        parser.error("a command is required after --")
    return args


def main() -> int:
    args = parse_arguments()
    try:
        stdout_path = validate_output_path(args.stdout_file, "stdout")
        stderr_path = validate_output_path(args.stderr_file, "stderr")
        if output_paths_are_same(stdout_path, stderr_path):
            raise RunnerError("stdout and stderr must use distinct output files")
    except (OSError, RunnerError) as exc:
        print(f"Bounded process runner FAILED: {exc}", file=sys.stderr)
        return 2

    stdout_file: BinaryIO | None = None
    stderr_file: BinaryIO | None = None
    process: subprocess.Popen[bytes] | None = None
    windows_job: WindowsJob | None = None
    created_outputs: list[pathlib.Path] = []
    try:
        stdout_file = open_exclusive_output(stdout_path)
        if stdout_path is not None:
            created_outputs.append(stdout_path)
        stderr_file = open_exclusive_output(stderr_path)
        if stderr_path is not None:
            created_outputs.append(stderr_path)
        process = subprocess.Popen(
            args.command,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=False,
            start_new_session=(os.name != "nt"),
            creationflags=(getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0) if os.name == "nt" else 0),
        )
        windows_job = WindowsJob(process)
        assert process.stdout is not None and process.stderr is not None
        cap_event = threading.Event()
        stdout_state = StreamState("stdout")
        stderr_state = StreamState("stderr")
        stdout_thread = threading.Thread(
            target=stream_reader,
            args=(
                process.stdout,
                stdout_file,
                sys.stdout.buffer if args.tee else None,
                args.max_output_bytes,
                stdout_state,
                cap_event,
            ),
            name="bounded-stdout",
            daemon=True,
        )
        stderr_thread = threading.Thread(
            target=stream_reader,
            args=(
                process.stderr,
                stderr_file,
                sys.stderr.buffer if args.tee else None,
                args.max_output_bytes,
                stderr_state,
                cap_event,
            ),
            name="bounded-stderr",
            daemon=True,
        )
        stdout_thread.start()
        stderr_thread.start()

        deadline = time.monotonic() + args.timeout_seconds
        timed_out = False
        while process.poll() is None:
            if cap_event.wait(timeout=min(0.1, max(0.0, deadline - time.monotonic()))):
                break
            if time.monotonic() >= deadline:
                timed_out = True
                break

        if timed_out or cap_event.is_set():
            terminate_tree(process, windows_job)
        else:
            process.wait()

        stdout_thread.join(timeout=TERMINATION_GRACE_SECONDS)
        stderr_thread.join(timeout=TERMINATION_GRACE_SECONDS)
        if stdout_thread.is_alive() or stderr_thread.is_alive():
            terminate_tree(process, windows_job)
            stdout_thread.join(timeout=TERMINATION_GRACE_SECONDS)
            stderr_thread.join(timeout=TERMINATION_GRACE_SECONDS)
            print("Bounded process runner FAILED: output reader did not terminate", file=sys.stderr)
            return 126
        reader_failure = stdout_state.failure or stderr_state.failure
        if reader_failure is not None:
            print(f"Bounded process runner FAILED: output reader error: {reader_failure}", file=sys.stderr)
            return 126
        if timed_out:
            print(
                f"Bounded process runner FAILED: command exceeded {args.timeout_seconds:g} seconds",
                file=sys.stderr,
            )
            return 124
        if stdout_state.exceeded or stderr_state.exceeded:
            labels = ", ".join(
                state.label for state in (stdout_state, stderr_state) if state.exceeded
            )
            print(
                f"Bounded process runner FAILED: {labels} exceeded {args.max_output_bytes} bytes",
                file=sys.stderr,
            )
            return 125
        return int(process.returncode or 0)
    except (OSError, subprocess.SubprocessError) as exc:
        if process is not None:
            terminate_tree(process, windows_job)
        else:
            for stream in (stdout_file, stderr_file):
                if stream is not None and not stream.closed:
                    stream.close()
            stdout_file = None
            stderr_file = None
            for created_output in created_outputs:
                try:
                    created_output.unlink()
                except FileNotFoundError:
                    pass
        print(f"Bounded process runner FAILED: {exc}", file=sys.stderr)
        return 127
    finally:
        if windows_job is not None:
            windows_job.close()
        if stdout_file is not None:
            stdout_file.close()
        if stderr_file is not None:
            stderr_file.close()


if __name__ == "__main__":
    raise SystemExit(main())
