plugins { id("org.jetbrains.kotlin.jvm") }

kotlin {
    jvmToolchain(17)
    sourceSets.main {
        // Compile the EXACT production Kotlin files, not a port or copied DSP.
        kotlin.srcDir("../app/src/main/java")
        kotlin.include("io/airmic/AudioProtocol.kt", "io/airmic/DspProcessor.kt", "io/airmic/DspStages.kt",
            "io/airmic/NoiseSuppressor.kt", "io/airmic/Radix2Fft.kt")
    }
}
dependencies { testImplementation("junit:junit:4.13.2") }
tasks.test { useJUnit() }
