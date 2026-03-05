# Остання зміна: 2026-03-05 (v6.6 — Dialog Killer + Taskbar Fix + BleachBit Fix)

## Git
- **Попередній коміт:** `cea3e87` — `v6.4: Full Disk C cleanup + Process Killer + Silent Mode + Premium UI`
- **Бранч:** `claude/inspiring-snyder`
- `.gitignore`: виключає `bin/`, `obj/`, `publish/`, `*.exe`

## Що зроблено сьогодні

### v6.6 — Dialog Killer + Taskbar Fix + BleachBit Fix

#### 1. DialogKillerService — ГЛОБАЛЬНИЙ УБИВЦЯ ДІАЛОГІВ (НОВЕ!)

**Проблеми з тесту:**
- BleachBit показував діалог "MSVCR100.dll не знайдено"
- BlueStacks показував survey "чому ви видаляєте?"
- Windows показував "Як ви хочете відкрити це?"
- TLauncher та інші показували uninstall wizards

**Рішення: DialogKillerService.cs** — фоновий сервіс що працює ВСЮ оптимізацію:
1. `SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX)` — suppresses system DLL error dialogs
2. Кожні 1.5с сканує ВСІ вікна на системі
3. Вбиває процеси: OpenWith.exe, WerFault.exe, dwwin.exe
4. Вбиває вікна з ключовими словами: "uninstall", "удалить", "системная ошибка", "msvcr", "как вы хотите открыть", "are you sure", тощо
5. Ігнорує наші вікна (WinFlow, WinOptimizer, Explorer)
6. Запускається в Orchestrator.RunAsync() на початку, зупиняється в finally

#### 2. BleachBit — перевірка MSVCR100.dll

**Проблема:** `bleachbit_console.exe` потребує MSVCR100.dll (VC++ 2010 Runtime). Якщо DLL немає — показує системну помилку.

**Виправлення:**
- `CanRunBleachBit()` — перевіряє наявність MSVCR100.dll в System32 та SysWOW64
- Якщо DLL не знайдено — BleachBit пропускається повністю (без помилки)
- Плюс DialogKiller тепер вбиває будь-які DLL error діалоги

#### 3. Taskbar Cleanup — ПОВНИЙ ПЕРЕПИС

**Проблема:** Після завершення оптимізації на таскбарі залишались іконки (YouTube, TG, WPS, тощо).

**Причини:**
- Старий код видаляв тільки .lnk файли
- Win10/11 зберігає pins в registry binary blob (TaskBand)
- Explorer не оновлювався правильно

**Нове рішення (4 методи):**
1. Видалення .lnk з Quick Launch для ВСІХ юзерів
2. Очистка ImplicitAppShortcuts
3. Очистка TaskBand registry binary blob (Favorites/FavoritesResolve) для ВСІХ юзерів через HKEY_USERS
4. Win11-specific: очистка нового формату таскбара
5. Вимкнення Search box, Task View, Widgets, Chat, News & Interests
6. Перезапуск Explorer

**Подвійна очистка:** таскбар чиститься 2 рази:
- 1-й раз: в WindowsDebloatService (крок 7)
- 2-й раз: FinalTaskbarCleanupAsync() — ПІСЛЯ всіх видалень (перед Completed)

#### 4. Win7/8/11 Compatibility (з попередньої сесії)

| Сервіс | Win7/8 fallback | Win10/11 |
|--------|----------------|----------|
| **AntivirusScanner** | `MpCmdRun.exe -Scan -ScanType 1` | `Start-MpScan` cmdlet |
| **DefragService** | `defrag.exe C: /O` (HDD), skip TRIM (SSD) | `Optimize-Volume` cmdlet |
| **DriverUpdater** | `pnputil -e` + `wuauclt /detectnow` | `pnputil /scan-devices` + `UsoClient` |
| **ServiceOptimizer** | `SuperFetch` ім'я служби | `SysMain` ім'я служби |
| **ProgramUninstaller** | UWP skip | UWP removal через Get-AppxPackage |

#### 5. ProgramUninstaller — Silent Uninstall (з попередньої сесії)

**Стратегія:** Kill process → Silent uninstall (15с) → Dialog monitor → Force delete folder

### Попередні зміни:
- v6.5: Silent Uninstall + Win7/8/11 compat
- v6.4: Full Disk C: cleanup + Process Killer + Silent Mode
- v6.3: Full Disk C: Cleanup (всі папки юзерів + корінь C:\)
- v6.1: Premium UI + WindowsDebloatService + BleachBitService
- v6.0: Повна зміна архітектури

## Build

```
src\WinOptimizer\bin\Release\net9.0\win-x86\publish\WinOptimizer.exe
```

0 помилок, 21 попереджень (pre-existing)

## Поточний flow v6.6

```
ФАЗА 0 — DialogKiller START (фон):
- SetErrorMode() — suppress system error dialogs
- Background scan: кожні 1.5с вбиває ВСІ діалоги/помилки/wizards

ФАЗА 1 — Підготовка:
1. Створення точки відновлення (System Restore)
2. Agent Deploy

ФАЗА 2 — Очистка:
3. Сканування системи
4. Видалення програм (SILENT!) + UWP (Win10+ only)
5. Очистка браузерів
6. ПОВНА очистка диску C: + BleachBit (з перевіркою MSVCR100.dll)
7. Оптимізація диску (TRIM/defrag)

ФАЗА 3 — Глибока оптимізація:
8. Оптимізація служб + Windows Debloat + Taskbar cleanup #1
9. Очистка автозавантаження
10. Оновлення драйверів
11. Перевірка безпеки (Defender Quick Scan)

ФАЗА 4 — Завершення:
12. Taskbar cleanup #2 (фінальний прохід)
13. Reset wallpaper
14. Готово! + DialogKiller STOP
```

## Сумісність

| Компонент | Win 7 | Win 8/8.1 | Win 10 | Win 11 |
|-----------|-------|-----------|--------|--------|
| DialogKiller | ✓ | ✓ | ✓ | ✓ |
| Cleanup (Disk) | ✓ | ✓ | ✓ | ✓ |
| Programs Uninstall | ✓ | ✓ | ✓ | ✓ |
| UWP Removal | ✗ (skip) | ✗ (skip) | ✓ | ✓ |
| BleachBit | ✓ (if DLL) | ✓ (if DLL) | ✓ | ✓ |
| Taskbar Cleanup | ✓ (.lnk) | ✓ (.lnk) | ✓ (full) | ✓ (full) |
| Defrag (HDD) | ✓ (defrag.exe) | ✓ (Optimize-Volume) | ✓ | ✓ |
| Defender Scan | ✓ (MpCmdRun) | ✓ (MpCmdRun) | ✓ (cmdlets) | ✓ |
| Driver Update | ✓ (wuauclt) | ✓ (pnputil -e) | ✓ (pnputil) | ✓ |
| Service Optimize | ✓ (SuperFetch) | ✓ | ✓ (SysMain) | ✓ |

## Rollback стратегія

```
Rollback:  System Restore → reboot → все повертається
Оплата:    видалити Restore Point → agent self-delete
```

## Що залишилось

- [ ] Тест на Windows PC — повний цикл v6.6
- [ ] Тест rollback з TG
- [ ] Тест payment з TG

## VPS

```
SSH: root@84.238.132.84 / BxdBvzJaKT2Qge
API: http://84.238.132.84/api/
Bot: /opt/winflow-bot/bot.py
Logs: curl "http://84.238.132.84/api/logs?client_id=HWID&limit=50"
```
