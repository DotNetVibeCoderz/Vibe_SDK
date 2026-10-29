// depthai-c: implementasi C ABI di atas depthai-core v3.
//
// Prinsip yang dijaga:
//  - Tidak ada exception yang menyeberang batas C. Setiap fungsi publik menangkap
//    semuanya, menyimpan pesan ke last error per thread, lalu mengembalikan status.
//  - Model pull: callback queue depthai berjalan di thread depthai dan hanya mengisi
//    antrean siap-ambil; dai_device_poll menariknya. Thread .NET tidak pernah dipanggil
//    balik dari native.
//  - Buffer paket dimiliki "holder" yang hidup sampai dai_packet_release, sehingga host
//    bisa menyalin tanpa takut memori didaur ulang depthai di tengah jalan.

#include "depthai_c.h"

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

#include <nlohmann/json.hpp>

#include "depthai/build/version.hpp"
#include "depthai/device/Device.hpp"
#include "depthai/nn_archive/NNArchive.hpp"
#include "depthai/openvino/OpenVINO.hpp"
#include "depthai/pipeline/InputQueue.hpp"
#include "depthai/pipeline/Pipeline.hpp"
#include "depthai/pipeline/datatype/EncodedFrame.hpp"
#include "depthai/pipeline/datatype/IMUData.hpp"
#include "depthai/pipeline/datatype/ImgFrame.hpp"
#include "depthai/pipeline/datatype/NNData.hpp"
#include "depthai/pipeline/node/Camera.hpp"
#include "depthai/pipeline/node/IMU.hpp"
#include "depthai/pipeline/node/ImageManip.hpp"
#include "depthai/pipeline/node/NeuralNetwork.hpp"
#include "depthai/pipeline/node/StereoDepth.hpp"
#include "depthai/pipeline/node/VideoEncoder.hpp"

using json = nlohmann::json;

namespace {

thread_local std::string lastError;

/// Kesalahan yang membawa status ABI spesifik, supaya pemanggil .NET bisa membedakan
/// "argumen salah" dari "perangkat tidak ada" tanpa mengurai teks.
struct DaiError : std::runtime_error {
    dai_status_t status;
    DaiError(dai_status_t s, const std::string& message) : std::runtime_error(message), status(s) {}
};

int fail(dai_status_t status, const std::string& message) {
    lastError = message;
    return status;
}

/// Membungkus badan fungsi publik: exception apa pun berubah menjadi status.
template <typename F>
int guarded(dai_status_t fallback, F&& body) {
    try {
        return body();
    } catch(const DaiError& e) {
        return fail(e.status, e.what());
    } catch(const std::exception& e) {
        return fail(fallback, e.what());
    } catch(...) {
        return fail(fallback, "exception tak dikenal dari depthai-core");
    }
}

int copyString(const std::string& value, char* buffer, int length) {
    if(buffer == nullptr || length <= 0) return 0;
    const auto n = static_cast<int>(std::min<size_t>(value.size(), static_cast<size_t>(length - 1)));
    std::memcpy(buffer, value.data(), static_cast<size_t>(n));
    buffer[n] = '\0';
    return n;
}

template <size_t N>
void copyFixed(char (&target)[N], const std::string& value) {
    const auto n = std::min(value.size(), N - 1);
    std::memcpy(target, value.data(), n);
    target[n] = '\0';
}

// ---------------------------------------------------------------------------
// Paket siap-ambil
// ---------------------------------------------------------------------------

/// Pemilik memori satu paket sampai dai_packet_release.
struct PacketHolder {
    std::shared_ptr<dai::ADatatype> message;  // menahan buffer depthai tetap hidup
    std::vector<uint8_t> buffer;              // dipakai bila data harus dikonversi
};

struct ReadyPacket {
    dai_packet_t packet{};
    PacketHolder* holder = nullptr;
};

/// Cara mengubah pesan sebuah stream menjadi paket ABI.
enum class StreamKind { Image, Depth, Tensors, Encoded, Imu };

int64_t deviceNanos(const dai::Buffer& buffer) {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(buffer.getTimestampDevice().time_since_epoch()).count();
}

/// Format piksel SDK untuk tipe ImgFrame; planar dilaporkan lewat out-parameter karena
/// harus diubah ke interleaved dulu — ImageFrame di .NET hanya mengenal interleaved.
dai_pixel_format_t pixelFormatOf(dai::ImgFrame::Type type, bool& planar) {
    using T = dai::ImgFrame::Type;
    planar = false;
    switch(type) {
        case T::BGR888i: return DAI_PIXEL_BGR888;
        case T::RGB888i: return DAI_PIXEL_RGB888;
        case T::BGR888p: planar = true; return DAI_PIXEL_BGR888;
        case T::RGB888p: planar = true; return DAI_PIXEL_RGB888;
        case T::GRAY8:
        case T::RAW8:
        case T::YUV400p: return DAI_PIXEL_GRAY8;
        case T::NV12: return DAI_PIXEL_NV12;
        case T::RGBA8888: return DAI_PIXEL_RGBA8888;
        case T::BITSTREAM: return DAI_PIXEL_JPEG;
        default: return DAI_PIXEL_UNKNOWN;
    }
}

void appendInt32(std::vector<uint8_t>& out, int32_t value) {
    const auto* bytes = reinterpret_cast<const uint8_t*>(&value);
    out.insert(out.end(), bytes, bytes + sizeof value);
}

void appendFloats(std::vector<uint8_t>& out, const float* values, size_t count) {
    const auto* bytes = reinterpret_cast<const uint8_t*>(values);
    out.insert(out.end(), bytes, bytes + count * sizeof(float));
}

/// Serialisasi tensor sesuai format di depthai_c.h. Tensor 4D dipaksa NCHW karena
/// parser .NET (YOLO grid, segmentasi) membaca kanal sebagai sumbu kedua.
std::vector<uint8_t> serializeTensors(dai::NNData& data) {
    std::vector<uint8_t> out;
    const auto names = data.getAllLayerNames();
    appendInt32(out, static_cast<int32_t>(names.size()));

    for(const auto& name : names) {
        const auto info = data.getTensorInfo(name);
        const bool fourD = info && info->dims.size() == 4;
        xt::xarray<float> tensor =
            fourD ? data.getTensor<float>(name, dai::TensorInfo::StorageOrder::NCHW, true) : data.getTensor<float>(name, true);

        appendInt32(out, static_cast<int32_t>(name.size()));
        out.insert(out.end(), name.begin(), name.end());
        appendInt32(out, static_cast<int32_t>(tensor.dimension()));
        for(auto d : tensor.shape()) appendInt32(out, static_cast<int32_t>(d));
        appendFloats(out, tensor.data(), tensor.size());
    }

    return out;
}

// ---------------------------------------------------------------------------
// Sesi perangkat
// ---------------------------------------------------------------------------

/// Antrean host-ke-perangkat milik satu node HostInput.
struct HostInput {
    std::shared_ptr<dai::InputQueue> queue;
    bool planar = false;  // tujuan neural network: model blob RVC2 mengharapkan CHW
};

struct Session {
    // Parameter buka disimpan karena depthai v3 menutup perangkat saat pipeline berhenti;
    // sesi .NET mengharapkan satu handle tetap bisa menjalankan pipeline berikutnya.
    dai::DeviceInfo info;
    dai::UsbSpeed maxUsbSpeed = dai::UsbSpeed::SUPER;
    std::optional<std::filesystem::path> firmwarePath;

