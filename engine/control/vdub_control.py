"""VirtualDub2 local Tool-SDK control. Windows, Python 3 standard library only.

No keyboard/mouse injection, OCR, UI Automation, web service, or remote listener.
`seek` confirms the SDK timeline position. It does not promise decoded-frame
completion: the current Tool SDK does not expose that signal.
"""
from __future__ import annotations
import argparse
import ctypes as c
from ctypes import wintypes as w
import json
import os
from pathlib import Path
import secrets
import struct
import sys
import threading
import time

MAGIC = 0x31434456
HEADER = struct.Struct('<IHHIIqII')
MAX_PAYLOAD = 32768
_k = c.WinDLL('kernel32', use_last_error=True)
INVALID_HANDLE = c.c_void_p(-1).value

class OVERLAPPED(c.Structure):
    _fields_ = [('Internal', c.c_size_t), ('InternalHigh', c.c_size_t),
                ('Offset', w.DWORD), ('OffsetHigh', w.DWORD), ('hEvent', w.HANDLE)]

_k.CreateFileW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, c.c_void_p, w.DWORD, w.DWORD, w.HANDLE]
_k.CreateFileW.restype = w.HANDLE
_k.CreateEventW.argtypes = [c.c_void_p, w.BOOL, w.BOOL, w.LPCWSTR]
_k.CreateEventW.restype = w.HANDLE
_k.CloseHandle.argtypes = [w.HANDLE]
_k.WaitNamedPipeW.argtypes = [w.LPCWSTR, w.DWORD]
_k.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]
_k.GetNamedPipeServerProcessId.argtypes = [w.HANDLE, c.POINTER(w.ULONG)]
_k.SetNamedPipeHandleState.argtypes = [w.HANDLE, c.POINTER(w.DWORD), c.c_void_p, c.c_void_p]
for _name in ['ReadFile', 'WriteFile']:
    getattr(_k, _name).argtypes = [w.HANDLE, c.c_void_p, w.DWORD, c.POINTER(w.DWORD), c.POINTER(OVERLAPPED)]
_k.GetOverlappedResult.argtypes = [w.HANDLE, c.POINTER(OVERLAPPED), c.POINTER(w.DWORD), w.BOOL]
_k.CancelIoEx.argtypes = [w.HANDLE, c.POINTER(OVERLAPPED)]

class BridgeError(RuntimeError):
    def __init__(self, response: dict):
        self.response = response
        detail = response.get('error', {})
        self.code = detail.get('code', 'unknown')
        self.may_have_applied = detail.get('may_have_applied', False)
        super().__init__(f"{self.code}: {detail.get('message', '')}")

def _io(handle: int, data: bytes | None, timeout: float) -> bytes:
    event = _k.CreateEventW(None, True, False, None)
    if not event:
        raise c.WinError(c.get_last_error())
    ov = OVERLAPPED(hEvent=event)
    size = len(data) if data is not None else 262144
    buf = c.create_string_buffer(data, size) if data is not None else c.create_string_buffer(size)
    count = w.DWORD()
    fn = _k.WriteFile if data is not None else _k.ReadFile
    try:
        ok = fn(handle, buf, size, c.byref(count), c.byref(ov))
        if not ok:
            error = c.get_last_error()
            if error != 997:
                raise c.WinError(error)
            if _k.WaitForSingleObject(event, max(1, int(timeout * 1000))) != 0:
                _k.CancelIoEx(handle, c.byref(ov))
                _k.GetOverlappedResult(handle, c.byref(ov), c.byref(count), True)
                raise TimeoutError('Pipe I/O timed out; a sent command may already have applied. Query state before retrying.')
            if not _k.GetOverlappedResult(handle, c.byref(ov), c.byref(count), False):
                raise c.WinError(c.get_last_error())
        if data is not None and count.value != size:
            raise OSError('Incomplete pipe write')
        return bytes(buf.raw[:count.value])
    finally:
        _k.CloseHandle(event)

