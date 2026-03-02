# WinOptimizer — Автоматична установка Windows

## Суть проєкту

**WinOptimizer** — програма для автоматичної "установки" Windows. Виконує глибоку очистку, оптимізацію та РЕАЛЬНИЙ upgrade Windows через ISO + setup.exe.

### Головна ідея
- Юзер бачить: "Автоматична установка Windows"
- Два режими: **Автоматичний** (рекомендація ОС) і **Ручний** (вибір Win 10/11)
- РЕАЛЬНИЙ upgrade Windows через ISO download з VPS + setup.exe /auto upgrade
- Після установки: WinOptimizer вшитий в ISO → автозапуск після OOBE
- Логи пишуть "Встановлення Windows", "Оптимізація системи" (НЕ "очистка")
- Після завершення — Agent чекає оплату або rollback через TG

### ID формат:
- Тільки цифри з дефісами: `89-45-73-42-07-18` (6 пар по 2 цифри)

## Архітектура проекту

### Поточна версія: v4.2

### Фази роботи (12 кроків):
```
ФАЗА 1 — Підготовка:
1. Створення точки відновлення (для rollback)
2. Сканування системи
3. Видалення програм + UWP
4. Очистка диску C: (temp, кеш, кошик, DISM)
5. Оптимізація диску (TRIM/defrag)
6. Оптимізація служб
7. Очистка автозавантаження
8. Встановлення драйверів

ФАЗА 2 — Установка Windows:
9. Завантаження ISO з VPS (4-6 GB)
10. Встановлення Windows (setup.exe /auto upgrade)

ФАЗА 3 — Після установки:
11. Перевірка безпеки (Defender Quick Scan)
12. Готово! (шпалери + результати)
```

### Rollback стратегія:
- **Очистка (та ж ОС):** System Restore → reboot → все як було
- **Upgrade (інша ОС):** DISM /Online /Initiate-OSUninstall → Windows.old → reboot
- **Оплата (очистка):** видалити Restore Point → agent self-delete
- **Оплата (upgrade):** видалити Windows.old → agent self-delete

## Стек технологій

```
WinOptimizer/
  src/
    WinOptimizer.sln
    WinOptimizer/          (UI — Avalonia 11.3)
    WinOptimizer.Core/     (Domain — enums, models, interfaces)
    WinOptimizer.Services/ (Logic — cleanup, optimization, rollback)
    WinOptimizerAgent/     (Agent — heartbeat, rollback, payment)
```

- .NET 9.0 + Avalonia UI 11.3 + CommunityToolkit.Mvvm
- VPS: Flask + SQLite на 84.238.132.84
- TG Bot для адмін-панелі
- VPS Remote Logging (всі логи клієнтів на VPS)

## Правила роботи Claude

### КРИТИЧНО ВАЖЛИВО:
1. **Мова спілкування:** ВИКЛЮЧНО українська мова
2. **Перед початком роботи:** ЗАВЖДИ читати `LastChange.md`
3. **Після кожної зміни:** ОБОВ'ЯЗКОВО оновлювати `LastChange.md`
4. **Термінологія:** НІКОЛИ не писати "очистка" або "оптимізація" в UI — тільки "установка Windows"
5. **НЕ запускати** .exe на Mac, НЕ запускати dotnet run

## Середовище розробки

### IDE / Розробка:
- **macOS** (Mac) — основна машина
- **Claude Code** — IDE/агент
- Проект: `/Users/paveldudko1/Desktop/Rinat-WinFlow-PO/WinOptimizer`

### Тестування:
- **Windows PC** через SMB (192.168.1.101 → WIN)
- **Мережева папка на Mac:** `/Volumes/WIN/`
- Або: збірка в `src/WinOptimizer/bin/Release/net9.0/win-x86/publish/` → перенос вручну

## Build команди

### Agent:
```bash
cd src/WinOptimizerAgent && dotnet publish -c Release -r win-x86 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```
→ Копіювати в `src/WinOptimizer/Assets/WinOptimizerAgent.exe`

### Основний:
```bash
cd src/WinOptimizer && dotnet publish -c Release -r win-x86 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### Деплой:
```bash
# Через SMB (якщо доступний):
cp src/WinOptimizer/bin/Release/net9.0/win-x86/publish/WinOptimizer.exe /Volumes/WIN/
# Або перенести вручну з publish/
```

## VPS

- IP: 84.238.132.84
- SSH: root / BxdBvzJaKT2Qge
- API: Flask :5000 → nginx :80
- ISO: /var/www/iso/ (nginx sendfile)
- Bot: /opt/winflow-bot/bot.py
- Client logs: /opt/winflow-api/client_logs/

### VPS Logging:
```bash
# Список клієнтів
curl http://84.238.132.84/api/logs
# Логи клієнта
curl "http://84.238.132.84/api/logs?hwid=HWID"
```

## Ключові файли

### Core:
- `src/WinOptimizer.Services/Core/OptimizationOrchestrator.cs` — головний оркестратор
- `src/WinOptimizer/ViewModels/MainWindowViewModel.cs` — головна ViewModel
- `src/WinOptimizer.Core/Enums/OptimizationStep.cs` — 12 кроків

### Installation:
- `src/WinOptimizer.Services/Installation/IsoDownloadService.cs` — ISO download
- `src/WinOptimizer.Services/Installation/WindowsUpgradeService.cs` — setup.exe upgrade

### Logging:
- `src/WinOptimizer.Services/Logging/VpsLogger.cs` — VPS remote logging
- `src/WinOptimizer.Services/Logging/Logger.cs` — local + VPS logging

### UI:
- `src/WinOptimizer/Views/MainWindow.axaml` — UI (5 екранів)
- `src/WinOptimizer/Controls/CircularProgressControl.axaml.cs` — прогрес-кільце

### VPS:
- `vps/winflow-api/app.py` — Flask API
- `vps/winflow-bot/bot.py` — TG Bot

## Шляхи на Windows:
- Дані: `C:\ProgramData\WinOptimizer\Data\`
- ISO: `C:\ProgramData\WinOptimizer\ISO\`
- Agent: `C:\ProgramData\WinOptimizer\Agent\`
- Desktop logs: `C:\Users\Public\Desktop\WinOptimizer_*.log`

## TODO (майбутнє — нова архітектура):
- [ ] Вшити WinOptimizer в ISO (SetupComplete.cmd) → автозапуск після OOBE
- [ ] Інтегрувати Sophia Script або O&O ShutUp10 для оптимізації (тихо, в процесах)
- [ ] Windows 11 ISO на VPS
- [ ] Тестовий період 24-48 годин
- [ ] Іконки redesign

## Workflow
1. Прочитати CLAUDE.MD
2. Прочитати LastChange.md
3. Продовжити роботу з контексту
