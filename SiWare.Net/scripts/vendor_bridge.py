"""JSON-lines worker for the locally installed Si-Ware CPython 3.10 binding."""
import argparse
import importlib.util
import json
import math
import os
from pathlib import Path
import sys
import threading
import time

MAX_LINE = 1024 * 1024
POINTS = (65, 129, 257, 513, 1024, 2048, 4096)


def integer(value, name, low, high):
    if type(value) is not int or not low <= value <= high:
        raise ValueError(f"{name} must be an integer between {low} and {high}")
    return value


class Bridge:
    def __init__(self, module, options):
        self.module = module
        self.options = options
        self.devices = set()
        self.condition = threading.Condition()
        self.driver = module.py_driver()
        self.driver.set_on_connected(self.connected)
        self.driver.set_on_disconnected(self.disconnected)

    def connected(self, device_id):
        with self.condition:
            self.devices.add(device_id)
            self.condition.notify_all()

    def disconnected(self, device_id):
        with self.condition:
            self.devices.discard(device_id)

    def discover(self):
        # A USB sensor announces itself ~6 s after the driver opens; returns as soon as one does.
        deadline = time.monotonic() + 20
        with self.condition:
            while not self.devices and time.monotonic() < deadline:
                self.condition.wait(max(0, deadline - time.monotonic()))
            return [{"id": n, "name": f"Si-Ware sensor {n}"} for n in sorted(self.devices)]

    def acquire(self, request):
        kind = request.get("kind", "Sample")
        if kind not in ("Sample", "Reference"):
            raise ValueError("Only Sample and Reference are supported by this vendor bridge")
        points = request.get("points", 257)
        if type(points) is not int or points not in POINTS:
            raise ValueError("Unsupported common wavenumber point count")
        integration = integer(request.get("integrationMilliseconds", 100), "integrationMilliseconds", 1, 600000)
        averages = integer(request.get("averages", 1), "averages", 1, 100)
        device_id = request.get("deviceId")
        if device_id is None:
            devices = self.discover()
            if not devices:
                raise ValueError("No Si-Ware devices discovered")
            device_id = devices[0]["id"]
        integer(device_id, "deviceId", 0, 2**63 - 1)
        with self.condition:
            if device_id not in self.devices:
                raise ValueError("Requested device is not connected")
        c = self.module.py_driver
        common = getattr(c.CommonWaveNumber, f"points_{points}")
        scan_time = integration if self.options.scan_time_unit == "milliseconds" else integration / 1000.0
        arguments = ([device_id], float(scan_time), common,
                     c.OpticalGainSelection.internal, c.ApodizationSelection.boxcar,
                     c.ZeroPaddingSelection.points_zero, self.options.mode)
        total = None
        axis = None
        for _ in range(averages):
            if kind == "Reference":
                self.check(self.driver.run_background(*arguments)[device_id])
                error, spectrum = self.driver.read_background([device_id], common)[device_id]
            else:
                error, spectrum = self.driver.run_psd(*arguments)[device_id]
            self.check(error)
            if spectrum is None:
                raise ValueError("Vendor returned an empty spectrum")
            current_axis, values = list(spectrum.wvn), list(spectrum.psd)
            if len(values) != points or len(current_axis) != points:
                raise ValueError("Vendor spectrum has an unexpected point count")
            if not all(math.isfinite(x) for x in current_axis + values) or any(x <= 0 for x in current_axis):
                raise ValueError("Vendor spectrum contains invalid numeric values")
            if axis is not None and current_axis != axis:
                raise ValueError("Vendor wavenumber axis changed between averages")
            axis = current_axis
            total = values if total is None else [a + b for a, b in zip(total, values)]
        values = [x / averages for x in total]
        if not all(math.isfinite(x) for x in values):
            raise ValueError("Averaging overflowed")
        return {"deviceId": str(device_id), "wavenumbers": axis, "values": values,
                "kind": kind, "wavenumberUnit": "cm-1"}

    @staticmethod
    def check(error):
        code = error.value()
        if code != 0:
            raise ValueError(f"Vendor operation failed with error code {code}")


def dispatch(bridge, request):
    if not isinstance(request, dict):
        raise ValueError("Request must be a JSON object")
    integer(request.get("id"), "id", 0, 2**53 - 1)
    if request.get("op") == "discover":
        return bridge.discover()
    if request.get("op") == "acquire":
        return bridge.acquire(request)
    raise ValueError("Unknown operation")


def self_test():
    class Fake:
        def discover(self):
            return [{"id": 7, "name": "Test sensor"}]
        def acquire(self, request):
            return {"kind": request["kind"]}
    assert dispatch(Fake(), {"id": 1, "op": "discover"})[0]["id"] == 7
    assert dispatch(Fake(), {"id": 2, "op": "acquire", "kind": "Sample"})["kind"] == "Sample"
    for invalid in ([], {"id": True, "op": "discover"}, {"id": 0, "op": "erase"}):
        try:
            dispatch(Fake(), invalid)
        except ValueError:
            continue
        raise AssertionError("Invalid request accepted")
    print("vendor bridge protocol self-test passed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--driver", type=Path, default=Path(
        r"C:\Program Files (x86)\Si-Spect Platform\Analyze\pyDriver.pyd"))
    parser.add_argument("--wavenumber-unit", choices=("cm-1",))
    parser.add_argument("--scan-time-unit", choices=("milliseconds", "seconds"))
    parser.add_argument("--mode", type=int)
    parser.add_argument("--self-test", action="store_true")
    options = parser.parse_args()
    if options.self_test:
        self_test()
        return
    if options.wavenumber_unit is None or options.scan_time_unit is None or options.mode is None:
        parser.error("Confirm --wavenumber-unit, --scan-time-unit, and --mode against your device documentation")
    if sys.version_info[:2] != (3, 10) or sys.maxsize <= 2**32:
        parser.error("Vendor driver requires CPython 3.10 x64")
    # Preserve IPC stdout before redirecting native/Python vendor diagnostics.
    output = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", buffering=1)
    sys.stdout.flush()
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    if os.name == "nt":
        import ctypes
        import msvcrt
        ctypes.windll.kernel32.SetStdHandle(-11, ctypes.c_void_p(msvcrt.get_osfhandle(sys.stderr.fileno())))
    path = options.driver.resolve(strict=True)
    with os.add_dll_directory(str(path.parent)):
        spec = importlib.util.spec_from_file_location("pyDriver", path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        bridge = Bridge(module, options)
        while True:
            raw = sys.stdin.buffer.readline(MAX_LINE + 1)
            if not raw:
                break
            request_id = None
            try:
                if len(raw) > MAX_LINE:
                    # Close the worker to avoid parsing the remainder as another command.
                    output.write(json.dumps({"id": None, "ok": False, "error": "Request exceeds 1 MiB"}) + "\n")
                    break
                request = json.loads(raw)
                if isinstance(request, dict) and type(request.get("id")) is int:
                    request_id = request["id"]
                result = dispatch(bridge, request)
                response = {"id": request_id, "ok": True, "result": result}
            except (ValueError, KeyError, TypeError) as error:
                response = {"id": request_id, "ok": False, "error": str(error)}
            except Exception:
                response = {"id": request_id, "ok": False, "error": "Unexpected vendor operation failure"}
            output.write(json.dumps(response, allow_nan=False) + "\n")
        bridge.driver.set_on_connected(None)
        bridge.driver.set_on_disconnected(None)


if __name__ == "__main__":
    main()
