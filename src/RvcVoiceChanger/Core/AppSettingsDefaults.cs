namespace RvcVoiceChanger.Core;

/// <summary>Сброс параметров модели к значениям по умолчанию (не трогает устройства, прокси и список моделей).</summary>
public static class AppSettingsDefaults
{
    public static void ResetModelDefaults(this AppSettings s)
    {
        s.Pitch = 0;
        s.IndexRate = 0.5;
        s.Protect = 0.5;
        s.VolumeEnvelope = 1.0;

        s.Autotune = false;
        s.AutotuneStrength = 1.0;
        s.ProposedPitch = false;
        s.ProposedPitchThreshold = 155.0;

        s.CleanAudio = false;
        s.CleanStrength = 0.5;
        s.PostProcess = false;

        s.F0Method = "rmvpe";
        s.EmbedderModel = "contentvec";
        s.EmbedderModelCustom = "";
        s.UsePhaseVocoder = true;
        s.VadEnabled = true;

        s.ReadChunkSize = 96;
        s.CrossFadeOverlapSize = 0.10;
        s.ExtraConvertSize = 0.50;
        s.SilentThreshold = -60;

        s.InputGainDb = 0;
        s.OutputGainDb = 0;

        s.Reverb = false;
        s.ReverbRoomSize = 0.5;
        s.ReverbWetLevel = 0.33;
        s.ReverbDryLevel = 0.4;
        s.ReverbDamping = 0.5;

        s.Limiter = false;
        s.LimiterThresholdDb = -6;
        s.Compressor = false;
        s.CompressorThresholdDb = -20;
        s.CompressorRatio = 4;

        Log.Info("Settings", "Настройки модели сброшены к значениям по умолчанию");
    }
}