    std::shared_ptr<dai::Device> device;
    std::unique_ptr<dai::Pipeline> pipeline;
    std::vector<std::shared_ptr<dai::MessageQueue>> queues;
    std::map<std::string, HostInput> inputs;
    std::map<std::string, std::vector<uint8_t>> models;
    std::vector<std::filesystem::path> tempFiles;

    std::mutex mutex;
    std::condition_variable ready;
    std::deque<ReadyPacket> packets;

    /// Batas antrean siap-ambil. Paket tertua dibuang bila host tertinggal: pipeline
    /// realtime lebih baik kehilangan frame daripada menumpuk latensi tanpa batas.
    static constexpr size_t MaxPending = 64;

    void push(ReadyPacket item) {
        {
            std::lock_guard lock(mutex);
            if(packets.size() >= MaxPending) {
                delete packets.front().holder;
                packets.pop_front();
            }
            packets.push_back(item);
        }
        ready.notify_one();
    }

    void clearPackets() {
        std::lock_guard lock(mutex);
        for(auto& p : packets) delete p.holder;
        packets.clear();
    }

    void removeTempFiles() {
        for(const auto& path : tempFiles) {
            std::error_code ignored;
            std::filesystem::remove(path, ignored);
        }
        tempFiles.clear();
    }
};

void openDevice(Session& session) {
    session.device = session.firmwarePath ? std::make_shared<dai::Device>(session.info, *session.firmwarePath)
                                          : std::make_shared<dai::Device>(session.info, session.maxUsbSpeed);
}

/// Pesan untuk perangkat yang terlihat di bus tapi tidak boleh dibuka proses ini.
const char* const PermissionHint =
    "izin USB tidak cukup. Di Linux pasang aturan udev untuk vendor 03e7, lalu cabut-colok perangkat: "
    "echo 'SUBSYSTEM==\"usb\", ATTRS{idVendor}==\"03e7\", MODE=\"0666\"' | sudo tee /etc/udev/rules.d/80-movidius.rules "
    "&& sudo udevadm control --reload-rules && sudo udevadm trigger";

Session* asSession(void* handle) {
    if(handle == nullptr) throw DaiError(DAI_INVALID_ARGUMENT, "handle perangkat null");
    return static_cast<Session*>(handle);
}

ReadyPacket makePacket(const std::string& stream, dai_packet_type_t type, PacketHolder* holder, const uint8_t* data, size_t length) {
    ReadyPacket item;
    copyFixed(item.packet.stream_name, stream);
    item.packet.type = type;
    item.packet.data = data;
    item.packet.data_length = static_cast<int64_t>(length);
    item.packet.native_handle = holder;
    item.holder = holder;
    return item;
}

/// Mengubah satu pesan depthai menjadi satu atau lebih paket ABI.
void enqueueMessage(Session& session, const std::string& stream, StreamKind kind, const std::shared_ptr<dai::ADatatype>& message) {
    if(auto frame = std::dynamic_pointer_cast<dai::ImgFrame>(message)) {
        auto* holder = new PacketHolder{message, {}};
        const auto bytes = frame->getData();
        const auto width = static_cast<int>(frame->getWidth());
        const auto height = static_cast<int>(frame->getHeight());

        bool depth = frame->getType() == dai::ImgFrame::Type::RAW16 && kind == StreamKind::Depth;
        bool planar = false;
        auto format = depth ? DAI_PIXEL_UNKNOWN : pixelFormatOf(frame->getType(), planar);

        const uint8_t* data = bytes.data();
        size_t length = bytes.size();

        if(planar) {
            // Tiga bidang warna rapat (stride = lebar) disusun ulang menjadi interleaved.
            const size_t plane = static_cast<size_t>(width) * height;
            const size_t stride = frame->getStride() > 0 ? frame->getStride() : static_cast<size_t>(width);
            holder->buffer.resize(plane * 3);
            for(int y = 0; y < height; ++y) {
                for(int x = 0; x < width; ++x) {
                    const size_t src = y * stride + x;
                    const size_t dst = (static_cast<size_t>(y) * width + x) * 3;
                    holder->buffer[dst] = data[src];
                    holder->buffer[dst + 1] = data[stride * height + src];
                    holder->buffer[dst + 2] = data[2 * stride * height + src];
                }
            }
            data = holder->buffer.data();
            length = holder->buffer.size();
        }

        auto type = depth ? DAI_PACKET_DEPTH : (format == DAI_PIXEL_JPEG ? DAI_PACKET_ENCODED : DAI_PACKET_IMAGE);
        auto item = makePacket(stream, type, holder, data, length);
        item.packet.width = width;
        item.packet.height = height;
        item.packet.pixel_format = format;
        item.packet.sequence_number = frame->getSequenceNum();
        item.packet.timestamp_nanos = deviceNanos(*frame);
        session.push(item);
        return;
    }

    if(auto nn = std::dynamic_pointer_cast<dai::NNData>(message)) {
        auto* holder = new PacketHolder{message, serializeTensors(*nn)};
        auto item = makePacket(stream, DAI_PACKET_NEURAL_TENSORS, holder, holder->buffer.data(), holder->buffer.size());
        // Ukuran masukan NN, dipakai parser .NET untuk menormalkan koordinat.
        if(nn->transformation) {
            auto [w, h] = nn->transformation->getSize();
            item.packet.width = static_cast<int32_t>(w);
            item.packet.height = static_cast<int32_t>(h);
        }
        item.packet.sequence_number = nn->getSequenceNum();
        item.packet.timestamp_nanos = deviceNanos(*nn);
        session.push(item);
        return;
    }

    if(auto encoded = std::dynamic_pointer_cast<dai::EncodedFrame>(message)) {
        auto* holder = new PacketHolder{message, {}};
        const auto bytes = encoded->getData();
        auto item = makePacket(stream, DAI_PACKET_ENCODED, holder, bytes.data(), bytes.size());
        item.packet.width = static_cast<int32_t>(encoded->getWidth());
        item.packet.height = static_cast<int32_t>(encoded->getHeight());
        item.packet.pixel_format = encoded->getProfile() == dai::EncodedFrame::Profile::JPEG ? DAI_PIXEL_JPEG : DAI_PIXEL_UNKNOWN;
        item.packet.sequence_number = encoded->getSequenceNum();
        item.packet.timestamp_nanos = deviceNanos(*encoded);
        session.push(item);
        return;
    }

    if(auto imu = std::dynamic_pointer_cast<dai::IMUData>(message)) {
        // Satu pesan IMU membawa satu batch laporan; tiap laporan menjadi paket sendiri
        // agar sisi .NET cukup mengenal satu bentuk ImuReading.
        for(const auto& report : imu->packets) {
            const auto& a = report.acceleroMeter;
            const auto& g = report.gyroscope;
            const auto& r = report.rotationVector;
            const bool hasRotation = r.i != 0 || r.j != 0 || r.k != 0 || r.real != 0;
            const float values[11] = {a.x, a.y, a.z, g.x, g.y, g.z, r.i, r.j, r.k, r.real, hasRotation ? 1.0f : 0.0f};

            auto* holder = new PacketHolder{message, {}};
            appendFloats(holder->buffer, values, 11);
            auto item = makePacket(stream, DAI_PACKET_IMU, holder, holder->buffer.data(), holder->buffer.size());
            item.packet.sequence_number = a.sequence;
            item.packet.timestamp_nanos =
                std::chrono::duration_cast<std::chrono::nanoseconds>(a.getTimestampDevice().time_since_epoch()).count();
            session.push(item);
        }
        return;
    }

    // Tipe lain (mis. ImgDetections dari node yang tidak dipetakan) belum punya bentuk ABI.
}

// ---------------------------------------------------------------------------
// Pembangun pipeline dari JSON
// ---------------------------------------------------------------------------

dai::CameraBoardSocket parseSocket(const std::string& name) {
    static const std::map<std::string, dai::CameraBoardSocket> sockets = {
        {"Auto", dai::CameraBoardSocket::AUTO}, {"CamA", dai::CameraBoardSocket::CAM_A}, {"Rgb", dai::CameraBoardSocket::CAM_A},
        {"CamB", dai::CameraBoardSocket::CAM_B}, {"Left", dai::CameraBoardSocket::CAM_B}, {"CamC", dai::CameraBoardSocket::CAM_C},
        {"Right", dai::CameraBoardSocket::CAM_C}, {"CamD", dai::CameraBoardSocket::CAM_D}, {"CamE", dai::CameraBoardSocket::CAM_E},
        {"CamF", dai::CameraBoardSocket::CAM_F}, {"CamG", dai::CameraBoardSocket::CAM_G}, {"CamH", dai::CameraBoardSocket::CAM_H},
    };
    auto it = sockets.find(name);
    if(it == sockets.end()) throw DaiError(DAI_INVALID_ARGUMENT, "socket kamera tidak dikenal: " + name);
    return it->second;
}

std::pair<uint32_t, uint32_t> colorSensorSize(const std::string& resolution) {
    static const std::map<std::string, std::pair<uint32_t, uint32_t>> sizes = {
        {"The720P", {1280, 720}}, {"The800P", {1280, 800}}, {"The1080P", {1920, 1080}}, {"The1200P", {1920, 1200}},
        {"The4K", {3840, 2160}}, {"The5Mp", {2592, 1944}}, {"The12Mp", {4056, 3040}}, {"The13Mp", {4208, 3120}},
        {"The48Mp", {8000, 6000}},
    };
    auto it = sizes.find(resolution);
    return it == sizes.end() ? std::pair<uint32_t, uint32_t>{1920, 1080} : it->second;
}

std::pair<uint32_t, uint32_t> monoSensorSize(const std::string& resolution) {
    static const std::map<std::string, std::pair<uint32_t, uint32_t>> sizes = {
        {"The400P", {640, 400}}, {"The480P", {640, 480}}, {"The720P", {1280, 720}}, {"The800P", {1280, 800}}, {"The1200P", {1920, 1200}},
    };
    auto it = sizes.find(resolution);
    return it == sizes.end() ? std::pair<uint32_t, uint32_t>{640, 400} : it->second;
}

template <typename T>
T prop(const json& props, const char* key, T fallback) {
    auto it = props.find(key);
    if(it == props.end() || it->is_null()) return fallback;
    try {
        return it->get<T>();
    } catch(const json::exception&) {
        return fallback;
    }
}

/// Satu node .NET beserta padanan depthai-nya.
struct BuiltNode {
    std::string name;
    std::string type;
    json props;
    std::shared_ptr<dai::node::Camera> camera;
    std::shared_ptr<dai::node::StereoDepth> stereo;
    std::shared_ptr<dai::node::NeuralNetwork> nn;
    std::shared_ptr<dai::node::ImageManip> manip;
    std::shared_ptr<dai::node::VideoEncoder> encoder;
    std::shared_ptr<dai::node::IMU> imu;
    bool hostInput = false;  // tidak punya padanan node depthai: hanya antrean masukan
    std::map<std::string, dai::Node::Output*> requested;  // keluaran kamera yang sudah diminta
};

class PipelineBuilder {
   public:
    PipelineBuilder(Session& session, const json& document) : session_(session), document_(document) {}

