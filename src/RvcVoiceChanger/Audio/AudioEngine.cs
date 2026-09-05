using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Runtime;

namespace RvcVoiceChanger.Audio;

public enum EngineState { Stopped, Starting, Running, Error }

/// <summary>
/// Аудиотракт реального времени:
/// микрофон (WASAPI capture) -> mono 48 кГц float -> блоки по ReadChunkSize*128 семплов
/// -> python-воркер (RVC) -> буфер вывода -> виртуальный микрофон и/или наушники.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48000;

    private readonly AppSettings _settings;
    private readonly PythonBridge _bridge;

    private WasapiCapture? _capture;
    private WasapiOut? _virtualMicOut;
    private WasapiOut? _monitorOut;
    private BufferedWaveProvider? _virtualMicBuffer;
    private BufferedWaveProvider? _monitorBuffer;

    private readonly object _sync = new();
    private float[] _inputAccumulator = Array.Empty<float>();
    private int _accumulated;
    private int _blockSize = 96 * 128;

    public EngineState State { get; private set; } = EngineState.Stopped;

    /// <summary>Уровень входа 0..1 для индикатора.</summary>
    public event Action<double>? InputLevel;

    /// <summary>Уровень выхода 0..1 и время обработки блока в мс.</summary>
    public event Action<double, double>? OutputLevel;

    public event Action<EngineState, string?>? StateChanged;

    public AudioEngine(AppSettings settings, PythonBridge bridge)
    {
        _settings = settings;
        _bridge = bridge;
        _bridge.AudioReady += OnAudioReady;
    }

    private double _lastProcessMs;

    /// <summary>
    /// Оценка сквозной задержки: буфер захвата + накопление блока + обработка +
    /// кроссфейд/SOLA + буфер вывода. extra_convert_size — контекст из ПРОШЛОГО
    /// (пайплайн обрезает его через skip_head), поэтому в задержку он не входит.
    /// </summary>
    public double LatencyEstimateMs =>
        CaptureBufferMs + _blockSize * 1000.0 / SampleRate + _lastProcessMs
        + (_settings.CrossFadeOverlapSize + 0.01) * 1000 + OutputLatencyMs;

    private const int CaptureBufferMs = 10;
    private const int OutputLatencyMs = 20;

    public async Task StartAsync(string modelPath, string? indexPath, CancellationToken ct = default)
    {
        await StopAsync();

        SetState(EngineState.Starting, null);

        try
        {
            _blockSize = Math.Max(1024, _settings.ReadChunkSize * 128);
            _inputAccumulator = new float[_blockSize];
            _accumulated = 0;

            await _bridge.StartAsync(modelPath, indexPath, ct);

            StartCapture();
            StartOutputs();

            SetState(EngineState.Running, null);
            Log.Info("Engine", $"Запущено. Блок {_blockSize} семплов (~{_blockSize * 1000.0 / SampleRate:F0} мс), оценка задержки ~{LatencyEstimateMs:F0} мс");
        }
        catch (Exception ex)
        {
            Log.Error("Engine", "Не удалось запустить аудиотракт", ex);
            SetState(EngineState.Error, ex.Message);
            await StopAsync();
            throw;
        }
    }

    private void StartCapture()
    {
        var device = AudioDevices.Get(_settings.InputDeviceId);
        if (device == null)
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            Log.Warn("Engine", "Входное устройство не выбрано — взяли микрофон по умолчанию");
        }

        _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: CaptureBufferMs)
        {
            ShareMode = _settings.ExclusiveMode ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared
        };

        Log.Info("Engine", $"Микрофон: {device.FriendlyName}, формат {_capture.WaveFormat}");

        _capture.DataAvailable += OnCaptureData;
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                Log.Error("Engine", "Захват звука остановлен с ошибкой", e.Exception);
        };

        _capture.StartRecording();
    }

    private void StartOutputs()
    {
        var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

        if (_settings.SendToVirtualMic)
        {
            var target = AudioDevices.Get(_settings.OutputDeviceId);
            if (target == null)
            {
                var cable = VbCable.FindCableInput();
                target = AudioDevices.Get(cable?.Id);
                if (target != null)
                    Log.Info("Engine", $"Вывод автоматически направлен в {target.FriendlyName}");
            }

            if (target != null)
            {
                _virtualMicBuffer = new BufferedWaveProvider(outFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(2)
                };

                _virtualMicOut = new WasapiOut(target, _settings.ExclusiveMode ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, true, OutputLatencyMs);
                _virtualMicOut.Init(BuildOutputChain(_virtualMicBuffer, target));
                _virtualMicOut.Play();
                Log.Info("Engine", $"Вывод (виртуальный микрофон): {target.FriendlyName}");
            }
            else
            {
                Log.Warn("Engine", "Устройство вывода не найдено (VB-CABLE не установлен?)");
            }
        }

        if (_settings.MonitorEnabled)
        {
            var monitor = AudioDevices.Get(_settings.MonitorDeviceId);
            if (monitor != null)
            {
                _monitorBuffer = new BufferedWaveProvider(outFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(2)
                };

                _monitorOut = new WasapiOut(monitor, AudioClientShareMode.Shared, true, OutputLatencyMs);
                _monitorOut.Init(BuildOutputChain(_monitorBuffer, monitor));
                _monitorOut.Play();
                Log.Info("Engine", $"Самопрослушка: {monitor.FriendlyName}");
            }
        }
    }

    /// <summary>Доводим mono 48k float до формата устройства (обычно stereo).</summary>
    private IWaveProvider BuildOutputChain(BufferedWaveProvider buffer, MMDevice device)
    {
        ISampleProvider sample = buffer.ToSampleProvider();

        var deviceChannels = device.AudioClient.MixFormat.Channels;
        if (deviceChannels >= 2)
            sample = new MonoToStereoSampleProvider(sample);

        var deviceRate = device.AudioClient.MixFormat.SampleRate;
        if (deviceRate != SampleRate)
            sample = new WdlResamplingSampleProvider(sample, deviceRate);

        return sample.ToWaveProvider();
    }

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        if (_capture == null || e.BytesRecorded == 0) return;

        var format = _capture.WaveFormat;
        var mono = ConvertToMonoFloat(e.Buffer, e.BytesRecorded, format, out var peak);

        var gain = (float)Math.Pow(10, _settings.InputGainDb / 20.0);
        if (Math.Abs(gain - 1f) > 0.001f)
            for (var i = 0; i < mono.Length; i++) mono[i] *= gain;

        InputLevel?.Invoke(Math.Min(1.0, peak * gain));

        if (format.SampleRate != SampleRate)
            mono = ResampleLinear(mono, format.SampleRate, SampleRate);

        lock (_sync)
        {
            var offset = 0;
            while (offset < mono.Length)
            {
                var take = Math.Min(_blockSize - _accumulated, mono.Length - offset);
                Array.Copy(mono, offset, _inputAccumulator, _accumulated, take);
                _accumulated += take;
                offset += take;

                if (_accumulated < _blockSize) continue;

                var block = new float[_blockSize];
                Array.Copy(_inputAccumulator, block, _blockSize);
                _accumulated = 0;

                _ = _bridge.SendAudioAsync(block, block.Length);
            }
        }
    }

    private DateTime _lastDriftWarn = DateTime.MinValue;

    private void TrimBacklog(BufferedWaveProvider? buffer)
    {
        if (buffer == null) return;

        var maxMs = _blockSize * 2000.0 / SampleRate + 100;
        if (buffer.BufferedDuration.TotalMilliseconds <= maxMs) return;

        buffer.ClearBuffer();

        var now = DateTime.UtcNow;
        if ((now - _lastDriftWarn).TotalSeconds > 10)
        {
            _lastDriftWarn = now;
            Log.Warn("Engine", $"Буфер вывода накопил задержку (>{maxMs:F0} мс) — сброшен для синхронизации. " +
                "Если повторяется часто — увеличьте размер чанка или уменьшите доп. преобразование.");
        }
    }

    private void OnAudioReady(float[] samples, WorkerStats stats)
    {
        _lastProcessMs = stats.ProcessMs;

        var gain = (float)Math.Pow(10, _settings.OutputGainDb / 20.0);
        float peak = 0;

        for (var i = 0; i < samples.Length; i++)
        {
            var v = samples[i] * gain;
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            samples[i] = v;
            var abs = Math.Abs(v);
            if (abs > peak) peak = abs;
        }

        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        TrimBacklog(_virtualMicBuffer);
        TrimBacklog(_monitorBuffer);

        _virtualMicBuffer?.AddSamples(bytes, 0, bytes.Length);
        _monitorBuffer?.AddSamples(bytes, 0, bytes.Length);

        OutputLevel?.Invoke(peak, stats.ProcessMs);

        if (stats.ProcessMs > _blockSize * 1000.0 / SampleRate)
            Log.Warn("Engine", $"Обработка не успевает: {stats.ProcessMs:F0} мс на блок {_blockSize * 1000.0 / SampleRate:F0} мс. Увеличьте размер чанка.");
    }

    private static float[] ConvertToMonoFloat(byte[] buffer, int bytesRecorded, WaveFormat format, out float peak)
    {
        peak = 0;
        var channels = Math.Max(1, format.Channels);
        float[] mono;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var totalSamples = bytesRecorded / 4;
            var frames = totalSamples / channels;
            mono = new float[frames];

            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                    sum += BitConverter.ToSingle(buffer, (f * channels + c) * 4);

                var v = sum / channels;
                mono[f] = v;
                var abs = Math.Abs(v);
                if (abs > peak) peak = abs;
            }

            return mono;
        }

        if (format.BitsPerSample == 16)
        {
            var totalSamples = bytesRecorded / 2;
            var frames = totalSamples / channels;
            mono = new float[frames];

            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                    sum += BitConverter.ToInt16(buffer, (f * channels + c) * 2) / 32768f;

                var v = sum / channels;
                mono[f] = v;
                var abs = Math.Abs(v);
                if (abs > peak) peak = abs;
            }

            return mono;
        }

        if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.Pcm)
        {
            var totalSamples = bytesRecorded / 4;
            var frames = totalSamples / channels;
            mono = new float[frames];

            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                    sum += BitConverter.ToInt32(buffer, (f * channels + c) * 4) / 2147483648f;

                var v = sum / channels;
                mono[f] = v;
                var abs = Math.Abs(v);
                if (abs > peak) peak = abs;
            }

            return mono;
        }

        Log.Warn("Engine", $"Неподдерживаемый формат захвата: {format}");
        return Array.Empty<float>();
    }

    /// <summary>Простой линейный ресемпл — для 44.1k -&gt; 48k на голосе достаточно.</summary>
    private static float[] ResampleLinear(float[] input, int sourceRate, int targetRate)
    {
        if (input.Length == 0 || sourceRate == targetRate) return input;

        var ratio = targetRate / (double)sourceRate;
        var outLength = (int)(input.Length * ratio);
        var output = new float[outLength];

        for (var i = 0; i < outLength; i++)
        {
            var srcPos = i / ratio;
            var idx = (int)srcPos;
            var frac = (float)(srcPos - idx);

            var a = input[Math.Min(idx, input.Length - 1)];
            var b = input[Math.Min(idx + 1, input.Length - 1)];
            output[i] = a + (b - a) * frac;
        }

        return output;
    }

    public async Task StopAsync()
    {
        try
        {
            if (_capture != null)
            {
                _capture.DataAvailable -= OnCaptureData;
                _capture.StopRecording();
                _capture.Dispose();
                _capture = null;
            }

            _virtualMicOut?.Stop();
            _virtualMicOut?.Dispose();
            _virtualMicOut = null;

            _monitorOut?.Stop();
            _monitorOut?.Dispose();
            _monitorOut = null;

            _virtualMicBuffer = null;
            _monitorBuffer = null;

            await _bridge.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Engine", "Ошибка при остановке: " + ex.Message);
        }
        finally
        {
            if (State != EngineState.Error) SetState(EngineState.Stopped, null);
        }
    }

    private void SetState(EngineState state, string? message)
    {
        State = state;
        StateChanged?.Invoke(state, message);
    }

    public void Dispose()
    {
        _bridge.AudioReady -= OnAudioReady;
        StopAsync().GetAwaiter().GetResult();
    }
}
