/*
 * unitree_net_native — Cyclone DDS shim implementation.
 *
 * The interesting part of this file is the raw-CDR path. Cyclone's ordinary dds_write / dds_take
 * marshal to and from generated C structs, which would mean duplicating every Unitree message layout
 * here. Instead we use the serdata API: ddsi_serdata_from_ser_iov to wrap an already-encoded payload
 * for writing, and dds_takecdr to obtain samples still in their serialised form. The managed side owns
 * all encoding, so message layout lives in exactly one place.
 */

#define UNITREE_NET_NATIVE_EXPORTS

#include "unitree_net_native.h"

#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <vector>

#include "dds/dds.h"
#include "dds/ddsi/ddsi_serdata.h"
#include "dds/ddsi/ddsi_sertype.h"

/*
 * Generated type descriptors.
 *
 * These headers come from running idlc over the IDL shipped with unitree_sdk2; see
 * native/README.md. Each generated header declares a descriptor symbol such as
 * unitree_go_msg_dds__LowCmd__desc.
 */
#include "unitree_go/msg/LowCmd.h"
#include "unitree_go/msg/LowState.h"
#include "unitree_go/msg/SportModeState.h"
#include "unitree_go/msg/WirelessController.h"
#include "unitree_hg/msg/LowCmd.h"
#include "unitree_hg/msg/LowState.h"
#include "unitree_api/msg/Request.h"
#include "unitree_api/msg/Response.h"

