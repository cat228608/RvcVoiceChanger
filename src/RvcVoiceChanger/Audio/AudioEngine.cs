using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Runtime;

namespace RvcVoiceChanger.Audio;

/// <summary>
/// Warmup — тракт уже идёт, но ядро ещё набивает контекст и отдаёт тишину:
/// говорить в этот момент бессмысленно, первые слова не пройдут.
/// </summary>
public enum EngineState { Stopped, Starting, Warmup, Running, Error }

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

    /// <summary>Отметка нажатия «Старт» и счётчик оставшихся блоков прогрева.</summary>
    private DateTime _startedAtUtc = DateTime.UtcNow;
    private int _warmupBlocksLeft;

    /// <summary>Сколько секунд идёт запуск/прогрев — для надписи «Инициализация…» в интерфейсе.</summary>
    public double StartupSeconds => (DateTime.UtcNow - _startedAtUtc).TotalSeconds;

    /// <summary>Оценка прогресса прогрева 0..1 (только после загрузки модели).</summary>
    public double WarmupProgress
    {
        get
        {
            if (State == EngineState.Running) return 1;
            if (State != EngineState.Warmup || _warmupBlocksTotal <= 0) return 0;

            var done = _warmupBlocksTotal - _warmupBlocksLeft;
            return Math.Clamp(done / (double)_warmupBlocksTotal, 0, 1);
        }
    }

    private int _warmupBlocksTotal;

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

        _startedAtUtc = DateTime.UtcNow;
        SetState(EngineState.Starting, null);

        try
        {
            _blockSize = Math.Max(1024, _settings.ReadChunkSize * 128);
            _inputAccumulator = new float[_blockSize];
            _accumulated = 0;

            await _bridge.StartAsync(modelPath, indexPath, ct);

            StartCapture();
            StartOutputs();

            // Модель загружена и звук пошёл, но первые блоки ядро тратит на набивку
            // контекста и отдаёт тишину (warmup_blocks в realtime/core.py). Значит, «работает»
            // ≠ «можно говорить»: держим состояние Warmup, пока прогрев не пройдёт.
            _warmupBlocksTotal = EstimateWarmupBlocks();
            _warmupBlocksLeft = _warmupBlocksTotal;

            SetState(EngineState.Warmup, null);

            var blockMs = _blockSize * 1000.0 / SampleRate;
            Log.Info("Engine", $"Запущено. Блок {_blockSize} семплов (~{blockMs:F0} мс), "
                + $"прогрев {_warmupBlocksTotal} блоков (~{_warmupBlocksTotal * blockMs:F0} мс), "
                + $"оценка задержки ~{LatencyEstimateMs:F0} мс");
        }
        catch (Exception ex)
        {
            Log.Error("Engine", "Не удалось запустить аудиотракт", ex);
            SetState(EngineState.Error, ex.Message);
            await StopAsync();
            throw;
        }
    }

    /// <summary>
    /// Сколько блоков ядро тратит на прогрев. Формула та же, что в realtime/core.py:
    /// ceil(convert_size_16k / block_frame_16k) + 1. Пока они не прошли, на выход идёт тишина.
    /// </summary>
    private int EstimateWarmupBlocks()
    {
        var blockFrame16k = _blockSize / 3.0;
        var convertSize = _blockSize
            + (_settings.CrossFadeOverlapSize + 0.01) * SampleRate
            + _settings.ExtraConvertSize * SampleRate;

        var blocks = (int)Math.Ceiling(convertSize / 3.0 / blockFrame16k) + 1;
        return Math.Clamp(blocks, 1, 64);
    }

    /// <summary>
    /// Пришёл обработанный блок. Пока идёт прогрев — только считаем блоки; когда
    /// прогрев закончился, переводим движок в Running — это и есть «можно говорить».
    /// </summary>
    private void NoteWarmupBlock()
    {
        if (State != EngineState.Warmup) return;

        if (_warmupBlocksLeft > 0)
        {
            _warmupBlocksLeft--;
            return;
        }

        var seconds = StartupSeconds;
        SetState(EngineState.Running, null);
        Log.Info("Engine", $"Можно говорить: инициализация заняла {seconds:F1} с");
    }

    private void StartCapture()
    {
        var deviceId = ResolveInputDeviceId();
        if (deviceId == null)
            throw new InvalidOperationException("Микрофон не найден: выберите устройство ввода в настройках.");

        // Устройства капризны: в эксклюзивном режиме WASAPI часто не принимает формат mix
        // (float32) и короткий буфер, а часть USB-микрофонов не умеет период 10 мс даже в общем
        // режиме — AudioClient.Initialize отвечает E_INVALIDARG (ArgumentException в NAudio).
        // Раньше это валило весь запуск, теперь перебираем режимы от лучшего к самому совместимому.
        var attempts = new List<(bool Exclusive, bool ForcePcm16, int BufferMs)>();

        if (_settings.ExclusiveMode)
        {
            attempts.Add((true, false, CaptureBufferMs));
            attempts.Add((true, true, CaptureBufferMs));
            attempts.Add((true, true, 20));
        }

        attempts.Add((false, false, CaptureBufferMs));
        attempts.Add((false, false, 30));
        attempts.Add((false, false, 0));

        Exception? last = null;

        foreach (var (exclusive, forcePcm16, bufferMs) in attempts)
        {
            // Каждой попытке нужен свежий MMDevice: NAudio берёт у устройства уже созданный
            // AudioClient, а повторно инициализировать его после ошибки уже нельзя.
            var device = AudioDevices.Get(deviceId);
            if (device == null)
                throw new InvalidOperationException("Микрофон недоступен — устройство отключено?");

            WasapiCapture? capture = null;

            try
            {
                capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: bufferMs)
                {
                    ShareMode = exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared
                };

                if (forcePcm16)
                {
                    // В эксклюзивном режиме устройства обычно принимают не float, а 16 бит PCM.
                    var mix = device.AudioClient.MixFormat;
                    capture.WaveFormat = new WaveFormat(mix.SampleRate, 16, Math.Max(1, mix.Channels));
                }

                capture.DataAvailable += OnCaptureData;
                capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception != null)
                        Log.Error("Engine", "Захват звука остановлен с ошибкой", e.Exception);
                };

                capture.StartRecording();
                _capture = capture;

                Log.Info("Engine", $"Микрофон: {device.FriendlyName}, формат {capture.WaveFormat}, "
                    + (exclusive ? "эксклюзивный" : "общий") + " режим, буфер "
                    + (bufferMs > 0 ? bufferMs + " мс" : "по умолчанию"));
                return;
            }
            catch (Exception ex)
            {
                last = ex;

                try
                {
                    if (capture != null)
                    {
                        capture.DataAvailable -= OnCaptureData;
                        capture.Dispose();
                    }
                }
                catch { }

                Log.Warn("Engine", "Микрофон не принял режим ("
                    + (exclusive ? "эксклюзивный" : "общий")
                    + (forcePcm16 ? ", 16 бит" : "")
                    + ", буфер " + (bufferMs > 0 ? bufferMs + " мс" : "по умолчанию")
                    + "): " + ex.Message);
            }
        }

        throw new InvalidOperationException(
            "Не удалось открыть микрофон ни в одном режиме. Проверьте, что устройство не занято "
            + "другой программой, и отключите «Эксклюзивный режим WASAPI» в настройках."
            + (last != null ? " Последняя ошибка: " + last.Message : ""), last);
    }

    /// <summary>Идентификатор входного устройства: из настроек либо микрофон по умолчанию.</summary>
    private string? ResolveInputDeviceId()
    {
        if (!string.IsNullOrWhiteSpace(_settings.InputDeviceId)) return _settings.InputDeviceId;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            Log.Warn("Engine", "Входное устройство не выбрано — взяли микрофон по умолчанию");
            return device.ID;
        }
        catch (Exception ex)
        {
            Log.Error("Engine", "Не удалось найти микрофон по умолчанию: " + ex.Message);
            return null;
        }
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

                _virtualMicOut = StartOutput(target, _virtualMicBuffer, _settings.ExclusiveMode,
                    "Вывод (виртуальный микрофон)");
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

                _monitorOut = StartOutput(monitor, _monitorBuffer, false, "Самопрослушка");
            }
        }
    }

    /// <summary>
    /// Открывает устройство вывода, перебирая режимы. Эксклюзивный режим часть устройств
    /// не принимает вовсе (виртуальные кабели в том числе), а слишком маленькая задержка
    /// даёт E_INVALIDARG. Ошибка вывода теперь не валит весь тракт.
    /// </summary>
    private WasapiOut? StartOutput(MMDevice target, BufferedWaveProvider buffer, bool allowExclusive, string label)
    {
        var attempts = new List<(bool Exclusive, int LatencyMs)>();
        if (allowExclusive) attempts.Add((true, OutputLatencyMs));
        attempts.Add((false, OutputLatencyMs));
        attempts.Add((false, 60));

        Exception? last = null;

        foreach (var (exclusive, latencyMs) in attempts)
        {
            // Свежий MMDevice на ка��дую попытку — по той же причине, что и для захвата.
            var device = AudioDevices.Get(target.ID) ?? target;
            WasapiOut? output = null;

            try
            {
                output = new WasapiOut(device,
                    exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared,
                    true, latencyMs);

                output.Init(BuildOutputChain(buffer, device));
                output.Play();

                Log.Info("Engine", $"{label}: {device.FriendlyName} "
                    + "(" + (exclusive ? "эксклюзивный" : "общий") + " режим, " + latencyMs + " мс)");
                return output;
            }
            catch (Exception ex)
            {
                last = ex;
                try { output?.Dispose(); } catch { }

                Log.Warn("Engine", $"{label}: устройство не приняло режим ("
                    + (exclusive ? "эксклюзивный" : "общий") + ", " + latencyMs + " мс): " + ex.Message);
            }
        }

        Log.Error("Engine", $"{label}: устройство не открылось ни в одном режиме"
            + (last != null ? ": " + last.Message : ""));
        return null;
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

                _accumulated = 0;

                // TrySendAudio копирует блок синхронно и ставит его в очередь
                // единственного писателя. Поэтому аккумулятор можно переиспользовать
                // сразу, а массив на каждый блок больше не аллоцируется.
                _bridge.TrySendAudio(_inputAccumulator, _blockSize);
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

        NoteWarmupBlock();

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
