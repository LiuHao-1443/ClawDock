using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawDock.Services;

public enum InstallPhase
{
    NotStarted,
    Wsl2Enabled,        // WSL2 功能已启用
    Wsl2Reboot,         // WSL2 需要重启激活（仅此阶段自动续装）
    DistroImported,     // 发行版已导入
    DistroConfigured,   // 发行版已配置（apt 镜像 + wsl.conf）
    OpenClawInstalling, // 正在安装 OpenClaw
    Complete
}

public class InstallState
{
    [JsonPropertyName("installed")]
    public bool IsInstallComplete { get; set; }

    [JsonPropertyName("phase")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InstallPhase Phase { get; set; } = InstallPhase.NotStarted;

    [JsonPropertyName("wsl2Installed")]
    public bool Wsl2Installed { get; set; }

    [JsonPropertyName("ubuntuInstalled")]
    public bool DistroInstalled { get; set; }

    [JsonPropertyName("openclawInstalled")]
    public bool OpenClawInstalled { get; set; }

    [JsonPropertyName("installDate")]
    public DateTime? InstallDate { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";
}

public class InstallStateService
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClawDock", "state.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public InstallState Load()
    {
        try
        {
            if (!File.Exists(StatePath))
                return new InstallState();

            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<InstallState>(json, JsonOptions) ?? new InstallState();
        }
        catch
        {
            return new InstallState();
        }
    }

    public void Save(InstallState state)
    {
        var dir = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(StatePath, json);
    }

    public void SavePhase(InstallPhase phase)
    {
        var state = Load();
        state.Phase = phase;
        Save(state);
    }

    public void MarkWsl2Done()
    {
        var state = Load();
        state.Wsl2Installed = true;
        state.DistroInstalled = true;
        state.Phase = InstallPhase.DistroConfigured;
        Save(state);
    }

    public void MarkOpenClawDone()
    {
        var state = Load();
        state.OpenClawInstalled = true;
        state.Phase = InstallPhase.Complete;
        state.IsInstallComplete = true;
        state.InstallDate = DateTime.UtcNow;
        Save(state);
    }

    public void Reset()
    {
        if (File.Exists(StatePath))
            File.Delete(StatePath);
    }
}
