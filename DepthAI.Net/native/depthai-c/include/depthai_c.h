/*
 * depthai-c — C ABI tipis di atas depthai-core (C++) untuk DepthAI.Net.
 *
 * Kontrak ini dicerminkan persis oleh src/DepthAI.Net.Core/Interop/NativeMethods.cs.
 * Mengubah struct atau tanda tangan di sini wajib diikuti perubahan di sana, dan
 * sebaliknya: P/Invoke tidak memeriksa layout saat runtime.
 *
 * Semua fungsi mengembalikan dai_status_t (0 = sukses). Detail kegagalan terakhir per
 * thread bisa dibaca lewat dai_last_error.
 */
#ifndef DEPTHAI_C_H
#define DEPTHAI_C_H

#include <stdint.h>

#ifdef _WIN32
#define DAI_API __declspec(dllexport)
#else
#define DAI_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    DAI_OK = 0,
    DAI_INVALID_ARGUMENT = -1,
    DAI_DEVICE_NOT_FOUND = -2,
    DAI_DEVICE_BUSY = -3,
    DAI_TIMEOUT = -4,
    DAI_PIPELINE_ERROR = -5,
    DAI_COMMUNICATION_ERROR = -6,
    DAI_NOT_SUPPORTED = -7,
    DAI_UNKNOWN = -100
} dai_status_t;

typedef enum {
    DAI_PACKET_IMAGE = 0,
    DAI_PACKET_DEPTH = 1,
    DAI_PACKET_NEURAL_TENSORS = 2,
    DAI_PACKET_ENCODED = 3,
    DAI_PACKET_IMU = 4
} dai_packet_type_t;

/* Nilai sama dengan enum PixelFormat di DepthAI.Streaming. */
typedef enum {
    DAI_PIXEL_UNKNOWN = 0,
    DAI_PIXEL_GRAY8 = 1,
    DAI_PIXEL_BGR888 = 2,
    DAI_PIXEL_RGB888 = 3,
    DAI_PIXEL_BGRA8888 = 4,
    DAI_PIXEL_RGBA8888 = 5,
    DAI_PIXEL_NV12 = 6,
    DAI_PIXEL_JPEG = 7
} dai_pixel_format_t;

typedef struct {
    char mx_id[32];
    char name[64];
    char connection_path[128];
    char firmware_version[32];
    int32_t protocol;  /* DeviceProtocol: 0 unknown, 1 USB, 2 PoE */
    int32_t state;     /* DeviceState: 1 available, 2 in use, 3 booting, 4 booted */
    int32_t usb_speed; /* UsbSpeed, urutan sama dengan dai::UsbSpeed */
} dai_device_info_t;

/* Hanya bisa diisi setelah perangkat dibuka: enumerasi XLink belum tahu sensor dan produk. */
typedef struct {
    int32_t color_camera_count;
    int32_t mono_camera_count;
    int32_t supports_stereo_depth;
    int32_t has_imu;
    int32_t shave_cores;
    int32_t usb_speed;        /* kecepatan link yang benar-benar dinegosiasikan */
    char product_name[64];    /* mis. "OAK-1", "OAK-D-PRO"; kosong bila EEPROM tidak mengisinya */
    char sensor_names[8][32]; /* indeks = socket CAM_A..CAM_H; kosong bila tidak terpasang */
} dai_capabilities_t;

typedef struct {
    int32_t max_usb_speed;   /* 0 = bawaan depthai (SUPER) */
    int32_t boot_timeout_ms; /* 0 = bawaan depthai */
    const char* firmware_path;
} dai_open_options_t;

/*
 * Satu paket dari perangkat. data tetap valid sampai dai_packet_release dipanggil
 * dengan native_handle-nya.
 *
 * Payload DAI_PACKET_NEURAL_TENSORS (little-endian):
 *   int32 count, lalu per tensor: int32 name_len, name (UTF-8), int32 rank,
 *   int32 dims[rank], float32 data[prod(dims)]
 *
 * Payload DAI_PACKET_IMU: float32[11] = accel xyz (m/s²), gyro xyz (rad/s),
 *   rotation i j k real, has_rotation (0 atau 1)
 */
typedef struct {
    char stream_name[64];
    int32_t type;
    const uint8_t* data;
    int64_t data_length;
    int32_t width;
    int32_t height;
    int32_t pixel_format;
    int64_t sequence_number;
    int64_t timestamp_nanos;
    void* native_handle;
} dai_packet_t;

typedef struct {
    float chip_temperature_celsius;
    float leon_css_usage_percent;
    float leon_mss_usage_percent;
    int64_t ddr_used_bytes;
    int64_t ddr_total_bytes;
} dai_telemetry_t;

/* Mengembalikan panjang string yang ditulis (tanpa NUL), atau <= 0 bila gagal. */
DAI_API int dai_get_version(char* buffer, int length);
DAI_API int dai_last_error(char* buffer, int length);

DAI_API int dai_device_list(dai_device_info_t* buffer, int capacity, int* count);
DAI_API int dai_device_open(const char* mx_id, const dai_open_options_t* options, void** handle);
DAI_API int dai_device_close(void* handle);
DAI_API int dai_device_capabilities(void* handle, dai_capabilities_t* out);

DAI_API int dai_device_upload_model(void* handle, const char* name, const uint8_t* data, int64_t length);
DAI_API int dai_device_start_pipeline(void* handle, const char* pipeline_json);
DAI_API int dai_device_stop_pipeline(void* handle);

/*
 * Mengirim satu gambar dari host ke node "HostInput" di pipeline yang sedang berjalan.
 * data berisi piksel interleaved (GRAY8, BGR888, atau RGB888) rapat tanpa padding baris;
 * shim mengubahnya ke planar bila tujuannya neural network. Data disalin sebelum fungsi
 * kembali, jadi buffer pemanggil boleh langsung dipakai ulang.
 */
DAI_API int dai_device_send_frame(void* handle, const char* input_name, int32_t width, int32_t height, int32_t pixel_format,
                                  const uint8_t* data, int64_t length, int64_t sequence_number);

DAI_API int dai_device_poll(void* handle, dai_packet_t* out, int timeout_ms);
DAI_API int dai_packet_release(void* handle, void* native_handle);

DAI_API int dai_device_telemetry(void* handle, dai_telemetry_t* out);

#ifdef __cplusplus
}
#endif

#endif /* DEPTHAI_C_H */