    void build() {
        session_.pipeline = std::make_unique<dai::Pipeline>(session_.device);

        // Kamera yang mengumpani NN diminta planar: model blob RVC2 mengharapkan CHW.
        for(const auto& link : array("links")) {
            auto [toNode, toPort] = split(link.at("to").get<std::string>());
            auto& target = findSpec(toNode);
            const auto type = target.at("type").get<std::string>();
            if((type == "NeuralNetwork" || type == "DetectionNetwork" || type == "SpatialDetectionNetwork") && toPort == "input") {
                planarOutputs_.insert(link.at("from").get<std::string>());
            }
        }

        for(const auto& spec : array("nodes")) createNode(spec);

        std::map<std::string, HostInput> hostInputs;
        std::map<std::string, dai::Node::Input*> hostTargets;
        for(const auto& link : array("links")) {
            const auto from = link.at("from").get<std::string>();
            const auto to = link.at("to").get<std::string>();
            auto* input = resolveInput(to);
            if(input == nullptr) continue;  // port yang sengaja tidak dipetakan, lihat resolveInput

            // HostInput bukan node depthai: masukan tujuannya diberi antrean dari host.
            const auto source = split(from).first;
            if(node(source).hostInput) {
                if(hostTargets.count(source) > 0) {
                    throw DaiError(DAI_NOT_SUPPORTED, "HostInput '" + source + "' hanya boleh terhubung ke satu masukan");
                }
                hostTargets[source] = input;
                hostInputs[source].planar = node(split(to).first).nn != nullptr;
                continue;
            }
            resolveOutput(from)->link(*input);
        }

        for(auto& [name, input] : hostInputs) {
            // Non-blocking dengan antrean pendek: host yang mengirim lebih cepat dari
            // inferensi kehilangan gambar lama alih-alih menahan thread pemanggil.
            input.queue = hostTargets.at(name)->createInputQueue(4, false);
            session_.inputs[name] = input;
        }

        for(const auto& stream : array("streams")) {
            const auto name = stream.at("name").get<std::string>();
            const auto source = stream.at("source").get<std::string>();
            const auto kind = streamKind(source);

            // Selalu non-blocking: paket diambil lewat callback, bukan get(), jadi queue
            // blocking yang penuh akan menahan perangkat selamanya.
            auto queue = resolveOutput(source)->createOutputQueue(1, false);
            Session* session = &session_;
            queue->addCallback([session, name, kind](std::shared_ptr<dai::ADatatype> message) {
                try {
                    enqueueMessage(*session, name, kind, message);
                } catch(...) {
                    // Pesan rusak dibuang; menjatuhkan thread depthai akan mematikan semua stream.
                }
            });
            session_.queues.push_back(queue);
        }
    }

