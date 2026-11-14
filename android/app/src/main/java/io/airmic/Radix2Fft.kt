// SPDX-License-Identifier: MIT
package io.airmic

import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.sin

// Fixed plan; callers own the work arrays. No tables, complex objects or scratch
// allocations are created by transform(). Used only by the DSP sender worker.
internal class Radix2Fft(val size: Int) {
    private val reverse = IntArray(size)
    private val cosine = DoubleArray(size / 2)
    private val sine = DoubleArray(size / 2)
    init {
        require(size >= 2 && (size and (size - 1)) == 0)
        val bits = Integer.numberOfTrailingZeros(size)
        for (i in 0 until size) reverse[i] = Integer.reverse(i) ushr (32 - bits)
        for (i in cosine.indices) {
            val angle = 2.0 * PI * i / size
            cosine[i] = cos(angle); sine[i] = sin(angle)
        }
    }
    fun transform(real: DoubleArray, imaginary: DoubleArray, inverse: Boolean = false) {
        require(real.size == size && imaginary.size == size)
        for (i in 0 until size) {
            val j = reverse[i]
            if (j > i) {
                val r = real[i]; real[i] = real[j]; real[j] = r
                val q = imaginary[i]; imaginary[i] = imaginary[j]; imaginary[j] = q
            }
        }
        var length = 2
        while (length <= size) {
            val half = length / 2; val step = size / length
            var start = 0
            while (start < size) {
                for (j in 0 until half) {
                    val c = cosine[j * step]
                    val s = if (inverse) sine[j * step] else -sine[j * step]
                    val a = start + j; val b = a + half
                    val r = real[b] * c - imaginary[b] * s
                    val q = real[b] * s + imaginary[b] * c
                    real[b] = real[a] - r; imaginary[b] = imaginary[a] - q
                    real[a] += r; imaginary[a] += q
                }
                start += length
            }
            length *= 2
        }
        if (inverse) for (i in 0 until size) { real[i] /= size; imaginary[i] /= size }
    }
}
