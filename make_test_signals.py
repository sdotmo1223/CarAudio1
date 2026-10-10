#!/usr/bin/env python3
"""
Generate a test-signal pack for measuring the car.

Files produced (44.1 kHz, 16-bit, stereo):
  1. pink-noise-30s.wav        flat per octave - the one to use with an RTA
  2. sweep-full-20-20k-20s.wav log sine sweep, full range
  3. sweep-bass-20-200hz-15s.wav log sine sweep, bass only (sub setup)
  4. tones-third-octave.wav    1/3-octave tone sequence, 20 Hz to 20 kHz

All peak-normalised to -6 dBFS so they will not clip, with short fades on the
tone sequence to avoid clicks.
"""
import os
import wave

import numpy as np

FS = 44100
OUTDIR = 'test-signals'
PEAK = 0.5          # -6 dBFS


def write_wav(path, mono, fs=FS):
    """Write a mono float array as a stereo 16-bit PCM wav."""
    x = np.clip(mono, -1.0, 1.0)
    pcm = (x * 32767.0).astype('<i2')
    stereo = np.repeat(pcm[:, None], 2, axis=1).ravel()
    with wave.open(path, 'wb') as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(fs)
        w.writeframes(stereo.tobytes())
    return os.path.getsize(path)


def normalise(x, peak=PEAK):
    m = np.max(np.abs(x))
    return x * (peak / m) if m > 0 else x


def pink_noise(seconds, seed=1):
    """White noise shaped to -3 dB/octave (equal energy per octave)."""
    rng = np.random.default_rng(seed)
    n = int(FS * seconds)
    white = rng.standard_normal(n)
    spec = np.fft.rfft(white)
    f = np.fft.rfftfreq(n, 1.0 / FS)
    scale = np.ones_like(f)
    scale[1:] = 1.0 / np.sqrt(f[1:])     # amplitude ~ 1/sqrt(f) => power ~ 1/f
    scale[0] = 0.0                        # kill DC
    pink = np.fft.irfft(spec * scale, n)
    return normalise(pink)


def log_sweep(f0, f1, seconds):
    n = int(FS * seconds)
    t = np.arange(n) / FS
    k = np.log(f1 / f0)
    phase = 2.0 * np.pi * f0 * seconds / k * (np.exp(t * k / seconds) - 1.0)
    x = np.sin(phase)
    # short fades so it never clicks
    fade = int(FS * 0.05)
    x[:fade] *= np.linspace(0, 1, fade)
    x[-fade:] *= np.linspace(1, 0, fade)
    return normalise(x)


def tone_sequence(f_lo=20.0, f_hi=20000.0, per_tone=1.5, gap=0.25):
    """1/3-octave steps with a short silence and fades between them."""
    centres = []
    f = f_lo
    while f <= f_hi:
        centres.append(f)
        f *= 2 ** (1 / 3)
    parts = []
    ramp = int(FS * 0.02)
    for fc in centres:
        n = int(FS * per_tone)
        t = np.arange(n) / FS
        tone = np.sin(2 * np.pi * fc * t)
        tone[:ramp] *= np.linspace(0, 1, ramp)
        tone[-ramp:] *= np.linspace(1, 0, ramp)
        parts.append(tone)
        parts.append(np.zeros(int(FS * gap)))
    return normalise(np.concatenate(parts)), centres


def main():
    os.makedirs(OUTDIR, exist_ok=True)
    made = []

    p = f'{OUTDIR}/pink-noise-30s.wav'
    made.append((p, write_wav(p, pink_noise(30.0))))

    p = f'{OUTDIR}/sweep-full-20-20k-20s.wav'
    made.append((p, write_wav(p, log_sweep(20.0, 20000.0, 20.0))))

    p = f'{OUTDIR}/sweep-bass-20-200hz-15s.wav'
    made.append((p, write_wav(p, log_sweep(20.0, 200.0, 15.0))))

    seq, centres = tone_sequence()
    p = f'{OUTDIR}/tones-third-octave.wav'
    made.append((p, write_wav(p, seq)))

    print('generated:')
    for p, size in made:
        print(f'  {p:<44} {size/1e6:6.2f} MB')

    print(f'\n1/3-octave tone centres ({len(centres)} tones, '
          f'{1.5:.1f}s each + gap):')
    print('  ' + ', '.join(f'{c:.0f}' for c in centres))


if __name__ == '__main__':
    main()
