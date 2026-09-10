# Refreshes the vendored Reachy 2 protobuf definitions from Pollen Robotics.
#
# These files are the wire contract: field numbers have to match the robot exactly, so they are
# copied verbatim and never hand-edited. Run this when the robot firmware moves on, then rebuild and
# check nothing in PollenRobotics.Net.Reachy2 broke.
param(
    [string]$Branch = "main"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root "src\PollenRobotics.Net.Reachy2\Protos"
$base = "https://raw.githubusercontent.com/pollen-robotics/reachy2-sdk-api/$Branch"

$files = @(
    "arm", "component", "dynamixel_motor", "error", "goto", "hand", "head", "kinematics",
    "mobile_base_lidar", "mobile_base_mobility", "mobile_base_utility", "orbita2d", "orbita3d",
    "parallel_gripper", "part", "reachy", "sound", "video", "webrtc_bridge"
)

New-Item -ItemType Directory -Force -Path $target | Out-Null

foreach ($file in $files) {
    $url = "$base/protos/$file.proto"
    $out = Join-Path $target "$file.proto"

    Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing
    Write-Output "$file.proto  ($((Get-Item $out).Length) bytes)"
}

Invoke-WebRequest -Uri "$base/LICENSE" -OutFile (Join-Path $target "LICENSE-reachy2-sdk-api.txt") -UseBasicParsing

Write-Output ""
Write-Output "Synced from $Branch. Rebuild PollenRobotics.Net.Reachy2 and check for breaks."
