using System.Text.Json;

namespace WinOptimizer.Core.Models;

/// <summary>
/// Стан для відкату — System Restore Point + зміни що можна скасувати.
/// Зберігається: C:\ProgramData\WinOptimizer\Data\rollback_state.json
/// </summary>
public class RollbackState
{
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>System Restore Point sequence number (0 = не створено)</summary>
    public int RestorePointSequenceNumber { get; set; }

    /// <summary>Опис точки відновлення</summary>
    public string RestorePointDescription { get; set; } = "";

    /// <summary>Чи є точка відновлення</summary>
    public bool HasRestorePoint => RestorePointSequenceNumber > 0;

    /// <summary>Вимкнені служби (назва → оригінальний StartType)</summary>
    public List<DisabledService> DisabledServices { get; set; } = new();

    /// <summary>Вимкнені елементи автозавантаження</summary>
    public List<DisabledStartupItem> DisabledStartupItems { get; set; } = new();

    /// <summary>Видалені програми (для логу)</summary>
    public List<string> RemovedPrograms { get; set; } = new();

    /// <summary>Скільки байтів очищено</summary>
    public long CleanedBytes { get; set; }

    /// <summary>Чи виконувалась дефрагментація</summary>
    public bool DefragPerformed { get; set; }

    /// <summary>Вільне місце до оптимізації</summary>
    public long FreeSpaceBefore { get; set; }

    /// <summary>Вільне місце після оптимізації</summary>
    public long FreeSpaceAfter { get; set; }

    /// <summary>Кількість знайдених загроз антивірусом</summary>
    public int ThreatsFound { get; set; }

    /// <summary>Тип rollback: "cleanup" (System Restore) або "upgrade" (DISM OSUninstall)</summary>
    public string Type { get; set; } = "cleanup";

    /// <summary>Чи було виконано upgrade Windows (ISO + setup.exe)</summary>
    public bool UpgradePerformed { get; set; }

    /// <summary>З якої версії оновлено (наприклад "Windows 10 Pro")</summary>
    public string UpgradeFromVersion { get; set; } = "";

    /// <summary>На яку версію оновлено (наприклад "11")</summary>
    public string UpgradeToVersion { get; set; } = "";

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinOptimizer", "Data", "rollback_state.json");

    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        // Атомарний запис: спочатку temp файл, потім File.Move
        // Якщо ПК крашнеться під час запису — старий файл залишиться цілим
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, FilePath, overwrite: true);
    }

    public static RollbackState? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<RollbackState>(json);
    }

    public static void Delete()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }
}

public class DisabledService
{
    public string ServiceName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string OriginalStartType { get; set; } = "";
}

public class DisabledStartupItem
{
    public string RegistryPath { get; set; } = "";
    public string ValueName { get; set; } = "";
    public string ValueData { get; set; } = "";
    public string ValueKind { get; set; } = "String";
}
