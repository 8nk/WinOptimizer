# Остання зміна: 2026-03-02 (v5.5.2 — Langpack Reboot + Agent Resume)

## Git
- **Попередній коміт:** `5e69fdf` — `v5.4: Language Detection + Multilingual AutoClicker`
- **Бранч:** `main`
- `.gitignore`: виключає `bin/`, `obj/`, `publish/`, `*.exe`

## Що зроблено сьогодні

### v5.5.2 — Langpack Reboot + Agent Resume (ПОТОЧНА)

**Проблема:** Після встановлення langpack через DISM + зміни реєстру, `setup.exe` все одно бачив стару мову сесії. `Get-UICulture` повертає en-US поки не зробиш **REBOOT**.

**Рішення — двохфазна архітектура з ребутом:**

#### Фаза 1 — Orchestrator (до ребуту):
1. Встановлює langpack (DISM /Add-Package) + змінює реєстр InstallLanguage
2. Шукає ISO (мережа → кеш → VPS) та кешує локально
3. Зберігає `langpack_resume.json`:
   ```json
   { "isoPath", "language", "version", "createdAt", "restorePointSeq" }
   ```
4. Зберігає rollback state (Type="upgrade")
5. Надсилає TG нотифікацію
6. Запускає `shutdown.exe /r /t 15 /c "WinOptimizer: мовний пакет встановлено"`
7. `Environment.Exit(0)` — програма закривається, ребут відбувається

#### Фаза 2 — Agent (після ребуту):
1. Agent стартує (scheduled task AtStartup + AtLogOn)
2. Знаходить `langpack_resume.json` → `ResumeLangpackUpgrade()`
3. Чекає 60 секунд (стабілізація системи)
4. Перевіряє мову сесії PowerShell (тепер uk-UA!)
5. Bypass TPM/CPU (реєстр)
6. Чистить `C:\$WINDOWS.~BT` (якщо є)
7. TRY 1: `setup.exe /auto upgrade`
8. TRY 2: `setup.exe` (GUI mode, без аргументів)
9. Надсилає TG нотифікації на кожному кроці
10. Видаляє `langpack_resume.json`

#### Змінені файли:
- **`OptimizationOrchestrator.cs`** — reboot logic після langpack (+200 рядків):
  - Пошук ISO перед ребутом (мережа → кеш)
  - Збереження resume файлу
  - `shutdown.exe /r /t 15` + `Environment.Exit(0)`
- **`WinOptimizerAgent/Program.cs`** — resume logic після ребуту (+188 рядків):
  - `ResumeLangpackUpgrade()` — повний pipeline
  - TRY 1 `/auto upgrade` → TRY 2 GUI mode
  - TG нотифікації
  - `using System.Linq` (fix build error)

### v5.5.1 — Language Pack Bugfix

**Знайдено та виправлено 2 баги (з тесту vps 3):**

1. **`IsLanguagePackInstalledAsync()`** — `"NOT_INSTALLED".Contains("INSTALLED")` = TRUE!
   - Перевірка ЗАВЖДИ повертала `true`, langpack НІКОЛИ не завантажувався
   - **Fix:** `Contains("INSTALLED_DISM") || Contains("INSTALLED_WINLANG")`

2. **`WindowsUpgradeService` ISO validation** — використовувала `Get-UICulture` (стару мову)
   - Навіть після зміни реєстру validation логувала "LANGUAGE MISMATCH"
   - **Fix:** читає `InstallLanguage` з реєстру (hex → lang маппінг)
   - 0409=en-US, 0419=ru-RU, 0422=uk-UA, 0809=en-GB

### v5.5 — Auto Language Pack Installation

**Проблема:** Якщо юзер хоче змінити мову (RU→UK), setup.exe блокує "Зберегти файли та програми" через невідповідність мов.

**Рішення:** Автоматична установка мовного пакету ПЕРЕД upgrade!