   private:
    Session& session_;
    const json& document_;
    std::map<std::string, BuiltNode> nodes_;
    std::set<std::string> planarOutputs_;

    /// Referensi ke array di dokumen asli. json::value() mengembalikan salinan, sehingga
    /// referensi ke elemennya menggantung begitu ekspresi selesai.
    const json& array(const char* key) const {
        static const json empty = json::array();
        auto it = document_.find(key);
        return it != document_.end() && it->is_array() ? *it : empty;
    }

    static std::pair<std::string, std::string> split(const std::string& path) {
        const auto dot = path.rfind('.');
        if(dot == std::string::npos) throw DaiError(DAI_INVALID_ARGUMENT, "path port harus berbentuk node.port: " + path);
        return {path.substr(0, dot), path.substr(dot + 1)};
    }

    const json& findSpec(const std::string& name) const {
        for(const auto& spec : array("nodes")) {
            if(spec.at("name").get<std::string>() == name) return spec;
        }
        throw DaiError(DAI_INVALID_ARGUMENT, "link merujuk node yang tidak ada: " + name);
    }

    BuiltNode& node(const std::string& name) {
        auto it = nodes_.find(name);
        if(it == nodes_.end()) throw DaiError(DAI_INVALID_ARGUMENT, "node tidak ada di pipeline: " + name);
        return it->second;
    }

    StreamKind streamKind(const std::string& source) {
        auto [nodeName, port] = split(source);
        const auto& type = node(nodeName).type;
        if(type == "StereoDepth" && port == "depth") return StreamKind::Depth;
        if((type == "NeuralNetwork" || type == "DetectionNetwork" || type == "SpatialDetectionNetwork") && (port == "out" || port == "detections"))
            return StreamKind::Tensors;
        if(type == "VideoEncoder") return StreamKind::Encoded;
        if(type == "Imu") return StreamKind::Imu;
        return StreamKind::Image;
    }

    void createNode(const json& spec) {
        BuiltNode built;
        built.name = spec.at("name").get<std::string>();
        built.type = spec.at("type").get<std::string>();
        built.props = spec.value("properties", json::object());
        auto& p = built.props;
        auto& pipeline = *session_.pipeline;

        if(built.type == "ColorCamera" || built.type == "MonoCamera") {
            const auto fallback = built.type == "ColorCamera" ? "CamA" : "CamB";
            built.camera = pipeline.create<dai::node::Camera>()->build(parseSocket(prop<std::string>(p, "socket", fallback)));
        } else if(built.type == "StereoDepth") {
            built.stereo = pipeline.create<dai::node::StereoDepth>();
            configureStereo(*built.stereo, p);
        } else if(built.type == "NeuralNetwork" || built.type == "DetectionNetwork" || built.type == "SpatialDetectionNetwork") {
            // Semua varian dijalankan sebagai NeuralNetwork mentah: tensornya diurai di host oleh
            // parser .NET yang sama dengan jalur simulasi, sehingga hasilnya konsisten.
            built.nn = pipeline.create<dai::node::NeuralNetwork>();
            configureNeuralNetwork(*built.nn, p);
        } else if(built.type == "ImageManip") {
            built.manip = pipeline.create<dai::node::ImageManip>();
            configureManip(*built.manip, p);
        } else if(built.type == "VideoEncoder") {
            built.encoder = pipeline.create<dai::node::VideoEncoder>();
            configureEncoder(*built.encoder, p);
        } else if(built.type == "Imu") {
            built.imu = pipeline.create<dai::node::IMU>();
            configureImu(*built.imu, p);
        } else if(built.type == "HostInput") {
            built.hostInput = true;
        } else {
            throw DaiError(DAI_NOT_SUPPORTED, "tipe node belum didukung runtime native: " + built.type);
        }

        nodes_.emplace(built.name, std::move(built));
    }

