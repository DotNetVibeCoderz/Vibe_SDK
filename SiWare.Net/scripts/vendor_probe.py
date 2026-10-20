"""Inspect the installed CPython binding without creating a driver or issuing commands."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inspect", action="store_true", required=True)
    parser.add_argument("--driver", type=Path, default=Path(
        r"C:\Program Files (x86)\Si-Spect Platform\Analyze\pyDriver.pyd"))
    args = parser.parse_args()
    if sys.version_info[:2] != (3, 10) or sys.maxsize <= 2**32:
        parser.error("The installed module requires CPython 3.10 x64.")
    path = args.driver.resolve(strict=True)
    with os.add_dll_directory(str(path.parent)):
        spec = importlib.util.spec_from_file_location("pyDriver", path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        driver = module.py_driver
        methods = ("__init__", "set_on_connected", "set_on_disconnected",
                   "run_psd", "run_absorbance", "run_background", "read_background",
                   "run_interferogram", "get_sensor_id", "read_fw_version", "read_temperature")
        enums = ("CommonWaveNumber", "OpticalGainSelection", "ApodizationSelection",
                 "ZeroPaddingSelection", "DeviceType")
        result = {
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "python": sys.version.split()[0],
            "driverCreated": False,
            "methods": {name: getattr(driver, name).__doc__ for name in methods},
            "enums": {name: {key: value.value for key, value in
                      getattr(driver, name).__members__.items()} for name in enums},
            "spectrumProperties": {name: getattr(module.RunPSdResponseElements, name).__doc__
                                   for name in ("psd", "wvn")},
            "errorValue": module.Error.value.__doc__,
        }
        print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
