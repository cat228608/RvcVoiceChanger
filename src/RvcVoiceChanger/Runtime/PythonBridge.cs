using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RvcVoiceChanger.Core;

namespace RvcVoiceChanger.Runtime;

public enum FrameType : byte
{
    AudioIn = 1,
    Control = 2,
    AudioOut = 3,
    Event = 4
}

public sealed record WorkerStats(double Volume, double ProcessMs);

/// <summary>
/// Мост к python-воркеру. Воркер поднимает TCP-сервер на 127.0.0.1 и печатает
/// "PORT &lt;n&gt;" в stdout. Дальше обмен бинарными кадрами:
/// [1 байт тип][int32 длина][payload].
/// Кадры AudioIn/AudioOut — float32 mono 48 кГц, Control/Event — UTF-8 JSON.
/// </summary>
public sealed class PythonBridge : IDisposable, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly RuntimeInstaller _installer;

    private Process? _process;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    private Task? _heartbeatTask;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Буфер для отправки аудио — чтобы не аллоцировать массив на каждый блок.</summary>
    private byte[] _audioSendBuffer = Array.Empty<byte>();

    /// <summary>Момент последнего ответа воркера (pong или любой кадр).</summary>
    private long _lastAliveUtcTicks;

    /// <summary>Соединение признано мёртвым (ошибка записи/чтения или нет pong).</summary>
    private volatile bool _connectionDead;

    public bool IsRunning => _process is { HasExited: false } && _stream != null && !_connectionDead;

    /// <summary>Обработанный блок аудио (float32 48 кГц mono) и телеметрия.</summary>
    public event Action<float[], WorkerStats>? AudioReady;

    /// <summary>Событие от воркера (ready / error / model_loaded / log / pong).</summary>
    public event Action<string, string>? WorkerEvent;

    public PythonBridge(AppSettings settings)
    {
        _settings = settings;
        _installer = new RuntimeInstaller(settings);
    }

    public async Task StartAsync(string modelPath, string? indexPath, CancellationToken ct = default)
    {
        await StopAsync();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _connectionDead = false;

        var psi = _installer.CreatePythonStartInfo(new[] { AppPaths.WorkerScript, "--serve" });
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var portTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        _process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;

            if (e.Data.StartsWith("PORT ", StringComparison.Ordinal) &&
                int.TryParse(e.Data.AsSpan(5), out var port))
            {
                portTcs.TrySetResult(port);
                Log.Info("Bridge", $"Воркер слушает порт {port}");
                return;
            }

            Log.Debug("worker", e.Data);
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            Log.Warn("worker", e.Data);
        };

        _process.Exited += (sender, _) =>
        {
            // Берём процесс из sender: поле _process к этому моменту может быть обнулено StopAsync().
            var code = -1;
            try { if (sender is Process p) code = p.ExitCode; } catch { }

            if (code != 0)
                Log.Error("Bridge", $"Процесс воркера завершился с кодом {code}");
            portTcs.TrySetException(new InvalidOperationException($"Воркер завершился (код {code})"));
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Ожидание порта с отменяемым таймаутом: после успеха 60-секундный таймер гасится,
        // а не висит в пуле потоков до истечения.
        using (var startupCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
        {
            var finished = await Task.WhenAny(portTcs.Task, Task.Delay(TimeSpan.FromSeconds(60), startupCts.Token));
            startupCts.Cancel();

            if (finished != portTcs.Task)
                throw new TimeoutException("Воркер не запустился за 60 секунд");
        }

        var portNumber = await portTcs.Task;

        _client = new TcpClient();
        await _client.ConnectAsync("127.0.0.1", portNumber, _cts.Token);
        _client.NoDelay = true;
        _stream = _client.GetStream();

        MarkAlive();
        _readerTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));

        // Пути модели кладём в params: воркер читает конфигурацию оттуда.
        // Верхнеуровневые ключи дублируем для совместимости.
        var initParams = BuildParams();
        initParams["model_path"] = modelPath;
        initParams["index_path"] = indexPath ?? "";

        await SendControlAsync(new Dictionary<string, object?>
        {
            ["cmd"] = "init",
            ["model_path"] = modelPath,
            ["index_path"] = indexPath ?? "",
            ["params"] = initParams
        });

        Log.Info("Bridge", "Воркер запущен, модель загружается");
    }

    public Dictionary<string, object?> BuildParams() => new()
    {
        ["f0_up_key"] = _settings.Pitch,
        ["index_rate"] = _settings.IndexRate,
        ["protect"] = _settings.Protect,
        ["volume_envelope"] = _settings.VolumeEnvelope,
        ["f0_autotune"] = _settings.Autotune,
        ["f0_autotune_strength"] = _settings.AutotuneStrength,
        ["proposed_pitch"] = _settings.ProposedPitch,
        ["proposed_pitch_threshold"] = _settings.ProposedPitchThreshold,
        ["use_phase_vocoder"] = _settings.UsePhaseVocoder,
        ["f0_method"] = _settings.F0Method,
        ["embedder_model"] = _settings.EmbedderModel,
        ["embedder_model_custom"] = _settings.EmbedderModelCustom,
        ["clean_audio"] = _settings.CleanAudio,
        ["clean_strength"] = _settings.CleanStrength,
        ["post_process"] = _settings.PostProcess,
        ["silent_threshold"] = _settings.SilentThreshold,
        ["vad_enabled"] = _settings.VadEnabled,
        ["read_chunk_size"] = _settings.ReadChunkSize,
        ["cross_fade_overlap_size"] = _settings.CrossFadeOverlapSize,
        ["extra_convert_size"] = _settings.ExtraConvertSize,
        ["device"] = _settings.Device.ToString().ToLowerInvariant(),
        ["pedalboard"] = new Dictionary<string, object?>
        {
            ["reverb"] = _settings.Reverb,
            ["reverb_room_size"] = _settings.ReverbRoomSize,
            ["reverb_wet_level"] = _settings.ReverbWetLevel,
            ["reverb_dry_level"] = _settings.ReverbDryLevel,
            ["reverb_damping"] = _settings.ReverbDamping,
            ["limiter"] = _settings.Limiter,
            ["limiter_threshold"] = _settings.LimiterThresholdDb,
            ["compressor"] = _settings.Compressor,
            ["compressor_threshold"] = _settings.CompressorThresholdDb,
            ["compressor_ratio"] = _settings.CompressorRatio
        }
    };

    /// <summary>Отправляет текущие настройки в воркер — вызывается при правке любого слайдера.</summary>
    public Task PushSettingsAsync() => SendControlAsync(new Dictionary<string, object?>
    {
        ["cmd"] = "update",
        ["params"] = BuildParams()
    });

    public Task SwitchModelAsync(string modelPath, string? indexPath)
    {
        // model_path/index_path — и на верхнем уровне, и в params: воркер применяет их
        // через тот же механизм, что и update, поэтому params обязателен.
        var switchParams = new Dictionary<string, object?>
        {
            ["model_path"] = modelPath,
            ["index_path"] = indexPath ?? ""
        };

        return SendControlAsync(new Dictionary<string, object?>
        {
            ["cmd"] = "switch_model",
            ["model_path"] = modelPath,
            ["index_path"] = indexPath ?? "",
            ["params"] = switchParams
        });
    }

    public async Task SendControlAsync(Dictionary<string, object?> payload)
    {
        if (_stream == null) return;

        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        await WriteFrameAsync(FrameType.Control, json, json.Length);
    }

    public async Task SendAudioAsync(float[] samples, int count)
    {
        if (_stream == null) return;

        var byteCount = count * sizeof(float);
        if (_audioSendBuffer.Length < byteCount)
            _audioSendBuffer = new byte[byteCount];

        Buffer.BlockCopy(samples, 0, _audioSendBuffer, 0, byteCount);
        await WriteFrameAsync(FrameType.AudioIn, _audioSendBuffer, byteCount);
    }

    private async Task WriteFrameAsync(FrameType type, byte[] payload, int count)
    {
        var stream = _stream;
        if (stream == null || _connectionDead) return;

        var header = new byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), count);

        await _writeLock.WaitAsync();
        try
        {
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload.AsMemory(0, count));
            await stream.FlushAsync();
        }
        catch (Exception ex)
        {
            // Ошибку записи нельзя молча глотать: сокет мёртв, движок должен перейти в Error.
            Log.Error("Bridge", "Ошибка записи в воркер", ex);
            OnConnectionDead("Связь с воркером потеряна: " + ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void OnConnectionDead(string message)
    {
        if (_connectionDead) return;
        _connectionDead = true;
        try { WorkerEvent?.Invoke("error", message); } catch { }
    }

    private void MarkAlive() => Interlocked.Exchange(ref _lastAliveUtcTicks, DateTime.UtcNow.Ticks);

    /// <summary>
    /// Пульс: раз в 5 секунд шлём ping. Любой входящий кадр обновляет отметку жизни.
    /// Если воркер молчит дольше 20 секунд — считаем соединение мёртвым.
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_connectionDead)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);

                await SendControlAsync(new Dictionary<string, object?> { ["cmd"] = "ping" });

                var silentFor = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastAliveUtcTicks), DateTimeKind.Utc);
                if (silentFor > TimeSpan.FromSeconds(20))
                {
                    Log.Error("Bridge", $"Воркер не отвечает {silentFor.TotalSeconds:F0} с — соединение считается разорванным");
                    OnConnectionDead("Воркер перестал отвечать (нет pong)");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug("Bridge", "Пульс остановлен: " + ex.Message);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var stream = _stream!;
        var header = new byte[5];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReadExactAsync(stream, header, 5, ct);

                var type = (FrameType)header[0];
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
                if (length < 0 || length > 64 * 1024 * 1024)
                    throw new InvalidDataException($"Некорректная длина кадра: {length}");

                var payload = new byte[length];
                await ReadExactAsync(stream, payload, length, ct);

                MarkAlive();

                switch (type)
                {
                    case FrameType.AudioOut:
                        HandleAudioOut(payload);
                        break;

                    case FrameType.Event:
                        HandleEvent(payload);
                        break;

                    default:
                        Log.Warn("Bridge", $"Неизвестный тип кадра: {type}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            Log.Debug("Bridge", "Чтение остановлено: " + ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("Bridge", "Связь с воркером потеряна", ex);
            OnConnectionDead("Связь с воркером потеряна: " + ex.Message);
        }
    }

    private void HandleAudioOut(byte[] payload)
    {
        // payload = [float32 volume][float32 process_ms][float32 samples...]
        if (payload.Length < 8) return;

        var volume = BitConverter.ToSingle(payload, 0);
        var processMs = BitConverter.ToSingle(payload, 4);

        var sampleCount = (payload.Length - 8) / sizeof(float);
        var samples = new float[sampleCount];
        Buffer.BlockCopy(payload, 8, samples, 0, sampleCount * sizeof(float));

        AudioReady?.Invoke(samples, new WorkerStats(volume, processMs));
    }

    private void HandleEvent(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("event", out var e) ? e.GetString() ?? "log" : "log";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

            switch (kind)
            {
                case "pong":
                    // Служебный ответ на ping — в лог не пишем.
                    return;
                case "error":
                    Log.Error("worker", message);
                    break;
                case "warn":
                    Log.Warn("worker", message);
                    break;
                case "ready":
                    Log.Info("worker", string.IsNullOrEmpty(message) ? "Воркер готов" : message);
                    break;
                default:
                    Log.Info("worker", message);
                    break;
            }

            WorkerEvent?.Invoke(kind, message);
        }
        catch (Exception ex)
        {
            Log.Warn("Bridge", "Не разобрали событие воркера: " + ex.Message);
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0) throw new EndOfStreamException("Воркер закрыл соединение");
            offset += read;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            if (_stream != null && !_connectionDead)
                await SendControlAsync(new Dictionary<string, object?> { ["cmd"] = "shutdown" });
        }
        catch { }

        try { _cts?.Cancel(); } catch { }

        try
        {
            var pending = new List<Task>();
            if (_readerTask != null) pending.Add(_readerTask);
            if (_heartbeatTask != null) pending.Add(_heartbeatTask);
            if (pending.Count > 0)
                await Task.WhenAny(Task.WhenAll(pending), Task.Delay(1000));
        }
        catch { }

        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        _readerTask = null;
        _heartbeatTask = null;

        try
        {
            if (_process is { HasExited: false })
            {
                if (!_process.WaitForExit(1500))
                {
                    _process.Kill(entireProcessTree: true);
                    Log.Warn("Bridge", "Воркер пришлось завершить принудительно");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Bridge", "Не удалось аккуратно завершить воркер: " + ex.Message);
        }

        _process?.Dispose();
        _process = null;
        _connectionDead = false;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>
    /// Синхронный Dispose не блокирует UI-поток напрямую: остановка выполняется
    /// в пуле потоков с жёстким таймаутом (иначе sync-over-async на UI = дедлок).
    /// </summary>
    public void Dispose()
    {
        try { Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(5)); }
        catch { }
    }
}
