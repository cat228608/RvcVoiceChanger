using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using RvcVoiceChanger.Core;

namespace RvcVoiceChanger.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsCapture, bool IsDefault, int Channels, int SampleRate)
{
    public string Display => IsDefault ? $"{Name}  (по умолчанию)" : Name;

    public bool LooksLikeVirtualCable =>
        Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Virtual Cable", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Virtual Audio", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Перебор WASAPI-устройств ввода/вывода.</summary>
public static class AudioDevices
{
    public static List<AudioDeviceInfo> List(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;

            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = def.ID;
            }
            catch { }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try
                {
                    var format = device.AudioClient.MixFormat;
                    result.Add(new AudioDeviceInfo(
                        device.ID,
                        device.FriendlyName,
                        flow == DataFlow.Capture,
                        device.ID == defaultId,
                        format.Channels,
                        format.SampleRate));
                }
                catch (Exception ex)
                {
                    Log.Warn("Audio", $"Устройство {device.FriendlyName} пропущено: {ex.Message}");
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio", "Не удалось перебрать аудиоустройства", ex);
        }

        return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
    }

    public static List<AudioDeviceInfo> Inputs() => List(DataFlow.Capture);
    public static List<AudioDeviceInfo> Outputs() => List(DataFlow.Render);

    public static MMDevice? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(id);
        }
        catch (Exception ex)
        {
            Log.Warn("Audio", $"Устройство {id} недоступно: {ex.Message}");
            return null;
        }
    }
}