class VirtualDubClient:
    """One explicitly selected process. Commands are serialized per client."""
    def __init__(self, pid: int, *, timeout: float = 20):
        if isinstance(pid, bool) or not isinstance(pid, int) or pid <= 0:
            raise ValueError('A positive process ID is required')
        if not 0 < timeout <= 60:
            raise ValueError('Timeout must be between 0 and 60 seconds')
        self.pid = pid
        self.timeout = timeout
        self.pipe = rf'\\.\pipe\vdub-sdk-{pid}'
        self._lock = threading.Lock()

    def _exchange(self, packet: bytes) -> dict:
        deadline = time.monotonic() + self.timeout
        with self._lock:
            while True:
                handle = _k.CreateFileW(self.pipe, 0xC0000000, 0, None, 3, 0x40000000, None)
                if handle != INVALID_HANDLE:
                    break
                error = c.get_last_error()
                if error not in (2, 231) or time.monotonic() >= deadline:
                    raise c.WinError(error)
                _k.WaitNamedPipeW(self.pipe, 100)
                time.sleep(0.01)
            try:
                server = w.ULONG()
                if not _k.GetNamedPipeServerProcessId(handle, c.byref(server)):
                    raise c.WinError(c.get_last_error())
                if server.value != self.pid:
                    raise RuntimeError('Pipe server process does not match the selected PID')
                mode = w.DWORD(2)  # message read mode
                if not _k.SetNamedPipeHandleState(handle, c.byref(mode), None, None):
                    raise c.WinError(c.get_last_error())
                remaining = lambda: max(0.001, deadline - time.monotonic())
                _io(handle, packet, remaining())
                raw = _io(handle, None, remaining())
                # Consume first, acknowledge, then let the server disconnect safely.
                _io(handle, b'\x06', remaining())
                return json.loads(raw.decode('utf-8'))
            finally:
                _k.CloseHandle(handle)

    def request(self, operation: int, argument: int = 0, flags: int = 0, payload: str = '') -> dict:
        if isinstance(argument, bool) or not isinstance(argument, int) or not -(1 << 63) <= argument < (1 << 63):
            raise ValueError('Frame argument must be a signed 64-bit integer')
        encoded = payload.encode('utf-8')
        if len(encoded) > MAX_PAYLOAD:
            raise ValueError('Payload exceeds protocol limit')
        request_id = secrets.randbits(32)
        packet = HEADER.pack(MAGIC, 1, operation, request_id, len(encoded), argument, flags, 0) + encoded
        response = self._exchange(packet)
        if response.get('id') != request_id:
            raise RuntimeError('Unexpected response request ID')
        if not response.get('ok'):
            raise BridgeError(response)
        if response['state']['pid'] != self.pid:
            raise RuntimeError('Unexpected host process in response')
        return response

    def status(self) -> dict:
        return self.request(1)['state']

    def open(self, path: str | Path) -> dict:
        return self.request(2, payload=str(Path(path).resolve()))['state']

    def seek(self, frame: int) -> dict:
        return self.request(3, argument=frame)['state']

    def step(self, delta: int = 1) -> dict:
        return self.request(4, argument=delta)['state']

    def view(self, mode: str = 'input', auto_fit: bool = True) -> dict:
        modes = {'input': 0, 'output': 1, 'both': 2}
        if mode not in modes:
            raise ValueError('Mode must be input, output, or both')
        return self.request(5, argument=modes[mode], flags=int(bool(auto_fit)))['state']

def list_instances(timeout: float = 2) -> list[dict]:
    result = []
    for name in os.listdir('\\\\.\\pipe\\'):
        if name.startswith('vdub-sdk-') and name.removeprefix('vdub-sdk-').isdigit():
            pid = int(name.removeprefix('vdub-sdk-'))
            try:
                result.append(VirtualDubClient(pid, timeout=timeout).status())
            except (OSError, ValueError, RuntimeError) as e:
                result.append({'pid': pid, 'unavailable': str(e)})
    return sorted(result, key=lambda x: x['pid'])

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pid', type=int, help='Explicit target process ID from list')
    parser.add_argument('--timeout', type=float, default=20)
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('list')
    commands.add_parser('status')
    p = commands.add_parser('open'); p.add_argument('path')
    p = commands.add_parser('seek'); p.add_argument('frame', type=int)
    p = commands.add_parser('step'); p.add_argument('delta', type=int)
    p = commands.add_parser('view'); p.add_argument('mode', choices=['input', 'output', 'both']); p.add_argument('--fixed', action='store_true', help='Disable auto fit')
    args = parser.parse_args()
    try:
        if args.command == 'list':
            data = list_instances()
        else:
            if args.pid is None:
                parser.error('--pid is required for a targeted command')
            client = VirtualDubClient(args.pid, timeout=args.timeout)
            if args.command == 'status': data = client.status()
            elif args.command == 'open': data = client.open(args.path)
            elif args.command == 'seek': data = client.seek(args.frame)
            elif args.command == 'step': data = client.step(args.delta)
            else: data = client.view(args.mode, not args.fixed)
        print(json.dumps({'ok': True, 'result': data}, ensure_ascii=False, indent=2))
        return 0
    except BridgeError as e:
        print(json.dumps(e.response, ensure_ascii=False, indent=2)); return 2
    except (OSError, ValueError, RuntimeError) as e:
        print(json.dumps({'ok': False, 'error': {'code': 'client_error', 'message': str(e)}}, ensure_ascii=False, indent=2)); return 3

if __name__ == '__main__':
    raise SystemExit(main())
