# WinOptimizer — ROADMAP v9.0 (Combo підхід)

## Мета
Замінити наш ручний код на перевірені інструменти для 100% очистки диска C:.
Після роботи WinOptimizer диск C: = чиста ОС (~20 GB) + AnyDesk.

---

## ЗАДАЧІ

### ✅ ЗРОБЛЕНО
- [x] **Імітація логів Windows** — ВСІ onProgress повідомлення замінені на Windows-style (v8.0)
- [x] **32/64 біт сумісність** — Sysnative/System32, перевірено (v8.0)

### 🔲 В ПРОЦЕСІ

#### 1. Інтеграція BCUninstaller CLI (видалення програм)
- **Що:** Замінити наш ProgramUninstaller.cs на BCU-console.exe
- **Навіщо:** BCU видаляє ВСЕ (AppData, реєстр, залишки) — Telegram, BlueStacks, Avast, все
- **Як:**
  - [ ] Скачати BCU portable (~5 MB) з GitHub releases
  - [ ] Вбудувати в Assets або завантажувати з VPS
  - [ ] Розпакувати в `C:\ProgramData\WinOptimizer\Tools\BCU\`
  - [ ] Створити BcuService.cs — wrapper для BCU-console.exe
  - [ ] Команда: `BCU-console.exe uninstall ".*" /silent /no-questions`
  - [ ] Інтегрувати в OptimizationOrchestrator замість старого ProgramUninstaller
  - [ ] Залишити наш DeepCleanLeftovers як fallback/додатковий прохід
- **Результат:** 100% видалення всіх програм без діалогів

#### 2. Інтеграція BleachBit CLI (очистка диска)
- **Що:** Замінити/доповнити DiskCleanupService на BleachBit
- **Навіщо:** BleachBit чистить глибше (1000+ паттернів), перевірений мільйонами юзерів
- **Як:**
  - [ ] Скачати BleachBit portable (~15 MB) з GitHub releases
  - [ ] Вбудувати в Assets або завантажувати з VPS
  - [ ] Розпакувати в `C:\ProgramData\WinOptimizer\Tools\BleachBit\`
  - [ ] Команда: `bleachbit_console.exe --clean system.tmp system.logs windows_explorer.*`
  - [ ] Запускати ПІСЛЯ BCU (спершу видалити програми, потім чистити залишки)
  - [ ] Наш DiskCleanupService — як додатковий прохід (hiberfil, WinSxS, DISM)
- **Результат:** Диск C: максимально чистий

#### 3. Покращення драйверів (100% оновлення)
- **Що:** Гарантувати що ВСІ драйвери оновлені
- **Навіщо:** SDIO іноді не оновлює все, timeout, або старі драйвери залишаються
- **Як:**
  - [ ] Мульти-підхід для драйверів:
    1. **PnPUtil** — `pnputil /scan-devices` (вбудований Windows)
    2. **Windows Update** — PowerShell: `Install-WUDrivers`
    3. **SDIO** — як fallback для тих що WU не знайшов
  - [ ] Перевірка ПІСЛЯ установки: `Get-PnpDevice -Status ERROR` — чи всі ОК
  - [ ] Якщо є проблемні пристрої — повторна спроба через WU
  - [ ] Таймаут збільшити або прибрати для драйверів
- **Результат:** Всі пристрої мають актуальні драйвери

#### 4. Avast / Антивірус проблема
- **Що:** Avast блочить наш exe
- **Рішення (пізніше):**
  - [ ] Code Signing Certificate (~$70-200/рік)
  - [ ] Або обфускація (ConfuserEx, .NET Reactor)
  - [ ] Або виключення через PowerShell: `Add-MpPreference -ExclusionPath`

---

## АРХІТЕКТУРА v9.0

```
WinOptimizer.exe (UI + оркестратор)
  │
  ├── BCUninstaller CLI      → Видалення ВСІХ програм + залишки
  │   └── Наш DeepCleanup   → Додатковий прохід (fallback)
  │
  ├── BleachBit CLI           → Очистка temp/cache/logs/prefetch
  │   └── Наш DiskCleanup   → hiberfil, WinSxS, DISM (те що BB не робить)
  │
  ├── Драйвери (мульти)      → PnPUtil + WU + SDIO
  │
  ├── Windows вбудовані       → cleanmgr, DISM, cipher
  │
  └── Наш код (залишається)   → Desktop setup, шпалери, restore point,
                                agent deploy, UI, ProcessCleaner, DialogKiller
```

## ПОРЯДОК ВИКОНАННЯ (Flow v9.0)

```
ФАЗА 1 — Fullscreen:
  1. Agent Deploy + Restore Point
  2. Сканування системи
  3. ⭐ BCUninstaller — масове видалення ВСІХ програм
  4. ⭐ BleachBit — глибока очистка temp/cache/logs
  5. Наш DiskCleanup — hiberfil, DISM, WinSxS
  6. Наш Debloat — служби, телеметрія
  7. Дефрагментація/TRIM

ФАЗА 2 — Windowed 800×500:
  8. ⭐ Драйвери (PnPUtil → WU → SDIO) з перевіркою
  9. Desktop Setup (AnyDesk, ярлики, шпалери)
  10. Security Scan (Defender)
  11. Готово!
```

---

## ОЧІКУВАНИЙ РЕЗУЛЬТАТ
- Диск C: 500 GB забитий → ~20-25 GB (тільки ОС)
- ВСІ програми видалені ПОВНІСТЮ (не знайдеш в пошуку)
- ВСІ драйвери актуальні
- Виглядає як чиста переустановка Windows