1. **Новий файл: `LanguagePackService.cs`**
   - `EnsureLanguageMatchAsync()` — повний pipeline зміни мови
   - `IsLanguagePackInstalledAsync()` — DISM /Get-Packages перевірка
   - `DownloadAsync()` — скачати .cab з VPS (`/api/langpack/info`)
   - `InstallAsync()` — DISM /Online /Add-Package
   - `SetSystemLanguageAsync()` — Set-WinUILanguageOverride + реєстр
   - Кеш langpack: `C:\ProgramData\WinOptimizer\LangPack\`

2. **OptimizationStep.cs** — додано `InstallingLanguagePack` (крок 9), тепер 13 кроків

3. **MainWindowViewModel.cs** — 13 dots, нові описи, log milestones

4. **ProgressDotsControl.axaml.cs** — DotCount 13, Spacing 30

5. **VPS (84.238.132.84)**:
   - `/api/langpack/info?lang=uk-UA` → filename, size, download_url
   - `/api/langpack/list` → список .cab файлів
   - nginx: `/langpack/` → `/var/www/langpack/`
   - .cab файли завантажені:
     - `lp_uk-ua_amd64.cab` (23 MB)
     - `lp_ru-ru_amd64.cab` (35 MB)
     - `lp_en-us_amd64.cab` (22 MB)

### v5.4 — Language Detection + Multilingual AutoClicker (попередній коміт)

- DetectWindowsLanguageAsync() тепер ВИКОРИСТОВУЄТЬСЯ
- ISO обирається по МОВІ СИСТЕМИ
- AutoClicker v4.2: Multilingual (EN+UK+RU)
- Спрощена стратегія (2 спроби замість 4)

## Що робимо зараз

- [x] LanguagePackService.cs (v5.5)
- [x] OptimizationStep + Orchestrator + ViewModel (v5.5)
- [x] VPS langpack endpoint + nginx (v5.5)
- [x] .cab файли на VPS (v5.5)
- [x] Bugfix Contains("INSTALLED") (v5.5.1)
- [x] Bugfix Get-UICulture → Registry (v5.5.1)
- [x] Reboot + Agent Resume архітектура (v5.5.2)
- [x] Build v5.5.2 (148 MB)
- [x] Тест на Windows PC — langpack встановлюється, ребут працює ✅
- [ ] Повний тест: langpack → reboot → Agent resume → upgrade → rollback
- [ ] Перевірити "Зберегти файли та програми" ENABLED після ребуту
- [ ] Тест rollback (TG кнопка → Agent → DISM)

## Поточний flow v5.5.2

```
ФАЗА 1 — Підготовка (WinOptimizer):
1. Точка відновлення (System Restore)
2. Agent Deploy (scheduled task: AtStartup + AtLogOn)
3. Сканування системи
4. Видалення програм + UWP
5. Очистка диску C:
6. Оптимізація диску (TRIM/defrag)
7. Оптимізація служб
8. Очистка автозавантаження
9. Встановлення драйверів

ФАЗА 2 — Мовний пакет + Установка:
10. [NEW] Langpack (якщо мова ≠):
    → Download .cab → DISM /Add-Package → Registry → ISO cache
    → Save langpack_resume.json → REBOOT
    → Agent resume → setup.exe
11. Завантаження ISO з VPS (якщо немає langpack)
12. Встановлення Windows (setup.exe /auto upgrade або GUI)

ФАЗА 3 — Після установки:
13. Перевірка безпеки (Defender Quick Scan)
14. Готово! (шпалери + результати)
```

## Rollback стратегія

```
Очистка (та ж ОС):    System Restore → reboot
Upgrade (інша ОС):    DISM /Online /Initiate-OSUninstall → Windows.old → reboot
Оплата (очистка):     видалити Restore Point → agent self-delete
Оплата (upgrade):     видалити Windows.old → agent self-delete
```

## Registry (критичний для setup.exe)

```
HKLM:\SYSTEM\CurrentControlSet\Control\Nls\Language\InstallLanguage
  uk-UA = 0422
  ru-RU = 0419
  en-US = 0409
  en-GB = 0809
```

## VPS

```
SSH: root@84.238.132.84 / BxdBvzJaKT2Qge
API: http://84.238.132.84/api/
Bot: /opt/winflow-bot/bot.py
ISO: /var/www/iso/
LangPack: /var/www/langpack/
Logs: curl "http://84.238.132.84/api/logs?client_id=HWID&limit=50"
```

## Build

```bash
# Простий спосіб:
./build.sh

# Ручний (Agent):
cd src/WinOptimizerAgent && dotnet publish -c Release -r win-x86 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
# → cp bin/Release/net9.0/win-x86/publish/WinOptimizerAgent.exe ../WinOptimizer/Assets/

# Ручний (Main):
cd src/WinOptimizer && dotnet publish -c Release -r win-x86 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ../../publish
```

## Що залишилось

- [ ] Повний тест reboot+resume flow
- [ ] Перевірити "Зберегти файли та програми" ENABLED
- [ ] Тест rollback з TG
- [ ] Russian ISO на мережевій папці
- [ ] Нова архітектура: вшити WinOptimizer в ISO
- [ ] Windows 11 ISO на VPS