    static void configureStereo(dai::node::StereoDepth& stereo, const json& p) {
        using Preset = dai::node::StereoDepth::PresetMode;
        static const std::map<std::string, Preset> presets = {
            {"Default", Preset::DEFAULT}, {"HighDensity", Preset::DENSITY}, {"HighAccuracy", Preset::ACCURACY}, {"FastAccuracy", Preset::ROBOTICS},
        };
        auto preset = presets.find(prop<std::string>(p, "preset", "Default"));
        stereo.setDefaultProfilePreset(preset == presets.end() ? Preset::DEFAULT : preset->second);

        stereo.setLeftRightCheck(prop(p, "leftRightCheck", true));
        stereo.setSubpixel(prop(p, "subpixel", false));
        stereo.setExtendedDisparity(prop(p, "extendedDisparity", false));
        stereo.initialConfig->setConfidenceThreshold(prop(p, "confidenceThreshold", 200));

        static const std::map<std::string, dai::node::StereoDepth::MedianFilter> medians = {
            {"Off", dai::node::StereoDepth::MedianFilter::MEDIAN_OFF}, {"Kernel3x3", dai::node::StereoDepth::MedianFilter::KERNEL_3x3},
            {"Kernel5x5", dai::node::StereoDepth::MedianFilter::KERNEL_5x5}, {"Kernel7x7", dai::node::StereoDepth::MedianFilter::KERNEL_7x7},
        };
        auto median = medians.find(prop<std::string>(p, "medianFilter", "Kernel7x7"));
        if(median != medians.end()) stereo.initialConfig->setMedianFilter(median->second);

        const auto align = prop<std::string>(p, "alignTo", "Auto");
        if(align != "Auto") stereo.setDepthAlign(parseSocket(align));

        const int width = prop(p, "outputWidth", 0);
        const int height = prop(p, "outputHeight", 0);
        if(width > 0 && height > 0) stereo.setOutputSize(width, height);
    }

    void configureNeuralNetwork(dai::node::NeuralNetwork& nn, const json& p) {
        const auto modelName = prop<std::string>(p, "model", "");
        auto model = session_.models.find(modelName);
        if(modelName.empty() || model == session_.models.end()) {
            throw DaiError(DAI_INVALID_ARGUMENT, "model '" + modelName + "' belum diunggah lewat dai_device_upload_model");
        }
        if(model->second.empty()) {
            throw DaiError(DAI_INVALID_ARGUMENT,
                           "model '" + modelName + "' adalah placeholder tanpa bobot; muat berkas .blob atau NN Archive untuk hardware");
        }

        const auto format = prop<std::string>(p, "modelFormat", "Blob");
        const int shaves = prop(p, "shavesPerThread", 0);

        if(format == "Blob") {
            nn.setBlob(dai::OpenVINO::Blob(model->second));
        } else if(format == "SuperBlob") {
            dai::OpenVINO::SuperBlob superBlob(model->second);
            nn.setBlob(superBlob.getBlobWithNumShaves(shaves > 0 ? shaves : 6));
        } else if(format == "NnArchive") {
            // NNArchive hanya bisa dibuka dari berkas; payload ditulis ke temp dulu.
            auto path = std::filesystem::temp_directory_path()
                        / ("depthai-c-" + std::to_string(reinterpret_cast<uintptr_t>(&nn)) + "-" + modelName + ".tar.xz");
            {
                std::ofstream file(path, std::ios::binary);
                file.write(reinterpret_cast<const char*>(model->second.data()), static_cast<std::streamsize>(model->second.size()));
            }
            session_.tempFiles.push_back(path);
            dai::NNArchive archive(path);
            if(shaves > 0)
                nn.setNNArchive(archive, shaves);
            else
                nn.setNNArchive(archive);
        } else {
            throw DaiError(DAI_NOT_SUPPORTED, "format model '" + format + "' tidak bisa dijalankan di perangkat; pakai .blob atau NN Archive");
        }

        const int threads = prop(p, "inferenceThreads", 0);
        if(threads > 0) nn.setNumInferenceThreads(threads);
        if(shaves > 0 && format != "NnArchive") nn.setNumShavesPerInferenceThread(shaves);
        nn.input.setBlocking(prop(p, "blockingInput", false));
    }

    static void configureManip(dai::node::ImageManip& manip, const json& p) {
        auto& config = *manip.initialConfig;

        if(p.contains("crop") && p["crop"].is_array() && p["crop"].size() == 4) {
            const auto c = p["crop"].get<std::vector<float>>();
            config.addCrop(dai::Rect(c[0], c[1], c[2] - c[0], c[3] - c[1]), true);
        }

        const int rotate = prop(p, "rotateDegrees", 0);
        if(rotate != 0) config.addRotateDeg(static_cast<float>(rotate));

        const int width = prop(p, "resizeWidth", 0);
        const int height = prop(p, "resizeHeight", 0);
        if(width > 0 && height > 0) {
            // Sama dengan preview kamera: menjaga rasio dengan memotong, bukan menambah bingkai.
            auto mode = prop(p, "keepAspectRatio", true) ? dai::ImageManipConfig::ResizeMode::CENTER_CROP
                                                         : dai::ImageManipConfig::ResizeMode::STRETCH;
            config.setOutputSize(static_cast<uint32_t>(width), static_cast<uint32_t>(height), mode);
            manip.setMaxOutputFrameSize(width * height * 3);
        }

        const auto format = prop<std::string>(p, "outputFormat", "Bgr888");
        const bool interleaved = prop(p, "interleaved", true);
        using T = dai::ImgFrame::Type;
        if(format == "Bgr888")
            config.setFrameType(interleaved ? T::BGR888i : T::BGR888p);
        else if(format == "Rgb888")
            config.setFrameType(interleaved ? T::RGB888i : T::RGB888p);
        else if(format == "Gray8")
            config.setFrameType(T::GRAY8);
        else if(format == "Nv12")
            config.setFrameType(T::NV12);
    }

