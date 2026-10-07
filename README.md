<div align="center">

# Lite Music Player (LMP)

**A high-performance, resource-efficient native YouTube Music desktop client for Windows.**  
Engineered with **.NET 11**, **Avalonia UI 12.1**, and **Native AOT**.

[![Runtime](https://img.shields.io/badge/.NET-11.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![UI Framework](https://img.shields.io/badge/Avalonia_UI-12.1-8A2BE2?style=flat-square)](https://avaloniaui.net/)
[![Compilation](https://img.shields.io/badge/Compilation-Native_AOT-107C41?style=flat-square)](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
[![Platform](https://img.shields.io/badge/Platform-Windows_10%2F11_x64-0078D6?style=flat-square&logo=windows)](https://microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-333333?style=flat-square)](LICENSE)

[**Download**](https://github.com/paralax034/LMP/releases/dev) | [**Report Issue**](https://github.com/paralax034/LMP/issues) | [**Changelog**](https://github.com/paralax034/LMP/releases)

<br/>

[English](#english) | [Русский](#русский)

<br/>

<!-- HERO DEMO MP4 -->
<video src="https://github.com/user-attachments/assets/ccc83af4-40fe-4048-b60f-e7c344d0ec91" width="100%" autoplay loop muted playsinline style="border-radius: 8px; box-shadow: 0 8px 24px rgba(0,0,0,0.5);">
</video>

</div>

---

# English

## Overview

Most modern desktop music players are web applications packaged into heavy Chromium/Electron wrappers. They often consume 600 MB to 1.5 GB of RAM, launch slowly, and experience frame drops during navigation.

**LMP** is a fully native client built without web technologies. It compiles directly to machine code (Native AOT) and utilizes hardware-accelerated rendering, delivering instant UI responsiveness, minimal memory footprint, and native YouTube Music integration.

---

## Performance Comparison

| Metric                   | Web / Electron Clients  | LMP (Native AOT)  |          Impact          |
| :----------------------- | :---------------------: | :---------------: | :----------------------: |
| **Cold Startup**         |       4.0 – 8.0 s       |    **~1.2 s**     |      Instant launch      |
| **Active Playback RAM**  |     600 MB – 1.2 GB     | **~180 – 240 MB** |   Up to 4x less memory   |
| **Minimized / Tray RAM** |      350 – 700 MB       | **~130 – 160 MB** |  Zero background waste   |
| **Distribution Size**    |      120 – 250 MB       | **~16 MB (.7z)**  | Compact standalone build |
| **UI Rendering**         | 30 – 60 FPS (CPU bound) | **60 – 144+ FPS** |   Skia GPU Compositor    |

---

## Core Features

### Visual Customization and Dynamic Effects

- Built-in theme presets: Paralax Purple, AMOLED Black, Classic Green, Ocean Deep, and more.
- Real-time animated audio wave border reacting to currently playing tracks.
- Customizable animation speeds and full control over accent colors.

<div align="center">
  <video src="https://github.com/user-attachments/assets/35e184d4-5294-43c3-9e48-1d4f1a67d0ae" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

<br/>

### EBU R128 / LUFS Loudness Normalization

- Integrated volume normalization matching modern streaming standards.
- Dual-mode processing (Spotify-like smooth attenuation vs. YouTube standard).
- Configurable target LUFS threshold (-25 to -10 LUFS) and maximum gain limiting.
- Custom non-linear volume curves (Quadratic / Linear / Cubic).

<div align="center">
  <video src="https://github.com/user-attachments/assets/575f3dbe-b438-4c15-9985-b0b49e9e34dd" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

<br/>

### YouTube Cloud & Local Playlist Synchronization

- Complete two-way sync for playlists, favorites (Liked Songs), and track metadata.
- Smart conflict resolution engine (Merge, Duplicate, or Overwrite remote playlists).
- Quick playlist creation with custom artwork, color tagging, and cloud backup.
- Multi-account management with instant session switching.

<div align="center">
  <video src="https://github.com/user-attachments/assets/212907b7-d1fc-4ab7-a7f5-563449918f7a" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

<br/>

### Intelligent Streaming, Caching and Network Economy

- **Adaptive Chunk Streaming:** Instant track playback without waiting for full audio downloads.
- **Two-Tier Storage Cache:** Configurable disk and RAM quotas for audio files, album covers, and search indexes.
- **Offline Mode:** Seamless local playback for cached and downloaded tracks.
- **Automated YouTube Cipher Bypass:** Background decryption of `sig`, `n-token`, and `poToken` parameters with automatic client profile rotation.

<div align="center">
  <video src="https://github.com/user-attachments/assets/cb0b3669-cfb9-42f7-8f70-b988ac60fae8" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

---

<details>
<summary><b>Technical Architecture & Internal Pipeline (Click to expand)</b></summary>

<br/>

- **Compilation Model:** Fully trimmed Native AOT compilation via the .NET ILC compiler into an independent native executable without JIT overhead.
- **Audio Pipeline:** Direct Windows WASAPI / WinMM audio output engine coupled with native `Concentus` (Opus) and `SharpJaad` (AAC) decoders feeding lock-free ring buffers (`LockFreeRingBuffer`).
- **MVVM Layer:** Zero-reflection view models driven by compile-time Source Generators (`CommunityToolkit.Mvvm`).
- **Data Persistence:** Raw ADO.NET SQLite database operations paired with high-speed binary serialization (`MemoryPack`).
- **Network Stack:** Centralized `NetworkManager` providing connection pooling, DNS-over-HTTPS (DoH), and system/custom proxy support.
- **Tooling Ecosystem:** Cross-platform build automation engine and zero-allocation static analysis toolset powered by Python 3.

</details>

---

## Installation & Build

### Portable Binary (Ready to run)

Download the latest `LMP-Release-latest.7z` from the [**dev**](https://github.com/paralax034/LMP/releases/dev) release, extract to any folder, and launch `LMP.exe`.

### Building from Source

**Requirements:**

- Windows 10 / 11 (x64)
- .NET 11.0 SDK
- Python 3.10+ (for cross-platform build orchestration and tooling)
- Visual Studio Build Tools (C++ Desktop Development workload for Native AOT linking)

```bash
# Clone the repository
git clone https://github.com/paralax034/LMP.git
cd LMP

# Run in debug mode (via cross-platform build engine)
python build.py debug
# or on Windows:
build.bat debug

# Build standalone Native AOT release
python build.py publish
# or on Windows / Unix:
build.bat publish
# ./build.sh publish
```

---

<br/>

# Русский

## Описание проекта

Большинство современных десктопных музыкальных клиентов представляют собой веб-страницы, обёрнутые в тяжёлый Chromium/Electron. Они расходуют от 600 МБ до 1.5 ГБ оперативной памяти, долго запускаются и подтормаживают при навигации.

**LMP (Lite Music Player)** — полностью нативное десктопное приложение без использования веб-движков. Проект компилируется напрямую в машинный код (Native AOT) и использует аппаратный GPU-рендеринг, обеспечивая мгновенный запуск, стабильный фреймрейт и нативную интеграцию с YouTube Music.

---

## Сравнение производительности

| Параметр                    |    Web / Electron клиенты    | LMP (Native AOT)  |       Преимущество        |
| :-------------------------- | :--------------------------: | :---------------: | :-----------------------: |
| **Холодный запуск**         |         4.0 – 8.0 с          |    **~1.2 с**     |   Мгновенная готовность   |
| **ОЗУ при воспроизведении** |       600 МБ – 1.2 ГБ        | **~180 – 240 МБ** |  До 4 раз меньше памяти   |
| **ОЗУ в трее / фоне**       |         350 – 700 МБ         | **~130 – 160 МБ** | Экономия ресурсов системы |
| **Размер архива**           |         120 – 250 МБ         | **~16 МБ (.7z)**  |  Компактный дистрибутив   |
| **Отрисовка UI**            | 30 – 60 FPS (нагрузка на ЦП) | **60 – 144+ FPS** |  Плавный GPU Skia рендер  |

---

## Основные возможности

### Персонализация и живые визуальные эффекты

- Встроенные цветовые темы: Paralax Purple, AMOLED Black, Classic Green, Ocean Deep и другие.
- Реактивная волновая анимация рамки играющего трека в реальном времени.
- Настройка скорости пульсации и полное управление акцентными цветами интерфейса.

<div align="center">
  <video src="https://github.com/user-attachments/assets/35e184d4-5294-43c3-9e48-1d4f1a67d0ae" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

<br/>

### Нормализация звука (LUFS / EBU R128)

- Аппаратное выравнивание громкости между разнородными треками.
- Режимы обработки: двухсторонний (как в Spotify) или только понижение (стандарт YouTube).
- Выбор целевого уровня громкости (от -25 до -10 LUFS) и ограничение максимального усиления.
- Настройка кривых затухания громкости (Квадратичная / Линейная / Кубическая).

<div align="center">
  <video src="https://github.com/user-attachments/assets/575f3dbe-b438-4c15-9985-b0b49e9e34dd" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

<br/>

### Синхронизация плейлистов с YouTube Music

- Двусторонний синк локальных плейлистов, лайков («Понравившиеся») и метаданных.
- Разрешение конфликтов при импорте (Объединить треки, Создать копию, Пропустить).
- Создание плейлистов с выбором обложки, цветового кода и сохранением в аккаунт.
- Поддержка нескольких аккаунтов с быстрым переключением сессий.

<div align="center">
  <video src="https://github.com/user-attachments/assets/212907b7-d1fc-4ab7-a7f5-563449918f7a" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

<br/>

### Потоковое воспроизведение, кэш и экономия сети

- **Чанковый стриминг:** Треки начинают звучать моментально без ожидания полной загрузки файла.
- **Двухуровневый кэш:** Раздельные настраиваемые квоты на диске и в RAM для аудио, обложек и поиска.
- **Оффлайн-режим:** Бесшовное воспроизведение сохраненных треков при отсутствии подключения.
- **Обход блокировок YouTube:** Фоновая дешифровка токенов `sig`, `n-token`, `poToken` и динамическая ротация клиентских профилей для защиты от троттлинга скорости.

<div align="center">
  <video src="https://github.com/user-attachments/assets/cb0b3669-cfb9-42f7-8f70-b988ac60fae8" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

---

<details>
<summary><b>Техническая архитектура приложения (Нажмите, чтобы развернуть)</b></summary>

<br/>

- **Модель компиляции:** Native AOT сборка через ILC-компилятор .NET в единый независимый бинарный файл без участия JIT.
- **Аудио-пайплайн:** Прямой вывод через WASAPI / WinMM и программные декодеры `Concentus` (Opus) / `SharpJaad` (AAC) с неблокирующими кольцевыми буферами (`LockFreeRingBuffer`).
- **Слой MVVM:** Компилируемые привязки и Source Generators библиотеки `CommunityToolkit.Mvvm` без использования динамической рефлексии.
- **Хранилище данных:** Прямое взаимодействие с SQLite через низкоуровневый ADO.NET и бинарную сериализацию `MemoryPack`.
- **Сетевой стек:** Централизованный `NetworkManager` с пулом соединений, поддержкой DNS-over-HTTPS (DoH) и системных/кастомных прокси.
- **Инструментарий:** Кроссплатформенный движок сборки и статические анализаторы без аллокаций памяти на базе Python 3.

</details>

---

## Установка и сборка

### Готовая сборка (Portable)

Скачайте архив `LMP-Release-latest.7z` из [**dev**](https://github.com/paralax034/LMP/releases/dev), распакуйте в любую папку и запустите `LMP.exe`.

### Сборка из исходников

**Требования:**

- Windows 10 / 11 (x64)
- .NET 11.0 SDK
- Python 3.10+ (для кроссплатформенного сборочного движка и утилит)
- Visual Studio Build Tools (компоненты «Разработка классических приложений на C++» для компоновщика Native AOT)

```bash
# Клонирование репозитория
git clone https://github.com/paralax034/LMP.git
cd LMP

# Запуск в режиме отладки (через кроссплатформенный сборочный движок)
python build.py debug
# или на Windows:
build.bat debug

# Публикация нативного релиза (Native AOT)
python build.py publish
# или на Windows / Unix:
build.bat publish
# ./build.sh publish
```

---

<div align="center">

_Developed by **paralax034** with <3_

</div>
