using System;
using System.Collections.Generic;
using UnityEngine;

namespace AutoWar
{
    // ========================
    // AUDIO SYNTH
    // All sound is generated in code (no audio files): a warm pad, soft low arpeggio, sparse melody,
    // plus one-shot effects. Voices are mixed in OnAudioFilterRead with a shared echo.
    // ========================
    [RequireComponent(typeof(AudioSource))]
    public class AudioSynth : MonoBehaviour
    {
        enum Env { Pad, Pluck, Tone, Noise }
        enum Wave { Sine, Triangle, Saw, Noise }

        class Voice
        {
            public long start;          // sample index when it begins
            public float dur;           // seconds (meaning depends on env)
            public float f0, f1;        // start / end frequency (f1 > 0 = exponential sweep over dur)
            public float vol, lpCut, detuneCents;
            public Wave wave; public Env env; public bool music;
            public double phase; public float lp1, lp2, bpLow;
            public float length;        // total audible seconds
        }

        readonly List<Voice> _voices = new List<Voice>();
        readonly object _lock = new object();
        System.Random _rng = new System.Random(7);          // main thread only
        readonly System.Random _noiseRng = new System.Random(11); // audio thread only
        int _sr = 48000;
        long _samples;   // advanced by the audio thread; the main thread schedules relative to it
        float[] _delay; int _delayIdx; float _echoLp;
        float _musicGain = 0.5f, _sfxGain = 0.8f;
        public bool musicOn = true, sfxOn = true;

        // music scheduling (seconds on the audio clock)
        string _musicLevel = "field";
        int _chordIdx, _melIdx = 3;
        double _nextChordTime, _nextPluckTime, _nextSchedule;
        readonly List<KeyValuePair<double, int[]>> _chordLog = new List<KeyValuePair<double, int[]>>();
        const double ChordLen = 8;
        bool _started;

        static readonly Dictionary<string, int[][]> Progs = new Dictionary<string, int[][]>
        {
            { "field", new[] { new[] { 45, 57, 60, 64 }, new[] { 41, 57, 60, 65 }, new[] { 48, 55, 60, 64 }, new[] { 43, 55, 59, 62 } } },
            { "fog",   new[] { new[] { 45, 57, 60, 64 }, new[] { 43, 55, 59, 62 }, new[] { 41, 57, 60, 65 }, new[] { 40, 56, 59, 64 } } }
        };
        static readonly int[] Scale = { 57, 60, 62, 64, 67, 69, 72 };

        double Now { get { return (double)_samples / _sr; } }
        static float Mtof(float m) { return 440f * Mathf.Pow(2, (m - 69) / 12f); }

        // -------------------------------------------------------
        // Awake()
        // Sets up the (silent) looping clip that keeps Unity calling OnAudioFilterRead, and the echo buffer.
        // -------------------------------------------------------
        void Awake()
        {
            _sr = AudioSettings.outputSampleRate;
            _delay = new float[(int)(_sr * 0.46f) + 1];
            var src = GetComponent<AudioSource>();
            var clip = AudioClip.Create("silence", _sr, 1, _sr, false);
            clip.SetData(new float[_sr], 0);
            src.clip = clip; src.loop = true; src.playOnAwake = false; src.spatialBlend = 0;
            src.Play();
            _nextChordTime = 0.3; _nextPluckTime = 3.3;
            _started = true;
        }

        public void SetMusic(bool on) { musicOn = on; _musicGain = on ? 0.5f : 0f; }
        public void SetSfx(bool on) { sfxOn = on; _sfxGain = on ? 0.8f : 0f; }

        public void SetLevel(string key)
        {
            _musicLevel = key; _chordIdx = 0;
        }

        void Update()
        {
            if (!_started) return;
            double now = Now;
            if (now < _nextSchedule) return;
            _nextSchedule = now + 0.3;
            ScheduleMusic(now);
        }