    static void configureEncoder(dai::node::VideoEncoder& encoder, const json& p) {
        using Profile = dai::VideoEncoderProperties::Profile;
        const auto profile = prop<std::string>(p, "profile", "MjpegLossy");
        const auto fps = static_cast<float>(prop(p, "fps", 30));

        if(profile == "H264Main")
            encoder.setDefaultProfilePreset(fps, Profile::H264_MAIN);
        else if(profile == "H265Main")
            encoder.setDefaultProfilePreset(fps, Profile::H265_MAIN);
        else
            encoder.setDefaultProfilePreset(fps, Profile::MJPEG);

        if(profile == "MjpegLossless") encoder.setLossless(true);
        encoder.setQuality(prop(p, "quality", 95));
        const int bitrate = prop(p, "bitrateKbps", 0);
        if(bitrate > 0) encoder.setBitrateKbps(bitrate);
        encoder.setKeyframeFrequency(prop(p, "keyframeFrequency", 30));
    }

    static void configureImu(dai::node::IMU& imu, const json& p) {
        // Flags .NET diserialisasi sebagai "Accelerometer, Gyroscope".
        const auto flags = prop<std::string>(p, "sensors", "Accelerometer, Gyroscope");
        std::vector<dai::IMUSensor> sensors;
        if(flags.find("Accelerometer") != std::string::npos || flags == "All") sensors.push_back(dai::IMUSensor::ACCELEROMETER_CALIBRATED);
        if(flags.find("Gyroscope") != std::string::npos || flags == "All") sensors.push_back(dai::IMUSensor::GYROSCOPE_CALIBRATED);
        if(flags.find("Magnetometer") != std::string::npos || flags == "All") sensors.push_back(dai::IMUSensor::MAGNETOMETER_CALIBRATED);
        if(flags.find("RotationVector") != std::string::npos || flags == "All") sensors.push_back(dai::IMUSensor::ROTATION_VECTOR);
        if(sensors.empty()) throw DaiError(DAI_INVALID_ARGUMENT, "node IMU tanpa sensor aktif");

        imu.enableIMUSensor(sensors, static_cast<uint32_t>(prop(p, "rateHz", 100)));
        const int threshold = prop(p, "batchReportThreshold", 1);
        imu.setBatchReportThreshold(threshold);
        imu.setMaxBatchReports(std::max(10, threshold));
    }

    dai::Node::Output* resolveOutput(const std::string& path) {
        auto [nodeName, port] = split(path);
        auto& n = node(nodeName);

        if(n.camera) {
            auto existing = n.requested.find(port);
            if(existing != n.requested.end()) return existing->second;
            auto* output = requestCameraOutput(n, port, planarOutputs_.count(path) > 0);
            n.requested[port] = output;
            return output;
        }
        if(n.stereo) {
            if(port == "depth") return &n.stereo->depth;
            if(port == "disparity") return &n.stereo->disparity;
            if(port == "rectifiedLeft") return &n.stereo->rectifiedLeft;
            if(port == "rectifiedRight") return &n.stereo->rectifiedRight;
            if(port == "syncedLeft") return &n.stereo->syncedLeft;
            if(port == "syncedRight") return &n.stereo->syncedRight;
        }
        if(n.nn) {
            if(port == "out" || port == "detections") return &n.nn->out;
            if(port == "passthrough") return &n.nn->passthrough;
        }
        if(n.manip && port == "out") return &n.manip->out;
        if(n.encoder && port == "bitstream") return &n.encoder->out;
        if(n.imu && port == "out") return &n.imu->out;
        if(n.hostInput) throw DaiError(DAI_NOT_SUPPORTED, "keluaran HostInput '" + path + "' tidak bisa dialirkan; gambarnya sudah ada di host");

        throw DaiError(DAI_NOT_SUPPORTED, "keluaran '" + path + "' belum didukung runtime native");
    }

    dai::Node::Output* requestCameraOutput(BuiltNode& n, const std::string& port, bool forNeuralNetwork) {
        auto& p = n.props;
        const auto fps = static_cast<float>(prop(p, "fps", 30));
        using T = dai::ImgFrame::Type;

        if(n.type == "MonoCamera") {
            if(port != "out") throw DaiError(DAI_NOT_SUPPORTED, "kamera mono hanya punya keluaran 'out'");
            return n.camera->requestOutput(monoSensorSize(prop<std::string>(p, "resolution", "The400P")), std::nullopt, dai::ImgResizeMode::CROP, fps);
        }

        if(port == "preview") {
            const bool rgb = prop<std::string>(p, "colorOrder", "Bgr") == "Rgb";
            const bool interleaved = prop(p, "interleaved", true) && !forNeuralNetwork;
            const auto type = rgb ? (interleaved ? T::RGB888i : T::RGB888p) : (interleaved ? T::BGR888i : T::BGR888p);
            const auto mode = prop(p, "keepPreviewAspectRatio", true) ? dai::ImgResizeMode::CROP : dai::ImgResizeMode::STRETCH;
            const auto width = static_cast<uint32_t>(prop(p, "previewWidth", 640));
            const auto height = static_cast<uint32_t>(prop(p, "previewHeight", 480));
            return n.camera->requestOutput({width, height}, type, mode, fps);
        }
        if(port == "video") {
            return n.camera->requestOutput(colorSensorSize(prop<std::string>(p, "resolution", "The1080P")), T::NV12, dai::ImgResizeMode::CROP, fps);
        }
        if(port == "isp") return n.camera->requestIspOutput(fps);
        if(port == "still") return n.camera->requestFullResolutionOutput(T::NV12);

        throw DaiError(DAI_NOT_SUPPORTED, "keluaran kamera '" + port + "' tidak dikenal");
    }

