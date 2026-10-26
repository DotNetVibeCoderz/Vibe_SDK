SiWare.NET vendor kit — hardware test on a Windows PC where the Si-Spect app already sees the sensor
Created by Gravicode Studios, led by Kang Fadhil. Not affiliated with Si-Ware Systems.

Contents: vendor_bridge.py (JSON-lines worker), vendor_probe.py (read-only binding check),
vendor.json (config template), python310\ (unmodified embeddable CPython 3.10.11 x64 from python.org, PSF license).
The Si-Ware driver (pyDriver.pyd) is NOT included; it comes with the Si-Spect Platform installation.

1. Install Si-Spect Platform. Plug in the sensor and confirm the Si-Spect app can scan with it. Then CLOSE the app,
   because only one program should talk to the sensor at a time.
2. Unzip this kit, for example to C:\SiWare\vendor-kit, and unzip the CLI and Workbench next to it.
3. Edit vendor.json: replace C:\SiWare\vendor-kit with your folder (absolute paths). Leave scanTimeUnit "seconds".
   "mode": 0 is unverified; it is the first thing this test checks.
4. Check the binding (loads the driver's Python module, creates no driver, sends no device command):
     C:\SiWare\vendor-kit\python310\python.exe C:\SiWare\vendor-kit\vendor_probe.py --inspect
5. Discovery only (finds the sensor; reads and writes nothing on it):
     SiWare.Cli.exe discover --vendor-config C:\SiWare\vendor-kit\vendor.json
6. First measurements (reference, then sample; the vendor's typical scan time is 2 s):
     SiWare.Cli.exe series --kind reference --count 1 --integration 2000 --vendor-config C:\SiWare\vendor-kit\vendor.json --output test\reference
     SiWare.Cli.exe series --count 3 --interval 5 --integration 2000 --vendor-config C:\SiWare\vendor-kit\vendor.json --output test\sample
7. Workbench with the sensor (PowerShell):
     $env:SIWARE_VENDOR_CONFIG = "C:\SiWare\vendor-kit\vendor.json"; .\SiWare.Workbench.exe --urls http://localhost:5069
   then open http://localhost:5069.
Report: the console output of steps 4-6, the Si-Spect firmware version, and whether the axis matches 1350-2550 nm.
SiWare.NET never calls the driver's burn_*/write_*/restore_* operations, which change the sensor's stored calibration.

Bahasa Indonesia
1. Pasang Si-Spect Platform, colok sensor, dan pastikan aplikasi Si-Spect bisa memindai. Lalu TUTUP aplikasinya,
   karena hanya satu program yang boleh berkomunikasi dengan sensor dalam satu waktu.
2. Ekstrak kit ini (misalnya ke C:\SiWare\vendor-kit), lalu ekstrak juga CLI dan Workbench di sebelahnya.
3. Ubah vendor.json: ganti C:\SiWare\vendor-kit dengan folder Anda (path absolut). Biarkan scanTimeUnit "seconds";
   nilai "mode": 0 belum terverifikasi dan justru itu yang diuji di sini.
4-7. Jalankan perintah di atas secara berurutan: cek binding, penemuan sensor saja, pengukuran pertama, lalu Workbench.
Kirimkan output langkah 4-6, versi firmware dari Si-Spect, dan apakah sumbu spektrum sesuai 1350-2550 nm.
SiWare.NET tidak pernah memanggil operasi burn_*/write_*/restore_* driver, yang mengubah kalibrasi tersimpan di sensor.
