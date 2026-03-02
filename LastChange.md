# Остання зміна: 2026-03-02 (v5.4 — Language Detection + Multilingual AutoClicker v4.2)

## Що зроблено сьогодні

### Сесія 02.03.2026 (пізніше) — v5.4 "Language Detection Fix + AutoClicker v4.2"

**🔴 ЗНАЙДЕНО КОРЕНЕВУ ПРИЧИНУ ПРОБЛЕМИ:**

Setup.exe блокував "Зберегти файли та програми" через **невідповідність мови ISO та мови Windows!**

- Юзер вибирав "uk" (українська) → програма бере Ukrainian ISO
- Windows на PC — **російська** (ru-RU) або інша мова
- setup.exe бачить: мова ISO ≠ мова системи → БЛОКУЄ "Keep files & programs"
- Повідомлення: "ви інсталюєте Windows з використанням мови, відмінної від поточної"

**Функція `DetectWindowsLanguageAsync()` БУЛА написана, але НЕ ВИКОРИСТОВУВАЛАСЬ!**

**Рішення:**

1. **Language Detection** (OptimizationOrchestrator.cs):
   - Тепер ISO обирається по МОВІ СИСТЕМИ, а не по мові UI!
   - `DetectWindowsLanguageAsync()` тепер ВИКЛИКАЄТЬСЯ перед вибором ISO
   - Get-UICulture → парсинг → "ru" / "uk" / "en"
   - Якщо мова системи ≠ мова UI → лог WARNING + використовує мову системи
   - Fallback: якщо detection не працює → використовує мову UI

2. **AutoClicker v4.2 (Multilingual)** (WindowsUpgradeService.cs):
   - Кнопки: EN + UK + RU (Next/Далі/Далее, Accept/Прийняти/Принять, etc.)
   - Виключення: Back/Назад/Cancel/Скасувати/Отмена etc.
   - Radio buttons: "Зберегти файли та програми" / "Сохранить личные файлы" etc.
   - Window titles: "Windows Setup" + "Програма інсталяції" + "Программа установки"
   - Логування системної мови для діагностики (Get-UICulture, Get-WinSystemLocale)

3. **Спрощена стратегія запуску** (2 спроби замість 4):
   - TRY 1: setup.exe /auto upgrade (швидкий тест 5с — Consumer DVD не підтримує)
   - TRY 2: GUI mode + AutoClicker (основний метод)
   - **ВИДАЛЕНО**: TRY 2 (копіювання 4.5 GB ISO) і TRY 3 (setupprep.exe) — марна трата часу

4. **ISO Language Validation** (ValidateIsoContentsAsync):
   - Перевіряє мову install.wim через DISM /Get-WimInfo
   - Порівнює мову ISO з мовою системи
   - Логує ⚠️⚠️⚠️ WARNING якщо мови не збігаються

### Попередня сесія 02.03.2026 — v5.1 "AutoClicker v4.1 + Error 183 Fix"

- AutoClicker v4.1: ScriptBlock + BOM + global trap + Desktop log
- Fix $pid → $procId (read-only automatic variable)
- Browser process exclusion (Edge, Chrome, Firefox)
- Tab escalation strategy (ENTER → Tab+ENTER → Tab×3 → Tab×4 → Tab×5 → Alt+A)
- Fix setup.exe error 183 (cleanup C:\$WINDOWS.~BT)