    dai::Node::Input* resolveInput(const std::string& path) {
        auto [nodeName, port] = split(path);
        auto& n = node(nodeName);

        if(n.stereo && port == "left") return &n.stereo->left;
        if(n.stereo && port == "right") return &n.stereo->right;
        if(n.nn && port == "input") return &n.nn->input;
        // Kedalaman untuk deteksi spasial belum dipakai: posisi 3D juga belum dihitung di
        // jalur simulasi, jadi link ini sengaja diabaikan alih-alih menolak pipeline.
        if(n.nn && port == "depth") return nullptr;
        if(n.manip && port == "input") return &n.manip->inputImage;
        if(n.encoder && port == "input") return &n.encoder->input;
        if(n.camera && port == "inputControl") return &n.camera->inputControl;

        throw DaiError(DAI_NOT_SUPPORTED, "masukan '" + path + "' belum didukung runtime native");
    }
};

void stopSession(Session& session, bool reopen) {
    if(session.pipeline) {
        if(session.pipeline->isRunning()) session.pipeline->stop();
        session.queues.clear();
        session.inputs.clear();
        session.pipeline.reset();
    }
    session.clearPackets();
    session.removeTempFiles();

    // Pipeline::stop() di v3 ikut menutup perangkat. Buka lagi supaya telemetri dan
    // pipeline berikutnya tetap bekerja; boot ulang ini makan beberapa detik.
    if(reopen && session.device && session.device->isClosed()) openDevice(session);
}

}  // namespace

// ---------------------------------------------------------------------------
// ABI publik
// ---------------------------------------------------------------------------

