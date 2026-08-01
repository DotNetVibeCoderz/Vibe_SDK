/*
 * unitree_net_native — a thin Cyclone DDS shim for Unitree.Net.
 *
 * Purpose
 * -------
 * Unitree robots communicate over RTPS using the unitree_go / unitree_hg / unitree_api IDL types.
 * DDS requires a registered type descriptor for every topic, so managed code cannot simply publish
 * opaque bytes onto a typed topic. This shim registers the generated descriptors and exchanges
 * *pre-serialised CDR* with them, which keeps all encoding and decoding in C# where it is testable,
 * while the wire format stays exactly what the firmware expects.
 *
 * The C ABI below is deliberately small and handle-based so it is trivial to P/Invoke.
 *
 * Threading
 * ---------
 * un_init / un_shutdown are not thread-safe with respect to each other or to endpoint creation.
 * un_write is safe to call concurrently on distinct handles. Reader callbacks are invoked on Cyclone
 * DDS listener threads; the callee must not block and must not let exceptions escape.
 */

#ifndef UNITREE_NET_NATIVE_H
#define UNITREE_NET_NATIVE_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(UNITREE_NET_NATIVE_EXPORTS)
#    define UN_API __declspec(dllexport)
#  else
#    define UN_API __declspec(dllimport)
#  endif
#  define UN_CALL __cdecl
#else
#  define UN_API __attribute__((visibility("default")))
#  define UN_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Status codes. Mirrored by Unitree.Net.Interop.NativeStatus. */
typedef enum un_status
{
    UN_OK               =  0,
    UN_NOT_INITIALISED  = -1,
    UN_INVALID_ARGUMENT = -2,
    UN_UNKNOWN_TYPE     = -3,
    UN_DDS_ERROR        = -4,
    UN_UNKNOWN_HANDLE   = -5,
    UN_OUT_OF_MEMORY    = -6
} un_status;

/*
 * Delivered for every sample a reader takes.
 *
 * topic     null-terminated UTF-8 topic name
 * data      serialised CDR payload, including the 4-byte encapsulation header
 * length    payload length in bytes
 * user_data the opaque pointer passed to un_create_reader
 *
 * The buffer is owned by the shim and is only valid for the duration of the call.
 */
typedef void(UN_CALL* un_message_callback)(const char* topic,
                                           const uint8_t* data,
                                           int32_t length,
                                           void* user_data);

/*
 * Joins the DDS domain.
 *
 * network_interface may be NULL, in which case Cyclone DDS selects an interface itself. On a
 * multi-homed host that choice is effectively arbitrary, so passing the robot-facing interface
 * explicitly is strongly recommended.
 */
UN_API int32_t UN_CALL un_init(int32_t domain_id, const char* network_interface);

/* Destroys every endpoint and leaves the domain. Safe to call when not initialised. */
UN_API int32_t UN_CALL un_shutdown(void);

/* Creates a writer. type_name must be one of the registered IDL type names. */
UN_API int32_t UN_CALL un_create_writer(const char* topic, const char* type_name, int32_t* out_handle);

/* Publishes a pre-serialised CDR payload. */
UN_API int32_t UN_CALL un_write(int32_t handle, const uint8_t* data, int32_t length);

/* Creates a reader that invokes callback for each sample. */
UN_API int32_t UN_CALL un_create_reader(const char* topic,
                                        const char* type_name,
                                        un_message_callback callback,
                                        void* user_data,
                                        int32_t* out_handle);

/* Destroys a reader or writer. */
UN_API int32_t UN_CALL un_destroy_endpoint(int32_t handle);

/* Returns the most recent error message for the calling thread. Never NULL. */
UN_API const char* UN_CALL un_last_error(void);

/* Returns the shim version string. Never NULL. */
UN_API const char* UN_CALL un_version(void);

#ifdef __cplusplus
}
#endif

#endif /* UNITREE_NET_NATIVE_H */
