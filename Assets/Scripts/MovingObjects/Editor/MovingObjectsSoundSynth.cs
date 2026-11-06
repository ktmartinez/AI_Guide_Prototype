using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Tools > Moving Objects > Generate Synthesized Sounds: writes the movers' sounds as mono WAVs to Assets/Prefabs/Moving Objects/Audio,
// so every mover has audio without any downloads. Loops are seamless. The scene setup prefers an .mp3/.ogg with the same name over
// these .wav files, so a real recording can replace any of them later: drop it in next to the .wav and re-run the setup.
// Everything is seeded, so regenerating gives identical files.
public static class MovingObjectsSoundSynth
{
    private const int Rate = 22050;
    private const string Folder = "Assets/Prefabs/Moving Objects/Audio/";
    private const double Tau = Math.PI * 2.0;

    [MenuItem("Tools/Moving Objects/Generate Synthesized Sounds")]
    public static void GenerateAll()
    {
        var sounds = new (string name, Func<float[]> make, bool loop)[]
        {
            ("hover-hum-loop", HoverHum, true),
            ("ufo-hum-loop", UfoHum, true),
            ("thruster-burst", ThrusterBurst, false),
            ("magic-shimmer-loop", MagicShimmer, true),
            ("broom-whoosh-loop", BroomWhoosh, true),
            ("alien-chirp", AlienChirp, false),
            ("ghost-moan", GhostMoan, false),
            ("dragon-wings-loop", DragonWings, true),
            ("dragon-roar", DragonRoar, false),
            ("creature-growl", CreatureGrowl, false),
            ("horse-walk-loop", HorseWalk, true),
            ("horse-cart-loop", HorseCart, true),
            ("horse-whinny", HorseWhinny, false),
            ("cow-moo", CowMoo, false),
        };

        foreach (var sound in sounds)
        {
            float[] samples = Normalize(sound.make(), 0.8f);
            string path = Folder + sound.name + ".wav";
            WriteWav(path, samples);
            float rms = 0f;
            foreach (float s in samples) rms += s * s;
            rms = Mathf.Sqrt(rms / samples.Length);
            string seam = sound.loop ? $", loop seam jump {Mathf.Abs(samples[samples.Length - 1] - samples[0]):0.000}" : "";
            Debug.Log($"[Sound Synth] {path}: {samples.Length / (float)Rate:0.00}s, rms {rms:0.000}{seam}");
        }

        AssetDatabase.Refresh();
        foreach (var sound in sounds)
        {
            if (AssetImporter.GetAtPath(Folder + sound.name + ".wav") is AudioImporter importer)
            {
                importer.forceToMono = true;
                AudioImporterSampleSettings settings = importer.defaultSampleSettings;
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settings.quality = 0.6f;
                importer.defaultSampleSettings = settings;
                importer.SaveAndReimport();
            }
        }
    }

    // ---------- Sci-fi ----------