### Попередні сесії
- v5.0-5.3 — /auto upgrade спроби, копіювання ISO, setupprep
- v4.9 — AutoClicker v3 (UI Automation + C# P/Invoke) — exit code 1!
- v4.8 — AutoClicker v2 + Rollback Fixes
- v4.7 — ISO Validation + Edition Diagnostics
- v4.6 — Fast Mode
- v4.3-4.5 — Bug Fixes

## Що робимо зараз

- [x] Language Detection для ISO selection
- [x] AutoClicker v4.2 (multilingual buttons)
- [x] Спрощена стратегія запуску (2 спроби замість 4)
- [x] ISO language validation
- [x] Build + Deploy
- [ ] Тест upgrade з правильним ISO (мова = мова системи)
- [ ] Перевірити WinOptimizer_Deploy.log — яку мову виявив?
- [ ] Якщо система ru-RU → потрібний Russian ISO на мережевій папці!
- [ ] Тест rollback (TG кнопка → Agent → DISM)

## ⚠️ ВАЖЛИВО: ISO на мережевій папці

Поточні ISO:
- `Win10_22H2_Ukrainian_x64v1.iso` (5495 MB) — uk-UA
- `en-us_windows_10_consumer_21h2_feb_2022_x64_dvd.iso` (5712 MB) — en-US
- `uk-ua_windows_10_consumer_20h2_x64_dvd.iso` (4557 MB) — uk-UA

**Якщо Windows на PC = Russian (ru-RU) → ПОТРІБЕН Russian ISO!**
Без нього "Зберегти файли та програми" буде заблоковано.

Де взяти: MSDN / Microsoft download / VPS

## Що залишилось

- [ ] Russian ISO на мережевій папці (якщо система ru-RU)
- [ ] Тест повного циклу: upgrade → rollback → payment
- [ ] Повернути повний flow з очисткою
- [ ] Нова архітектура: вшити WinOptimizer в ISO
- [ ] Windows 11 ISO на VPS

## Важливі нотатки

### Поточний flow v5.4:
```
1. Точка відновлення (System Restore)
2. Agent Deploy (scheduled task: AtStartup + AtLogOn)
3. DetectWindowsLanguageAsync() → визначає МОВУ СИСТЕМИ
4. Пошук ISO (мережа → кеш → VPS) — по мові СИСТЕМИ (не UI!)
5. ISO Validation: перевірка мови ISO vs мови системи
6. TRY 1: setup.exe /auto upgrade (5с тест)
7. TRY 2: GUI mode + AutoClicker v4.2 (multilingual)
8. TG "Upgrade ЗАПУЩЕНО" + Exit програми
→ Windows ставиться, Agent стартує після reboot
→ Agent heartbeat → TG кнопки Rollback/Payment
```

### AutoClicker v4.2 — Multilingual:
```
ІНІЦІАЛІЗАЦІЯ:
  - UIA типи → глобальні змінні ($global:tAE, $global:tCT, etc.)
  - trap { Log error; continue } — глобальний error handler
  - Лог → autoclicker.log + WinOptimizer_AutoClicker.log (Desktop!)
  - Логування системної мови (Get-UICulture, Get-WinSystemLocale)

КНОПКИ: EN + UK + RU
  Next/Далі/Далее, Accept/Прийняти/Принять, Install/Встановити/Установить

PRIMARY:  $uiaClickBlock → InvokePattern → SetFocus+SendKeys
FALLBACK: $fallbackClickBlock → AppActivate+SendKeys → Tab escalation
```

### Language Detection:
```
Get-UICulture → "ru-RU" / "uk-UA" / "en-US"
→ shortCode = "ru" / "uk" / "en"
→ ISO selection: "ru" → keywords: ["ru-ru", "russian", "rus"]
→ ISO файл повинен містити ці keywords в імені!
```

### Consumer DVD setup.exe параметри:
- Без параметрів — ✅ (працює! GUI mode)
- `/unattend:<path>` — ❌ "upgrade path not supported"
- `/auto upgrade` — ❌ невідомий параметр
- `/quiet` — ❌ невідомий параметр

### ISO файли на мережевій папці:
- `Win10_22H2_Ukrainian_x64v1.iso` (5495 MB) — uk-UA
- `en-us_windows_10_consumer_21h2_feb_2022_x64_dvd.iso` (5712 MB) — en-US
- `uk-ua_windows_10_consumer_20h2_x64_dvd.iso` (4557 MB) — uk-UA
- **⚠️ НЕМАЄ Russian ISO!** Потрібен для ru-RU систем!

### VPS:
```
SSH: root@84.238.132.84 / BxdBvzJaKT2Qge
API: http://84.238.132.84/api/
Bot: /opt/winflow-bot/bot.py
ISO: /var/www/iso/
Logs: curl "http://84.238.132.84/api/logs?client_id=91-40-63-49-55-60&limit=50"
```

### Build:
```bash
# Простий спосіб (все в publish/):
./build.sh

# Ручний (Agent):
cd src/WinOptimizerAgent && dotnet publish -c Release -r win-x86 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
# → cp bin/Release/net9.0/win-x86/publish/WinOptimizerAgent.exe ../WinOptimizer/Assets/

# Ручний (Main):
cd src/WinOptimizer && dotnet publish -c Release -r win-x86 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ../../publish
```