namespace {

constexpr const char* kVersion = "unitree_net_native 0.1.0";

/* Per-thread error text so concurrent failures do not overwrite each other. */
thread_local std::string g_last_error;

void set_error(const std::string& message)
{
    g_last_error = message;
}

void clear_error()
{
    g_last_error.clear();
}

struct Endpoint
{
    dds_entity_t entity = 0;
    dds_entity_t topic = 0;
    bool is_reader = false;
    std::string topic_name;
    un_message_callback callback = nullptr;
    void* user_data = nullptr;
};

std::mutex g_mutex;
dds_entity_t g_participant = 0;
bool g_initialised = false;
int32_t g_next_handle = 1;
std::map<int32_t, Endpoint*> g_endpoints;

/*
 * Maps the IDL type names used on the managed side to the generated Cyclone descriptors.
 *
 * Adding a message type means adding its generated header above and one line here. The managed
 * CycloneDdsTransport.ResolveTypeName must be kept in step, otherwise a topic resolves to a name this
 * table does not know and endpoint creation fails with UN_UNKNOWN_TYPE.
 */
const dds_topic_descriptor_t* find_descriptor(const std::string& type_name)
{
    static const std::map<std::string, const dds_topic_descriptor_t*> kDescriptors = {
        {"unitree_go::msg::dds_::LowCmd_",             &unitree_go_msg_dds__LowCmd__desc},
        {"unitree_go::msg::dds_::LowState_",           &unitree_go_msg_dds__LowState__desc},
        {"unitree_go::msg::dds_::SportModeState_",     &unitree_go_msg_dds__SportModeState__desc},
        {"unitree_go::msg::dds_::WirelessController_", &unitree_go_msg_dds__WirelessController__desc},
        {"unitree_hg::msg::dds_::LowCmd_",             &unitree_hg_msg_dds__LowCmd__desc},
        {"unitree_hg::msg::dds_::LowState_",           &unitree_hg_msg_dds__LowState__desc},
        {"unitree_api::msg::dds_::Request_",           &unitree_api_msg_dds__Request__desc},
        {"unitree_api::msg::dds_::Response_",          &unitree_api_msg_dds__Response__desc},
    };

    auto it = kDescriptors.find(type_name);
    return it == kDescriptors.end() ? nullptr : it->second;
}

/*
 * Builds the Cyclone configuration XML.
 *
 * Restricting the interface matters more than it looks: with several NICs up, Cyclone will happily
 * pick the corporate LAN, where multicast is usually filtered, and discovery then silently never
 * completes.
 */
std::string build_config(const char* network_interface)
{
    if (network_interface == nullptr || network_interface[0] == '\0') {
        return std::string();
    }

    std::string config = "<CycloneDDS><Domain><General><Interfaces>";
    config += "<NetworkInterface name=\"";
    config += network_interface;
    config += "\" priority=\"default\" multicast=\"default\"/>";
    config += "</Interfaces></General></Domain></CycloneDDS>";
    return config;
}

/* Cyclone data-available listener: takes samples in serialised form and forwards them. */
void on_data_available(dds_entity_t reader, void* arg)
{
    Endpoint* endpoint = static_cast<Endpoint*>(arg);

    if (endpoint == nullptr || endpoint->callback == nullptr) {
        return;
    }

    struct ddsi_serdata* samples[8];
    dds_sample_info_t infos[8];

    while (true) {
        int32_t taken = dds_takecdr(reader, samples, 8, infos, DDS_ANY_STATE);

        if (taken <= 0) {
            return;
        }

        for (int32_t i = 0; i < taken; ++i) {
            /* Disposal and unregister notifications carry no payload. */
            if (infos[i].valid_data) {
                ddsrt_iovec_t iov[8];
                uint32_t iov_count = 8;
                struct ddsi_serdata* sd = samples[i];

                ddsi_serdata_to_ser_ref(sd, 0, ddsi_serdata_size(sd), iov, &iov_count);

                /*
                 * Cyclone may hand back the payload in several segments. Coalescing into one
                 * contiguous buffer keeps the managed callback contract simple — it receives a single
                 * span it can decode directly.
                 */
                size_t total = 0;
                for (uint32_t s = 0; s < iov_count; ++s) {
                    total += iov[s].iov_len;
                }

                std::vector<uint8_t> buffer;
                buffer.reserve(total);
                for (uint32_t s = 0; s < iov_count; ++s) {
                    const uint8_t* base = static_cast<const uint8_t*>(iov[s].iov_base);
                    buffer.insert(buffer.end(), base, base + iov[s].iov_len);
                }

                ddsi_serdata_to_ser_unref(sd, iov);

                endpoint->callback(endpoint->topic_name.c_str(),
                                   buffer.data(),
                                   static_cast<int32_t>(buffer.size()),
                                   endpoint->user_data);
            }

            ddsi_serdata_unref(samples[i]);
        }
    }
}

/* Creates, or reuses, a topic entity for the given name and descriptor. */
dds_entity_t create_topic(const char* topic_name, const dds_topic_descriptor_t* descriptor)
{
    dds_entity_t topic = dds_create_topic(g_participant, descriptor, topic_name, nullptr, nullptr);

    if (topic < 0) {
        set_error(std::string("dds_create_topic failed: ") + dds_strretcode(-topic));
    }

    return topic;
}

/*
 * QoS matching Unitree firmware.
 *
 * Best-effort with keep-last-1 is what the robot uses for the high-rate control and state topics.
 * Requesting reliable delivery here would leave the reader unmatched and produce a link that appears
 * connected but never receives anything.
 */
dds_qos_t* create_stream_qos()
{
    dds_qos_t* qos = dds_create_qos();
    dds_qset_reliability(qos, DDS_RELIABILITY_BEST_EFFORT, DDS_MSECS(100));
    dds_qset_history(qos, DDS_HISTORY_KEEP_LAST, 1);
    dds_qset_durability(qos, DDS_DURABILITY_VOLATILE);
    return qos;
}

/* Request/response topics are low-rate and must not lose messages, so they get reliable QoS. */
dds_qos_t* create_service_qos()
{
    dds_qos_t* qos = dds_create_qos();
    dds_qset_reliability(qos, DDS_RELIABILITY_RELIABLE, DDS_SECS(1));
    dds_qset_history(qos, DDS_HISTORY_KEEP_LAST, 16);
    dds_qset_durability(qos, DDS_DURABILITY_VOLATILE);
    return qos;
}

bool is_service_topic(const std::string& topic)
{
    return topic.size() >= 8 &&
           (topic.rfind("/request") == topic.size() - 8 || topic.rfind("/response") == topic.size() - 9);
}

}  // namespace