    // Low engine drone with breathing air noise and a faint turbine whine
    private static float[] HoverHum()
    {
        return Loop(4f, 0.5f, n =>
        {
            float[] x = new float[n];
            Biquad air = new Biquad(), body = Biquad.Lowpass(1800, 0.7);
            double whinePhase = 0;
            Rng rng = new Rng(11);
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                if (i % 32 == 0) air.SetBandpass(380 + 120 * Math.Sin(Tau * 0.25 * t), 1.2);
                double hum = Math.Sin(Tau * 58 * t) + 0.55 * Math.Sin(Tau * 116.3 * t) + 0.35 * Math.Sin(Tau * 174.2 * t) + 0.2 * Math.Sin(Tau * 232.9 * t);
                hum *= 1 + 0.15 * Math.Sin(Tau * 0.5 * t);
                whinePhase += Tau * (1320 + 15 * Math.Sin(Tau * 0.7 * t)) / Rate;
                x[i] = (float)(body.Process(0.6 * hum + 0.9 * air.Process(rng.Noise())) + 0.06 * Math.Sin(whinePhase));
            }
            return x;
        });
    }

    // Classic flying-saucer warble: two vibrato tones, ring modulated, over a low hum
    private static float[] UfoHum()
    {
        return Loop(4f, 0.4f, n =>
        {
            float[] x = new float[n];
            Biquad soften = Biquad.Lowpass(3500, 0.7);
            double p1 = 0, p2 = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                p1 += Tau * (520 + 140 * Math.Sin(Tau * 5.5 * t)) / Rate;
                p2 += Tau * (785 + 90 * Math.Sin(Tau * 3.7 * t + 1)) / Rate;
                double tone = (Math.Sin(p1) + 0.4 * Math.Sin(p2)) * (0.75 + 0.25 * Math.Sin(Tau * 37 * t));
                double low = 0.35 * Math.Sin(Tau * 70 * t) + 0.15 * Math.Sin(Tau * 140 * t);
                x[i] = (float)soften.Process(tone * (0.8 + 0.2 * Math.Sin(Tau * 0.25 * t)) * 0.6 + low);
            }
            return x;
        });
    }

    // Roaring noise that starts bright and settles into a rumble, with crackle
    private static float[] ThrusterBurst()
    {
        int n = Samples(2.0f);
        float[] x = new float[n];
        Biquad roar = new Biquad(), rumble = Biquad.Lowpass(120, 0.8), crackle = Biquad.Bandpass(2500, 2);
        Rng rng = new Rng(23);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            if (i % 32 == 0) roar.SetLowpass(600 + 2600 * Math.Exp(-t / 0.5), 0.8);
            double env = Math.Min(1, t / 0.04) * (t < 0.5 ? 1 : Math.Exp(-(t - 0.5) / 0.45));
            double pop = rng.Chance(0.004) ? rng.Range(-1, 1) * 4 : 0;
            x[i] = (float)(env * (0.8 * roar.Process(rng.Noise()) + 1.6 * rumble.Process(rng.Noise()) + crackle.Process(pop)));
        }
        return x;
    }

    // ---------- Magic ----------

    // Random high sparkles over a soft major chord; sparkles wrap around the loop point so it never clicks
    private static float[] MagicShimmer()
    {
        const float length = 4f;
        int n = Samples(length);
        float[] x = new float[n];
        double[] notes = { 1568, 1760, 2093, 2349, 2637, 3136, 3520, 4186, 4699, 5274 };
        Rng rng = new Rng(31);
        for (int e = 0; e < 46; e++)
        {
            double start = rng.Range(0, length), f = notes[rng.Index(notes.Length)], decay = rng.Range(0.08, 0.35), amp = rng.Range(0.15, 0.45);
            int count = Samples((float)(decay * 5));
            for (int k = 0; k < count; k++)
            {
                double dt = k / (double)Rate;
                int index = (Samples((float)start) + k) % n;
                x[index] += (float)(amp * Math.Min(1, dt / 0.003) * Math.Exp(-dt / decay) * Math.Sin(Tau * f * dt));
            }
        }
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate; // 523.25, 659.25 and 784 Hz all fit a whole number of cycles in 4 s, so the pad loops cleanly
            double pad = Math.Sin(Tau * 523.25 * t) + Math.Sin(Tau * 659.25 * t) + Math.Sin(Tau * 784 * t);
            x[i] += (float)(0.05 * pad * (0.7 + 0.3 * Math.Sin(Tau * 0.5 * t)));
        }
        return x;
    }

    // A broom rushing past: band-passed wind that swells and rises in pitch once per loop
    private static float[] BroomWhoosh()
    {
        const float length = 2f;
        return Loop(length, 0.2f, n =>
        {
            float[] x = new float[n];
            Biquad low = new Biquad(), high = new Biquad();
            Rng rng = new Rng(41);
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double pass = Math.Pow(Math.Sin(Math.PI * t / length), 2);
                if (i % 32 == 0)
                {
                    low.SetBandpass(350 + 900 * pass, 1.5);
                    high.SetBandpass(2 * (350 + 900 * pass), 3);
                }
                double noise = rng.Noise();
                x[i] = (float)((0.25 + 0.75 * pass) * (low.Process(noise) + 0.3 * high.Process(noise)));
            }
            return x;
        });
    }

    // A wobbly "oooOOOooo" with a spooky echo
    private static float[] GhostMoan()
    {
        int n = Samples(3.6f);
        float[] x = new float[n];
        Biquad f1 = Biquad.Bandpass(380, 3), f2 = Biquad.Bandpass(900, 4), breath = Biquad.Lowpass(1200, 0.7);
        Rng rng = new Rng(53);
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            double pitch = t < 1.2 ? 175 + 85 * Math.Sin(Math.PI / 2 * t / 1.2) : 260 - 110 * Smooth(Math.Min(1, (t - 1.2) / 1.8));
            phase += Tau * pitch * (1 + 0.035 * Math.Sin(Tau * 5.2 * t)) / Rate;
            double source = Math.Sin(phase) + 0.35 * Math.Sin(2 * phase) + 0.12 * Math.Sin(3 * phase);
            double env = Smooth(Math.Min(1, t / 0.7)) * (t < 1.9 ? 1 : Math.Max(0, 1 - (t - 1.9) / 1.1));
            x[i] = (float)(env * (f1.Process(source) + 0.4 * f2.Process(source) + 0.3 * source + 0.15 * breath.Process(rng.Noise())));
        }
        return Echo(x, (0.21f, 0.32f), (0.37f, 0.2f));
    }

    // ---------- Creatures ----------

    // Heavy downstroke "whoomps" with feathery flutter and lighter upstrokes, two flaps per loop
    private static float[] DragonWings()
    {
        const float length = 2f;
        int n = Samples(length);
        float[] x = new float[n];
        Rng rng = new Rng(61);
        foreach ((double time, double amp, double cutoff) in new[] { (0.0, 1.0, 220.0), (0.5, 0.35, 300.0), (1.0, 1.0, 220.0), (1.5, 0.35, 300.0) })
        {
            Biquad thump = Biquad.Lowpass(cutoff, 0.9), flutter = Biquad.Bandpass(650, 2);
            int count = Samples(0.6f);
            for (int k = 0; k < count; k++)
            {
                double dt = k / (double)Rate;
                double env = Smooth(Math.Min(1, dt / 0.07)) * Math.Exp(-Math.Max(0, dt - 0.07) / 0.18);
                double sample = 3 * thump.Process(rng.Noise()) + 0.35 * flutter.Process(rng.Noise()) * (0.5 + 0.5 * Math.Sin(Tau * 22 * dt));
                x[(Samples((float)time) + k) % n] += (float)(amp * env * sample);
            }
        }
        return x;
    }

    // A long falling roar: a rough low voice through mouth-like resonances, with a rattling tremolo
    private static float[] DragonRoar()
    {
        int n = Samples(2.8f);
        float[] x = new float[n];
        Biquad a = Biquad.Bandpass(550, 3), b = Biquad.Bandpass(1150, 4), c = Biquad.Bandpass(2500, 5), body = Biquad.Lowpass(300, 0.7);
        Rng rng = new Rng(71);
        double phase = 0, jitter = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            jitter = 0.999 * jitter + 0.004 * rng.Noise();
            double pitch = (90 + 45 * Math.Exp(-t / 0.9)) * (1 + 2 * jitter);
            phase += pitch / Rate;
            double saw = 2 * (phase - Math.Floor(phase + 0.5));
            double source = 0.7 * saw + 0.5 * rng.Noise();
            double voice = a.Process(source) + 0.7 * b.Process(source) + 0.35 * c.Process(source) + 0.6 * body.Process(source);
            voice *= 1 - 0.3 * (0.5 + 0.5 * Math.Sin(Tau * 26 * t));
            double env = Smooth(Math.Min(1, t / 0.18)) * (t < 1.6 ? 1 : Math.Exp(-(t - 1.6) / 0.35));
            x[i] = (float)(env * Math.Tanh(3 * voice) / Math.Tanh(3));
        }
        return Echo(x, (0.18f, 0.25f));
    }

    private static float[] CreatureGrowl()
    {
        int n = Samples(1.9f);
        float[] x = new float[n];
        Biquad tone = Biquad.Lowpass(700, 0.8);
        Rng rng = new Rng(83);
        double phase = 0, jitter = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            jitter = 0.998 * jitter + 0.006 * rng.Noise();
            phase += 78 * (1 + 2 * jitter) / Rate;
            double saw = 2 * (phase - Math.Floor(phase + 0.5));
            double voice = tone.Process(0.8 * saw + 0.4 * rng.Noise()) * (1 - 0.6 * (0.5 + 0.5 * Math.Sin(Tau * 17 * t)));
            double env = Smooth(Math.Min(1, t / 0.25)) * (t < 1.3 ? 1 : Math.Max(0, 1 - (t - 1.3) / 0.6));
            x[i] = (float)(env * Math.Tanh(4 * voice));
        }
        return x;
    }

    // A few quick bubbly pitch sweeps, ring modulated so it sounds unearthly
    private static float[] AlienChirp()
    {
        int n = Samples(1.3f);
        float[] x = new float[n];
        foreach ((double start, double duration, double from, double to) in new[]
                 { (0.0, 0.14, 900.0, 1900.0), (0.2, 0.12, 1700.0, 800.0), (0.38, 0.16, 1100.0, 2300.0), (0.62, 0.22, 2000.0, 1200.0), (0.92, 0.18, 1300.0, 2100.0) })
        {
            double phase = 0;
            int first = Samples((float)start), count = Samples((float)duration);
            for (int k = 0; k < count && first + k < n; k++)
            {
                double p = k / (double)count, t = (first + k) / (double)Rate;
                phase += Tau * (from + (to - from) * p + 70 * Math.Sin(Tau * 32 * t)) / Rate;
                double tone = (Math.Sin(phase) + 0.3 * Math.Sin(2 * phase)) * (0.7 + 0.3 * Math.Sin(Tau * 230 * t));
                x[first + k] += (float)(Math.Pow(Math.Sin(Math.PI * p), 0.6) * tone);
            }
        }
        return x;
    }

    // ---------- Farm ----------

    // A walking horse's four-beat gait on soft ground, two strides per loop
    private static float[] HorseWalk()
    {
        const float length = 2.2f;
        float[] x = new float[Samples(length)];
        Rng rng = new Rng(97);
        foreach (double stride in new[] { 0.0, 1.1 })
            foreach (double beat in new[] { 0.0, 0.29, 0.55, 0.83 })
                AddWrapped(x, stride + beat + rng.Range(-0.01, 0.01), Hoof(rng, rng.Range(0.7, 1.0), 1.0, 1400, 0.45, false));
        return x;
    }

    // Hooves on a stone path, wheels rumbling, a creaking axle and loose rattles
    private static float[] HorseCart()
    {
        const float length = 2.2f;
        float[] x = Loop(length, 0.3f, n =>
        {
            float[] rumble = new float[n];
            Biquad low = Biquad.Lowpass(140, 0.8);
            Rng noise = new Rng(101);
            for (int i = 0; i < n; i++)
                rumble[i] = (float)(0.5 * low.Process(noise.Noise()) * (0.8 + 0.2 * Math.Sin(Tau * 3 * i / (double)Rate / length)));
            return rumble;
        });

        Rng rng = new Rng(103);
        foreach (double stride in new[] { 0.0, 1.1 })
            foreach (double beat in new[] { 0.0, 0.29, 0.55, 0.83 })
                AddWrapped(x, stride + beat + rng.Range(-0.01, 0.01), Hoof(rng, rng.Range(0.7, 1.0), 0.7, 2200, 0.8, true));

        // Creak: a squeaky frequency-modulated tone through a resonance
        int creakLength = Samples(0.4f);
        float[] creak = new float[creakLength];
        Biquad wood = Biquad.Bandpass(900, 2);
        double phase = 0;
        for (int k = 0; k < creakLength; k++)
        {
            double p = k / (double)creakLength, t = k / (double)Rate;
            phase += Tau * (480 + 140 * p + 70 * Math.Sin(Tau * 38 * t)) / Rate;
            creak[k] = (float)(0.25 * Math.Sin(Math.PI * p) * wood.Process(Math.Sin(phase)) * 3);
        }
        AddWrapped(x, 1.3, creak);

        for (int r = 0; r < 10; r++)
        {
            Biquad tick = Biquad.Bandpass(3200, 3);
            float[] rattle = new float[Samples(0.04f)];
            double amp = rng.Range(0.08, 0.15);
            for (int k = 0; k < rattle.Length; k++)
                rattle[k] = (float)(amp * 4 * tick.Process(k == 0 ? 1 : 0) * Math.Exp(-k / (double)Rate / 0.006));
            AddWrapped(x, rng.Range(0, length), rattle);
        }
        return x;
    }

    // One hoof strike: a dull thump, a hard click and some grit (a second click 10 ms later on stone for the "clip-clop")
    private static float[] Hoof(Rng rng, double amp, double thumpAmp, double clickFreq, double clickAmp, bool doubleClick)
    {
        float[] hit = new float[Samples(0.25f)];
        Biquad click = Biquad.Bandpass(clickFreq, 1.5), grit = Biquad.Lowpass(900, 0.7);
        double phase = 0;
        for (int k = 0; k < hit.Length; k++)
        {
            double dt = k / (double)Rate;
            phase += Tau * (65 + 55 * Math.Exp(-dt / 0.03)) / Rate;
            double thump = thumpAmp * Math.Sin(phase) * Math.Exp(-dt / 0.045);
            double clickEnv = Math.Exp(-dt / 0.012) + (doubleClick && dt > 0.01 ? 0.5 * Math.Exp(-(dt - 0.01) / 0.012) : 0);
            double sample = thump + clickAmp * 2 * click.Process(rng.Noise()) * clickEnv + 0.25 * grit.Process(rng.Noise()) * Math.Exp(-dt / 0.05);
            hit[k] = (float)(amp * sample);
        }
        return hit;
    }

    // A whinny: a nasal voice that jumps up then falls with a fast, widening vibrato, ending in a snort
    private static float[] HorseWhinny()
    {
        int n = Samples(1.7f);
        float[] x = new float[n];
        Biquad nose = Biquad.Bandpass(1100, 3), bright = Biquad.Bandpass(2600, 4), breath = Biquad.Bandpass(3000, 1), snort = Biquad.Lowpass(1500, 0.7);
        Rng rng = new Rng(109);
        double phase = 0, vibratoPhase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            double p = Math.Min(1, t / 1.35);
            double pitch = t < 0.12 ? 600 + 350 * t / 0.12 : 950 - 530 * Smooth(Math.Min(1, (t - 0.12) / 1.23));
            vibratoPhase += Tau * (9 + 6 * p) / Rate;
            double vibrato = Math.Sin(vibratoPhase);
            phase += Tau * pitch * (1 + (0.07 + 0.05 * p) * vibrato) / Rate;
            double source = 0;
            for (int h = 1; h <= 8; h++) source += Math.Sin(h * phase) / h;
            double voice = (nose.Process(source) + 0.5 * bright.Process(source) + 0.2 * source) * (1 - 0.35 * (0.5 + 0.5 * vibrato));
            double env = Math.Min(1, t / 0.04) * (t < 1.0 ? 1 : Math.Max(0, 1 - (t - 1.0) / 0.35));
            double snortEnv = t > 1.45 ? Math.Exp(-(t - 1.45) / 0.06) : 0;
            x[i] = (float)(env * (voice + 0.1 * breath.Process(rng.Noise())) + 0.6 * snortEnv * snort.Process(rng.Noise()) * 3);
        }
        return x;
    }

    // "Mmmoooooo": a low voice that opens from a hum into an "oo"/"ow" vowel and closes again
    private static float[] CowMoo()
    {
        int n = Samples(2.0f);
        float[] x = new float[n];
        Biquad f1 = new Biquad(), f2 = new Biquad(), nasal = Biquad.Lowpass(250, 0.7), breath = Biquad.Lowpass(800, 0.7);
        Rng rng = new Rng(113);
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            double pitch = (t < 0.4 ? 98 + 14 * t / 0.4 : 112 - 22 * Smooth((t - 0.4) / 1.6)) * (1 + 0.015 * Math.Sin(Tau * 4 * t));
            phase += Tau * pitch / Rate;
            double source = 0;
            for (int h = 1; h <= 30 && h * pitch < 4000; h++) source += Math.Sin(h * phase) / Math.Pow(h, 1.2);
            double open = t < 0.25 ? 0 : t < 0.9 ? 0.4 * Smooth((t - 0.25) / 0.65) : t < 1.6 ? 0.4 + 0.6 * Smooth((t - 0.9) / 0.7) : 1 - Smooth((t - 1.6) / 0.4);
            if (i % 32 == 0)
            {
                f1.SetBandpass(300 + 300 * open, 5);
                f2.SetBandpass(800 + 200 * open, 6);
            }
            double mouth = Smooth(Math.Min(1, t / 0.25));
            double voice = mouth * (f1.Process(source) + 0.5 * f2.Process(source)) + 0.8 * nasal.Process(source);
            double env = Smooth(Math.Min(1, t / 0.2)) * (t < 1.65 ? 1 : Math.Max(0, 1 - (t - 1.65) / 0.35));
            x[i] = (float)(env * (voice + 0.05 * breath.Process(rng.Noise())));
        }
        return x;
    }

    // ---------- Helpers ----------

    private static int Samples(float seconds) => Mathf.RoundToInt(seconds * Rate);

    private static double Smooth(double p) { p = Math.Max(0, Math.Min(1, p)); return p * p * (3 - 2 * p); }

    // Renders length + crossfade, then fades the extra tail into the start so the loop point is seamless
    private static float[] Loop(float length, float crossfade, Func<int, float[]> render)
    {
        int n = Samples(length), fade = Samples(crossfade);
        float[] raw = render(n + fade);
        float[] loop = new float[n];
        Array.Copy(raw, loop, n);
        for (int i = 0; i < fade; i++)
        {
            double p = i / (double)fade;
            loop[i] = (float)(raw[i] * Math.Sin(p * Math.PI / 2) + raw[n + i] * Math.Cos(p * Math.PI / 2));
        }
        return loop;
    }

    private static void AddWrapped(float[] target, double seconds, float[] sound)
    {
        int start = ((Samples((float)seconds) % target.Length) + target.Length) % target.Length;
        for (int k = 0; k < sound.Length; k++) target[(start + k) % target.Length] += sound[k];
    }

    private static float[] Echo(float[] dry, params (float delay, float gain)[] taps)
    {
        float[] wet = (float[])dry.Clone();
        foreach ((float delay, float gain) in taps)
        {
            int offset = Samples(delay);
            for (int i = offset; i < wet.Length; i++) wet[i] += gain * wet[i - offset];
        }
        return wet;
    }

    // Similar loudness for every clip (RMS 0.22), without letting any peak go past the given level
    private static float[] Normalize(float[] x, float peak)
    {
        float max = 0f, sum = 0f;
        foreach (float s in x) { max = Mathf.Max(max, Mathf.Abs(s)); sum += s * s; }
        float rms = Mathf.Sqrt(sum / x.Length);
        if (max <= 0f) return x;
        float gain = Mathf.Min(peak / max, 0.22f / Mathf.Max(rms, 1e-6f));
        for (int i = 0; i < x.Length; i++) x[i] *= gain;
        return x;
    }

    private static void WriteWav(string assetPath, float[] samples)
    {
        using (BinaryWriter w = new BinaryWriter(File.Create(assetPath)))
        {
            int dataBytes = samples.Length * 2;
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + dataBytes); w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(dataBytes);
            foreach (float s in samples) w.Write((short)Mathf.Clamp(Mathf.RoundToInt(s * 32767f), -32768, 32767));
        }
    }

    private class Rng
    {
        private readonly System.Random random;
        public Rng(int seed) { random = new System.Random(seed); }
        public double Noise() => random.NextDouble() * 2 - 1;
        public double Range(double min, double max) => min + random.NextDouble() * (max - min);
        public int Index(int count) => random.Next(count);
        public bool Chance(double p) => random.NextDouble() < p;
    }

    // RBJ cookbook biquad (transposed direct form II)
    private class Biquad
    {
        private double b0, b1, b2, a1, a2, z1, z2;

        public static Biquad Lowpass(double f, double q) { Biquad b = new Biquad(); b.SetLowpass(f, q); return b; }
        public static Biquad Bandpass(double f, double q) { Biquad b = new Biquad(); b.SetBandpass(f, q); return b; }

        public void SetLowpass(double f, double q)
        {
            double w = Tau * Math.Min(f, Rate * 0.45) / Rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
            b0 = (1 - cos) / 2 / a0; b1 = (1 - cos) / a0; b2 = b0; a1 = -2 * cos / a0; a2 = (1 - alpha) / a0;
        }

        public void SetBandpass(double f, double q)
        {
            double w = Tau * Math.Min(f, Rate * 0.45) / Rate, alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
            b0 = alpha / a0; b1 = 0; b2 = -alpha / a0; a1 = -2 * Math.Cos(w) / a0; a2 = (1 - alpha) / a0;
        }

        public double Process(double x)
        {
            double y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }
    }
}