        // ========================
        // MUSIC
        // ========================
        void ScheduleMusic(double now)
        {
            if (_nextChordTime < now) { _nextChordTime = now + 0.3; _nextPluckTime = _nextChordTime + 3; } // resume after a long pause
            var prog = Progs[_musicLevel];
            while (_nextChordTime < now + 2)
            {
                var chord = prog[_chordIdx % 4];
                _chordIdx++;
                _chordLog.Add(new KeyValuePair<double, int[]>(_nextChordTime, chord));
                for (int i = 0; i < chord.Length; i++)
                    Pad(chord[i], _nextChordTime + i * 0.08, (float)ChordLen, i == 0 ? 0.07f : 0.04f);
                for (int i = 1; i < chord.Length; i++)
                    Pluck(chord[i], _nextChordTime + 0.35 + (i - 1) * 0.6, 0.07f);
                _nextChordTime += ChordLen;
            }
            _chordLog.RemoveAll(c => c.Key <= now - 20);
            double chance = _musicLevel == "fog" ? 0.5 : 0.65;
            while (_nextPluckTime < now + 2)
            {
                int[] chord = null; double ct = 0;
                foreach (var c in _chordLog) if (c.Key <= _nextPluckTime) { chord = c.Value; ct = c.Key; }
                if (chord != null && _nextPluckTime - ct > 2.6 && _rng.NextDouble() < chance)
                {
                    int[] steps = { -2, -1, -1, 0, 1, 1, 2 };
                    _melIdx = Mathf.Clamp(_melIdx + steps[_rng.Next(7)], 0, Scale.Length - 1);
                    int m = Scale[_melIdx];
                    if (!Clashes(m, chord)) Pluck(m, _nextPluckTime, 0.07f);
                }
                _nextPluckTime += 0.75 + 0.375 * _rng.Next(3);
            }
        }

        static bool Clashes(int m, int[] chord)
        {
            foreach (var n in chord) { int d = (((m - n) % 12) + 12) % 12; if (d == 1 || d == 11) return true; }
            return false;
        }

        void Add(Voice v, double at)
        {
            v.start = (long)(at * _sr);
            lock (_lock) _voices.Add(v);
        }

        void Pad(int m, double t, float dur, float vol)
        {
            float cut = m < 50 ? 450 : 800;
            float det = (float)(_rng.NextDouble() * 4 - 2);
            Add(new Voice { dur = dur, f0 = Mtof(m), vol = vol, lpCut = cut, wave = Wave.Sine, env = Env.Pad, music = true, detuneCents = det, length = dur + 2.7f }, t);
            Add(new Voice { dur = dur, f0 = Mtof(m), vol = vol, lpCut = cut, wave = Wave.Triangle, env = Env.Pad, music = true, detuneCents = 6 + det, length = dur + 2.7f }, t);
        }

        // Soft, round plucked note in the low-middle register
        void Pluck(int m, double t, float vol)
        {
            Add(new Voice { dur = 2.2f, f0 = Mtof(m), vol = vol, lpCut = 1200, wave = Wave.Sine, env = Env.Pluck, music = true, length = 2.3f }, t);
            Add(new Voice { dur = 2.2f, f0 = Mtof(m), vol = vol, lpCut = 1200, wave = Wave.Triangle, env = Env.Pluck, music = true, detuneCents = 4, length = 2.3f }, t);
        }

        // ========================
        // SOUND EFFECTS
        // ========================
        void Tone(float f0, double t, float dur, Wave w, float vol, float f1 = 0)
        {
            Add(new Voice { dur = dur, f0 = f0, f1 = f1, vol = vol, wave = w, env = Env.Tone, length = dur + 0.05f }, t);
        }
        void NoiseHit(double t, float dur, float freq, float vol)
        {
            Add(new Voice { dur = dur, f0 = freq, vol = vol, wave = Wave.Noise, env = Env.Noise, length = dur + 0.02f }, t);
        }

        public void Sfx(string name)
        {
            if (!_started || !sfxOn) return;
            double t = Now + 0.02;
            switch (name)
            {
                case "drop": Tone(330, t, 0.12f, Wave.Triangle, 0.2f, 520); NoiseHit(t, 0.05f, 1800, 0.05f); break;
                case "remove": Tone(420, t, 0.14f, Wave.Triangle, 0.15f, 220); break;
                case "tab": Tone(520, t, 0.1f, Wave.Sine, 0.1f, 780); break;
                case "run": Tone(220, t, 0.35f, Wave.Saw, 0.05f, 880); Tone(330, t + 0.05, 0.3f, Wave.Sine, 0.1f, 990); break;
                case "step": NoiseHit(t, 0.07f, 500 + (float)_rng.NextDouble() * 250, 0.1f); Tone(90, t, 0.06f, Wave.Sine, 0.09f, 60); break;
                case "spot": { int[] ms = { 72, 76, 81 }; for (int i = 0; i < 3; i++) Tone(Mtof(ms[i]), t + i * 0.13, 0.7f, Wave.Triangle, 0.13f); break; }
                case "win":
                    {
                        int[] ms = { 60, 64, 67, 72, 76 };
                        for (int i = 0; i < ms.Length; i++)
                        {
                            Tone(Mtof(ms[i]), t + i * 0.11, 0.9f, Wave.Triangle, 0.16f);
                            Tone(Mtof(ms[i] + 12), t + i * 0.11, 0.7f, Wave.Sine, 0.05f);
                        }
                        break;
                    }
                case "hit": NoiseHit(t, 0.06f, 900, 0.06f); Tone(180, t, 0.08f, Wave.Triangle, 0.08f, 120); break;
                case "fail": Tone(220, t, 0.25f, Wave.Saw, 0.09f, 150); Tone(165, t + 0.18, 0.4f, Wave.Saw, 0.09f, 110); break;
                case "wait": Tone(392, t, 0.55f, Wave.Sine, 0.06f); Tone(330, t + 0.3, 0.7f, Wave.Sine, 0.05f); break;
            }
        }

