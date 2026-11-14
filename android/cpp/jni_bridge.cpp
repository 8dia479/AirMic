#include "audio_engine.hpp"
#include <jni.h>
#include <exception>
#include <mutex>

namespace {
// This mutex protects CONTROL calls only. Oboe and sender threads NEVER take it.
std::mutex control;
airmic::android::AudioEngine engine;
}
extern "C" JNIEXPORT jstring JNICALL
Java_io_airmic_NativeBridge_start(JNIEnv* env, jobject, jstring host, jint port) {
    std::lock_guard lock(control);
    if (!host || port < 1 || port > 65535) return env->NewStringUTF("Invalid destination");
    const char* text = env->GetStringUTFChars(host, nullptr);
    if (!text) return nullptr; // JVM has a pending allocation exception.
    std::string address;
    try { address = text; } catch (...) { env->ReleaseStringUTFChars(host, text); return env->NewStringUTF("Out of memory"); }
    env->ReleaseStringUTFChars(host, text);
    try {
        const auto error = engine.start(address, static_cast<std::uint16_t>(port));
        return error.empty() ? nullptr : env->NewStringUTF(error.c_str());
    } catch (const std::exception& e) { engine.stop(); return env->NewStringUTF(e.what()); }
}
extern "C" JNIEXPORT void JNICALL
Java_io_airmic_NativeBridge_stop(JNIEnv*, jobject) {
    std::lock_guard lock(control); engine.stop();
}
extern "C" JNIEXPORT jboolean JNICALL
Java_io_airmic_NativeBridge_configure(JNIEnv*, jobject, jboolean enabled, jboolean gate,
                                     jfloat gain, jfloat low, jfloat mid, jfloat high) {
    std::lock_guard lock(control);
    airmic::DspConfig c;
    c.enabled = enabled; c.noiseGate = gate; c.inputGainDb = gain;
    c.lowEqDb = low; c.midEqDb = mid; c.highEqDb = high;
    return engine.configure(c) ? JNI_TRUE : JNI_FALSE;
}
extern "C" JNIEXPORT jlongArray JNICALL
Java_io_airmic_NativeBridge_stats(JNIEnv* env, jobject) {
    std::lock_guard lock(control);
    const auto values = engine.snapshot();
    jlong converted[17];
    for (std::size_t i = 0; i < values.size(); ++i) converted[i] = static_cast<jlong>(values[i]);
    auto array = env->NewLongArray(17);
    if (array) env->SetLongArrayRegion(array, 0, 17, converted);
    return array;
}
extern "C" JNIEXPORT jstring JNICALL
Java_io_airmic_NativeBridge_lastError(JNIEnv* env, jobject) {
    std::lock_guard lock(control); const auto error = engine.lastError();
    return env->NewStringUTF(error.c_str());
}