extern "C" {

int32_t UN_CALL un_init(int32_t domain_id, const char* network_interface)
{
    std::lock_guard<std::mutex> guard(g_mutex);
    clear_error();

    if (g_initialised) {
        return UN_OK;
    }

    std::string config = build_config(network_interface);

    if (!config.empty()) {
        /*
         * A domain created from an explicit config must be torn down with dds_delete on the domain
         * entity; we keep it implicit by letting the participant own the lifetime and calling
         * dds_delete(g_participant) in un_shutdown, which cascades.
         */
        dds_entity_t domain = dds_create_domain(static_cast<dds_domainid_t>(domain_id), config.c_str());

        if (domain < 0 && domain != DDS_RETCODE_PRECONDITION_NOT_MET) {
            set_error(std::string("dds_create_domain failed: ") + dds_strretcode(-domain));
            return UN_DDS_ERROR;
        }
    }

    g_participant = dds_create_participant(static_cast<dds_domainid_t>(domain_id), nullptr, nullptr);

    if (g_participant < 0) {
        set_error(std::string("dds_create_participant failed: ") + dds_strretcode(-g_participant));
        g_participant = 0;
        return UN_DDS_ERROR;
    }

    g_initialised = true;
    return UN_OK;
}

int32_t UN_CALL un_shutdown(void)
{
    std::lock_guard<std::mutex> guard(g_mutex);
    clear_error();

    if (!g_initialised) {
        return UN_OK;
    }

    for (auto& entry : g_endpoints) {
        delete entry.second;
    }
    g_endpoints.clear();

    if (g_participant > 0) {
        dds_delete(g_participant);
        g_participant = 0;
    }

    g_initialised = false;
    return UN_OK;
}

int32_t UN_CALL un_create_writer(const char* topic, const char* type_name, int32_t* out_handle)
{
    std::lock_guard<std::mutex> guard(g_mutex);
    clear_error();

    if (topic == nullptr || type_name == nullptr || out_handle == nullptr) {
        set_error("topic, type_name and out_handle must all be non-null");
        return UN_INVALID_ARGUMENT;
    }

    if (!g_initialised) {
        set_error("un_init has not been called");
        return UN_NOT_INITIALISED;
    }

    const dds_topic_descriptor_t* descriptor = find_descriptor(type_name);

    if (descriptor == nullptr) {
        set_error(std::string("no descriptor registered for type '") + type_name + "'");
        return UN_UNKNOWN_TYPE;
    }

    dds_entity_t topic_entity = create_topic(topic, descriptor);

    if (topic_entity < 0) {
        return UN_DDS_ERROR;
    }

    dds_qos_t* qos = is_service_topic(topic) ? create_service_qos() : create_stream_qos();
    dds_entity_t writer = dds_create_writer(g_participant, topic_entity, qos, nullptr);
    dds_delete_qos(qos);

    if (writer < 0) {
        set_error(std::string("dds_create_writer failed: ") + dds_strretcode(-writer));
        return UN_DDS_ERROR;
    }

    Endpoint* endpoint = new (std::nothrow) Endpoint();

    if (endpoint == nullptr) {
        dds_delete(writer);
        return UN_OUT_OF_MEMORY;
    }

    endpoint->entity = writer;
    endpoint->topic = topic_entity;
    endpoint->is_reader = false;
    endpoint->topic_name = topic;

    int32_t handle = g_next_handle++;
    g_endpoints[handle] = endpoint;
    *out_handle = handle;
    return UN_OK;
}

int32_t UN_CALL un_write(int32_t handle, const uint8_t* data, int32_t length)
{
    Endpoint* endpoint = nullptr;
    dds_entity_t writer = 0;

    {
        std::lock_guard<std::mutex> guard(g_mutex);

        if (!g_initialised) {
            set_error("un_init has not been called");
            return UN_NOT_INITIALISED;
        }

        auto it = g_endpoints.find(handle);

        if (it == g_endpoints.end() || it->second->is_reader) {
            set_error("unknown writer handle");
            return UN_UNKNOWN_HANDLE;
        }

        endpoint = it->second;
        writer = endpoint->entity;
    }

    if (data == nullptr || length <= 0) {
        set_error("data must be non-null and length positive");
        return UN_INVALID_ARGUMENT;
    }

    /*
     * Wrap the caller's already-encoded CDR without copying, then hand it to Cyclone. The sertype is
     * taken from the writer's topic so the payload is published under exactly the type the robot
     * expects.
     */
    struct ddsi_sertype* sertype = nullptr;

    if (dds_get_entity_sertype(writer, &sertype) != DDS_RETCODE_OK || sertype == nullptr) {
        set_error("could not resolve the writer's sertype");
        return UN_DDS_ERROR;
    }

    ddsrt_iovec_t iov;
    iov.iov_base = const_cast<uint8_t*>(data);
    iov.iov_len = static_cast<ddsrt_iov_len_t>(length);

    struct ddsi_serdata* serdata =
        ddsi_serdata_from_ser_iov(sertype, SDK_DATA, 1, &iov, static_cast<size_t>(length));

    if (serdata == nullptr) {
        set_error("ddsi_serdata_from_ser_iov returned null");
        return UN_DDS_ERROR;
    }

    dds_return_t rc = dds_writecdr(writer, serdata);

    if (rc != DDS_RETCODE_OK) {
        set_error(std::string("dds_writecdr failed: ") + dds_strretcode(-rc));
        return UN_DDS_ERROR;
    }

    return UN_OK;
}

int32_t UN_CALL un_create_reader(const char* topic,
                                 const char* type_name,
                                 un_message_callback callback,
                                 void* user_data,
                                 int32_t* out_handle)
{
    std::lock_guard<std::mutex> guard(g_mutex);
    clear_error();

    if (topic == nullptr || type_name == nullptr || callback == nullptr || out_handle == nullptr) {
        set_error("topic, type_name, callback and out_handle must all be non-null");
        return UN_INVALID_ARGUMENT;
    }

    if (!g_initialised) {
        set_error("un_init has not been called");
        return UN_NOT_INITIALISED;
    }

    const dds_topic_descriptor_t* descriptor = find_descriptor(type_name);

    if (descriptor == nullptr) {
        set_error(std::string("no descriptor registered for type '") + type_name + "'");
        return UN_UNKNOWN_TYPE;
    }

    dds_entity_t topic_entity = create_topic(topic, descriptor);

    if (topic_entity < 0) {
        return UN_DDS_ERROR;
    }

    Endpoint* endpoint = new (std::nothrow) Endpoint();

    if (endpoint == nullptr) {
        return UN_OUT_OF_MEMORY;
    }

    endpoint->is_reader = true;
    endpoint->topic = topic_entity;
    endpoint->topic_name = topic;
    endpoint->callback = callback;
    endpoint->user_data = user_data;

    /* The listener carries the endpoint so the callback can recover its topic and user data. */
    dds_listener_t* listener = dds_create_listener(endpoint);
    dds_lset_data_available_arg(listener, on_data_available, endpoint, false);

    dds_qos_t* qos = is_service_topic(topic) ? create_service_qos() : create_stream_qos();
    dds_entity_t reader = dds_create_reader(g_participant, topic_entity, qos, listener);
    dds_delete_qos(qos);
    dds_delete_listener(listener);

    if (reader < 0) {
        set_error(std::string("dds_create_reader failed: ") + dds_strretcode(-reader));
        delete endpoint;
        return UN_DDS_ERROR;
    }

    endpoint->entity = reader;

    int32_t handle = g_next_handle++;
    g_endpoints[handle] = endpoint;
    *out_handle = handle;
    return UN_OK;
}

int32_t UN_CALL un_destroy_endpoint(int32_t handle)
{
    std::lock_guard<std::mutex> guard(g_mutex);
    clear_error();

    auto it = g_endpoints.find(handle);

    if (it == g_endpoints.end()) {
        set_error("unknown endpoint handle");
        return UN_UNKNOWN_HANDLE;
    }

    Endpoint* endpoint = it->second;
    g_endpoints.erase(it);

    if (endpoint->entity > 0) {
        dds_delete(endpoint->entity);
    }

    delete endpoint;
    return UN_OK;
}

const char* UN_CALL un_last_error(void)
{
    return g_last_error.c_str();
}

const char* UN_CALL un_version(void)
{
    return kVersion;
}

}  // extern "C"