extern "C" {

DAI_API int dai_get_version(char* buffer, int length) {
    return copyString(dai::build::VERSION, buffer, length);
}

DAI_API int dai_last_error(char* buffer, int length) {
    return copyString(lastError, buffer, length);
}

DAI_API int dai_device_list(dai_device_info_t* buffer, int capacity, int* count) {
    return guarded(DAI_COMMUNICATION_ERROR, [&] {
        if(count == nullptr || (buffer == nullptr && capacity > 0)) return fail(DAI_INVALID_ARGUMENT, "buffer atau count null");

        const auto devices = dai::Device::getAllAvailableDevices();
        *count = static_cast<int>(devices.size());

        for(int i = 0; i < std::min(capacity, *count); ++i) {
            const auto& d = devices[static_cast<size_t>(i)];
            auto& out = buffer[i];
            std::memset(&out, 0, sizeof out);
            copyFixed(out.mx_id, d.getDeviceId());
            copyFixed(out.name, d.platform == X_LINK_RVC4 ? "OAK4" : "OAK");
            copyFixed(out.connection_path, d.name);
            out.protocol = d.protocol == X_LINK_TCP_IP ? 2 : (d.protocol == X_LINK_USB_VSC ? 1 : 0);
            // Perangkat yang sudah BOOTED dipegang proses lain; perangkat tanpa izin USB
            // dilaporkan Faulty supaya tidak tampak bisa dibuka; sisanya bisa dibuka.
            out.state = d.status == X_LINK_INSUFFICIENT_PERMISSIONS ? 5 : (d.state == X_LINK_BOOTED ? 2 : 1);
            out.usb_speed = 0;
        }
        return static_cast<int>(DAI_OK);
    });
}

DAI_API int dai_device_open(const char* mx_id, const dai_open_options_t* options, void** handle) {
    return guarded(DAI_COMMUNICATION_ERROR, [&] {
        if(mx_id == nullptr || handle == nullptr) return fail(DAI_INVALID_ARGUMENT, "mx_id atau handle null");
        *handle = nullptr;

        auto [found, info] = dai::Device::getDeviceById(mx_id);
        if(!found) return fail(DAI_DEVICE_NOT_FOUND, std::string("tidak ada perangkat dengan MxId ") + mx_id);
        if(info.status == X_LINK_INSUFFICIENT_PERMISSIONS) {
            return fail(DAI_COMMUNICATION_ERROR, std::string("perangkat ") + mx_id + ": " + PermissionHint);
        }
        if(info.state == X_LINK_BOOTED) return fail(DAI_DEVICE_BUSY, std::string("perangkat ") + mx_id + " sedang dipakai proses lain");

        auto session = std::make_unique<Session>();
        session->info = info;
        if(options != nullptr && options->max_usb_speed > 0) session->maxUsbSpeed = static_cast<dai::UsbSpeed>(options->max_usb_speed);
        if(options != nullptr && options->firmware_path != nullptr && options->firmware_path[0] != '\0') {
            session->firmwarePath = std::filesystem::path(options->firmware_path);
        }
        openDevice(*session);

        *handle = session.release();
        return static_cast<int>(DAI_OK);
    });
}

DAI_API int dai_device_close(void* handle) {
    return guarded(DAI_UNKNOWN, [&] {
        std::unique_ptr<Session> session(asSession(handle));
        stopSession(*session, false);
        if(session->device && !session->device->isClosed()) session->device->close();
        return static_cast<int>(DAI_OK);
    });
}

DAI_API int dai_device_capabilities(void* handle, dai_capabilities_t* out) {
    return guarded(DAI_COMMUNICATION_ERROR, [&] {
        auto* session = asSession(handle);
        if(out == nullptr) return fail(DAI_INVALID_ARGUMENT, "out null");
        std::memset(out, 0, sizeof *out);

        for(const auto& camera : session->device->getConnectedCameraFeatures()) {
            const auto& types = camera.supportedTypes;
            if(std::find(types.begin(), types.end(), dai::CameraSensorType::COLOR) != types.end())
                out->color_camera_count++;
            else
                out->mono_camera_count++;

            const auto socket = static_cast<int>(camera.socket);
            if(socket >= 0 && socket < 8) copyFixed(out->sensor_names[socket], camera.sensorName);
        }

        out->usb_speed = static_cast<int32_t>(session->device->getUsbSpeed());
        try {
            // Nama produk dibaca dari EEPROM; perangkat lama atau tanpa kalibrasi melemparnya kosong.
            auto product = session->device->getProductName();
            if(product.empty()) product = session->device->getDeviceName();
            copyFixed(out->product_name, product);
        } catch(const std::exception&) {
        }

        try {
            out->supports_stereo_depth = session->device->getStereoPairs().empty() ? 0 : 1;
        } catch(const std::exception&) {
            out->supports_stereo_depth = 0;  // perangkat tanpa kalibrasi stereo
        }

        const auto imu = session->device->getConnectedIMU();
        out->has_imu = imu.empty() || imu == "NONE" ? 0 : 1;
        // RVC2 punya 16 SHAVE; RVC4 tidak memakai SHAVE untuk inferensi.
        out->shave_cores = session->device->getPlatform() == dai::Platform::RVC2 ? 16 : 0;
        return static_cast<int>(DAI_OK);
    });
}

DAI_API int dai_device_upload_model(void* handle, const char* name, const uint8_t* data, int64_t length) {
    return guarded(DAI_UNKNOWN, [&] {
        auto* session = asSession(handle);
        if(name == nullptr || length < 0 || (data == nullptr && length > 0)) return fail(DAI_INVALID_ARGUMENT, "argumen model tidak valid");
        session->models[name] = std::vector<uint8_t>(data, data + length);
        return static_cast<int>(DAI_OK);
    });
}

DAI_API int dai_device_start_pipeline(void* handle, const char* pipeline_json) {
    return guarded(DAI_PIPELINE_ERROR, [&] {
        auto* session = asSession(handle);
        if(pipeline_json == nullptr) return fail(DAI_INVALID_ARGUMENT, "JSON pipeline null");
        if(session->pipeline && session->pipeline->isRunning()) return fail(DAI_DEVICE_BUSY, "pipeline sudah berjalan");

        json document;
        try {
            document = json::parse(pipeline_json);
        } catch(const json::exception& e) {
            return fail(DAI_INVALID_ARGUMENT, std::string("JSON pipeline tidak valid: ") + e.what());
        }

        if(session->device->isClosed()) openDevice(*session);

        try {
            PipelineBuilder(*session, document).build();
            session->pipeline->start();
        } catch(...) {
            stopSession(*session, true);
            throw;
        }
        return static_cast<int>(DAI_OK);
    });
}

DAI_API int dai_device_stop_pipeline(void* handle) {
    return guarded(DAI_UNKNOWN, [&] {
        stopSession(*asSession(handle), true);
        return static_cast<int>(DAI_OK);
    });
}

DAI_API int dai_device_send_frame(void* handle, const char* input_name, int32_t width, int32_t height, int32_t pixel_format,
                                  const uint8_t* data, int64_t length, int64_t sequence_number) {
    return guarded(DAI_UNKNOWN, [&] {
        auto* session = asSession(handle);
        if(input_name == nullptr || data == nullptr || width <= 0 || height <= 0) return fail(DAI_INVALID_ARGUMENT, "argumen gambar tidak valid");

        auto it = session->inputs.find(input_name);
        if(it == session->inputs.end()) {
            return fail(DAI_INVALID_ARGUMENT, std::string("pipeline yang berjalan tidak punya HostInput bernama ") + input_name);
        }

        using T = dai::ImgFrame::Type;
        const auto pixels = static_cast<size_t>(width) * static_cast<size_t>(height);
        const bool gray = pixel_format == DAI_PIXEL_GRAY8;
        if(!gray && pixel_format != DAI_PIXEL_BGR888 && pixel_format != DAI_PIXEL_RGB888) {
            return fail(DAI_NOT_SUPPORTED, "HostInput hanya menerima GRAY8, BGR888, atau RGB888");
        }
        const size_t channels = gray ? 1 : 3;
        if(length != static_cast<int64_t>(pixels * channels)) return fail(DAI_INVALID_ARGUMENT, "panjang data tidak cocok dengan ukuran gambar");

        const bool planar = it->second.planar && !gray;
        std::vector<uint8_t> bytes(pixels * channels);
        if(planar) {
            // Kebalikan dari konversi di enqueueMessage: interleaved menjadi tiga bidang.
            for(size_t i = 0; i < pixels; ++i) {
                bytes[i] = data[i * 3];
                bytes[pixels + i] = data[i * 3 + 1];
                bytes[2 * pixels + i] = data[i * 3 + 2];
            }
        } else {
            std::memcpy(bytes.data(), data, bytes.size());
        }

        const bool rgb = pixel_format == DAI_PIXEL_RGB888;
        auto frame = std::make_shared<dai::ImgFrame>();
        frame->setData(std::move(bytes));
        frame->setType(gray ? T::GRAY8 : (rgb ? (planar ? T::RGB888p : T::RGB888i) : (planar ? T::BGR888p : T::BGR888i)));
        frame->setSize(static_cast<unsigned>(width), static_cast<unsigned>(height));
        // Tanpa transformasi, NNData keluaran tidak membawa ukuran masukan dan koordinat
        // tidak bisa dinormalkan di host.
        frame->setTransformation(dai::ImgTransformation(static_cast<size_t>(width), static_cast<size_t>(height)));
        frame->setSequenceNum(sequence_number);
        frame->setTimestamp(std::chrono::steady_clock::now());
        it->second.queue->send(frame);
        return static_cast<int>(DAI_OK);
    });
}

DAI_API int dai_device_poll(void* handle, dai_packet_t* out, int timeout_ms) {
    return guarded(DAI_UNKNOWN, [&] {
        auto* session = asSession(handle);
        if(out == nullptr) return fail(DAI_INVALID_ARGUMENT, "out null");

        std::unique_lock lock(session->mutex);
        const auto hasPacket = [&] { return !session->packets.empty(); };
        if(!session->ready.wait_for(lock, std::chrono::milliseconds(std::max(0, timeout_ms)), hasPacket)) {
            return static_cast<int>(DAI_TIMEOUT);
        }

        *out = session->packets.front().packet;
        session->packets.pop_front();
        return static_cast<int>(DAI_OK);
    });
}

DAI_API int dai_packet_release(void* handle, void* native_handle) {
    return guarded(DAI_UNKNOWN, [&] {
        asSession(handle);
        delete static_cast<PacketHolder*>(native_handle);
        return static_cast<int>(DAI_OK);
    });
}

DAI_API int dai_device_telemetry(void* handle, dai_telemetry_t* out) {
    return guarded(DAI_COMMUNICATION_ERROR, [&] {
        auto* session = asSession(handle);
        if(out == nullptr) return fail(DAI_INVALID_ARGUMENT, "out null");

        auto& device = *session->device;
        out->chip_temperature_celsius = device.getChipTemperature().average;
        out->leon_css_usage_percent = device.getLeonCssCpuUsage().average * 100.0f;
        out->leon_mss_usage_percent = device.getLeonMssCpuUsage().average * 100.0f;
        const auto ddr = device.getDdrMemoryUsage();
        out->ddr_used_bytes = ddr.used;
        out->ddr_total_bytes = ddr.total;
        return static_cast<int>(DAI_OK);
    });
}

}  // extern "C"
