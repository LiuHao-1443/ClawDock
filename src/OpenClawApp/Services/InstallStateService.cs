using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClawApp.Services;

public enum InstallPhase
{
    NotStarted,
    Wsl2,       // WSL2 已安装，等待重启后继续
    OpenClaw,   // 正在安装 OpenClaw
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
    public bool UbuntuInstalled { get; set; }

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
        "OpenClaw", "state.json");

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

    public void MarkWsl2Done()
    {
        var state = Load();
        state.Wsl2Installed = true;
        state.UbuntuInstalled = true;
        state.Phase = InstallPhase.Wsl2;
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
}
