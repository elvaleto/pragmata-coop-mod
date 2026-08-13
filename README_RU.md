# PRAGMATA Split Control (Кооперативный мод на двух игроков)

![PRAGMATA Split Control](release/images/pragmata_split_control_cover_v1.png)

*Читать на других языках: [English](README.md)*

Локальный кооперативный мод для игры **PRAGMATA** (Steam-версия 1.2.2.0+). 

Мод позволяет играть вдвоем на одном ПК:
- **Игрок 1 (Hugh)**: полностью управляет персонажем, перемещением, боем и выбором целей для взлома.
- **Игрок 2 (Diana)**: использует второй геймпад для решения мини-игр и головоломок взлома (`Y/X/A/B` или `▲/■/X/●`).

---

## Архитектура и принцип работы

Игровой движок RE Engine по умолчанию объединяет ввод со всех подключенных геймпадов в одно устройство. Чтобы разделить управление, проект состоит из трех компонентов:

1. **Нативный фильтр ввода (`native/PragmataInputFilter.cpp`)**:
   - Выполняет перехват функций `XINPUT1_4.dll` в процессе `PRAGMATA.exe`.
   - Изолирует контроллеры: игра «видит» только геймпад Игрока 1, а сигналы второго геймпада перехватываются модом.
   - Ввод с клавиатуры и мыши никогда не фильтруется и всегда доступен Игроку 1.

2. **C#-плагин для REFramework (`managed/PragmataSplitControl/`)**:
   - Читает ввод со второго геймпада через `XINPUT9_1_0.dll`.
   - Передает команды взлома Игрока 2 непосредственно в логику игры во время активной мини-игры.
   - Если второй геймпад отключается, управление взломом автоматически возвращается Игроку 1.

3. **Конфигуратор (`tools/PragmataSplitControlConfigurator.cs`)**:
   - WinForms-приложение для удобной привязки контроллеров к игрокам и проверки ввода в реальном времени.

---

## Поддерживаемые конфигурации контроллеров

- **DualSense / DUALSHOCK 4 (нативно) для P1 + Xbox/XInput для P2** (рекомендуется для сохранения адаптивных триггеров DualSense).
- **Два XInput / Xbox геймпада**.
- **Клавиатура + Мышь для P1 + XInput геймпад для P2**.

---

## Структура репозитория

```
├── managed/               # C# плагин для REFramework
│   └── PragmataSplitControl/
│       ├── PragmataSplitControl.cs
│       ├── PragmataSplitControl.csproj
│       └── AssemblyInfo.cs
├── native/                # Нативный C++ фильтр ввода (DLL)
│   └── PragmataInputFilter.cpp
├── tools/                 # Исходный код конфигуратора WinForms
│   └── PragmataSplitControlConfigurator.cs
├── release/               # Шаблоны конфигурации и ресурсы
├── README.md              # Документация на английском
└── README_RU.md           # Документация на русском
```

---

## Требования и сборка

### Требования:
- [REFramework](https://github.com/praydog/REFramework-nightly/releases) 

### Сборка:
1. **C#-плагин**: `dotnet build managed/PragmataSplitControl/PragmataSplitControl.csproj -c Release`
2. **Конфигуратор**: Сборка `tools/PragmataSplitControlConfigurator.cs` в `PragmataSplitControl_Config.exe`
3. **C++ фильтр**: Сборка `native/PragmataInputFilter.cpp` в `PragmataSplitControl_InputFilter.dll` (x64 DLL)

---

## Установка и использование

1. Установите REFramework
2. Поместите скомпилированные файлы мода в папку с `PRAGMATA.exe`.
3. Запустите `PragmataSplitControl_Config.exe`, назначьте геймпады и сохраните настройки.
4. Запустите игру.