        // ========================
        // MIXER (audio thread)
        // ========================
        void OnAudioFilterRead(float[] data, int channels)
        {
            if (_delay == null) return;
            int frames = data.Length / channels;
            lock (_lock)
            {
                for (int f = 0; f < frames; f++)
                {
                    long now = _samples + f;
                    float music = 0, sfx = 0;
                    for (int vi = 0; vi < _voices.Count; vi++)
                    {
                        var v = _voices[vi];
                        long idx = now - v.start;
                        if (idx < 0) continue;
                        float t = idx / (float)_sr;
                        if (t > v.length) continue;
                        float s = Sample(v, t);
                        if (v.music) music += s; else sfx += s;
                    }
                    music *= _musicGain;
                    sfx *= _sfxGain;
                    // shared echo: music goes in fully, effects send a little
                    float echoIn = music + sfx * 0.18f;
                    float d = _delay[_delayIdx];
                    _echoLp += (d - _echoLp) * 0.17f;     // ~1.4 kHz one-pole low-pass
                    _delay[_delayIdx] = echoIn + _echoLp * 0.3f;
                    _delayIdx = (_delayIdx + 1) % _delay.Length;
                    float outS = (music * 0.85f + sfx + _echoLp * 0.3f) * 0.9f;
                    outS = Mathf.Clamp(outS, -1f, 1f);
                    for (int ch = 0; ch < channels; ch++) data[f * channels + ch] = outS;
                }
                _samples += frames;
                for (int i = _voices.Count - 1; i >= 0; i--)
                    if (_samples - _voices[i].start > (long)(_voices[i].length * _sr) + 8) _voices.RemoveAt(i);
            }
        }

        float Sample(Voice v, float t)
        {
            float env;
            switch (v.env)
            {
                case Env.Pad:
                    {
                        float rel = v.dur - 0.5f;
                        if (t < 2.4f) env = t / 2.4f;
                        else if (t < rel) env = 1;
                        else env = Mathf.Max(0, 1 - (t - rel) / 3.1f);
                        break;
                    }
                case Env.Pluck:
                    env = t < 0.03f ? t / 0.03f : Mathf.Exp(-9.21f * (t - 0.03f) / 2.17f);
                    break;
                case Env.Tone:
                    env = t < 0.01f ? t / 0.01f : Mathf.Exp(-9.21f * (t - 0.01f) / Mathf.Max(0.01f, v.dur - 0.01f));
                    break;
                default:
                    env = Mathf.Exp(-9.21f * t / v.dur);
                    break;
            }
            float osc;
            if (v.wave == Wave.Noise)
            {
                float n = (float)(_noiseRng.NextDouble() * 2 - 1);
                // crude band-pass: low-pass at ~1.5f minus low-pass at ~f/1.5
                float a1 = 1 - Mathf.Exp(-2 * Mathf.PI * v.f0 * 1.5f / _sr), a2 = 1 - Mathf.Exp(-2 * Mathf.PI * v.f0 / 1.5f / _sr);
                v.lp1 += (n - v.lp1) * a1; v.bpLow += (n - v.bpLow) * a2;
                osc = (v.lp1 - v.bpLow) * 2f;
            }
            else
            {
                float f = v.f0;
                if (v.f1 > 0) f = v.f0 * Mathf.Pow(v.f1 / v.f0, Mathf.Clamp01(t / v.dur));
                if (v.detuneCents != 0) f *= Mathf.Pow(2, v.detuneCents / 1200f);
                v.phase += f / _sr;
                v.phase -= Math.Floor(v.phase);
                float p = (float)v.phase;
                switch (v.wave)
                {
                    case Wave.Sine: osc = Mathf.Sin(p * 2 * Mathf.PI); break;
                    case Wave.Triangle: osc = 4 * Mathf.Abs(p - 0.5f) - 1; break;
                    default: osc = 2 * p - 1; break;
                }
                if (v.lpCut > 0)
                {
                    float a = 1 - Mathf.Exp(-2 * Mathf.PI * v.lpCut / _sr);
                    v.lp2 += (osc - v.lp2) * a;
                    osc = v.lp2;
                }
            }
            return osc * env * v.vol;
        }
    }
}
