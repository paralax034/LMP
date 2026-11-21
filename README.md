<div align="center">

# Lite Music Player (LMP)

**Fast, lightweight native YouTube Music player for Windows.**  
Launches in under a second, runs smoothly at 60–144+ FPS, and stays light on RAM.

[![Runtime](https://img.shields.io/badge/.NET-11.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![UI Framework](https://img.shields.io/badge/Avalonia_UI-12.1-8A2BE2?style=flat-square)](https://avaloniaui.net/)
[![Compilation](https://img.shields.io/badge/Compilation-Native_AOT-107C41?style=flat-square)](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
[![Platform](https://img.shields.io/badge/Platform-Windows_10%2F11_x64-0078D6?style=flat-square&logo=windows)](https://microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-333333?style=flat-square)](LICENSE)

[**Download**](https://github.com/paralax034/LMP/releases/dev) | [**Report Issue**](https://github.com/paralax034/LMP/issues) | [**Changelog**](https://github.com/paralax034/LMP/releases)

<br/>

[English](#english) | [Русский](#русский)

<br/>

<video src="https://github.com/user-attachments/assets/5ded1edb-9974-4ec1-8b7a-fb01dc1f36df" width="100%" autoplay loop muted playsinline style="border-radius: 8px; box-shadow: 0 8px 24px rgba(0,0,0,0.5);">
</video>

</div>

---

# English

Most desktop music players are basically web browsers wrapped in Electron. **LMP** is a 100% native Windows app — no browser engine, no bloat, just instant response and clean sound.

| Metric                  | Web / Electron Players | LMP (Native AOT)  |
| :---------------------- | :--------------------: | :---------------: |
| **Cold Startup**        |      4.0 – 8.0 s       | **~0.4 – 0.8 s**  |
| **Active Playback RAM** |    600 MB – 1.2 GB     | **~150 – 210 MB** |
| **Idle / Minimized**    |      350 – 700 MB      | **~80 – 160 MB**  |
| **Download Size**       |      120 – 250 MB      | **~17 MB (.7z)**  |
| **UI Smoothness**       |      30 – 60 FPS       | **60 – 144+ FPS** |

---

## What It Can Do

### Visuals & Theme Studio

- **Live Background Glow:** **Fluid Wave** and **Aurora** effects in Queue and Playlists that flow seamlessly into the window header and react to your music.
- **Music-Reactive Track Border:** Live waves running along the currently playing track (or classic pulse), with optional beat glow.
- **Theme Editor:** Built-in and custom themes, real-time **Live Preview** with easy rollback, readability contrast check, JSON sharing, and instant **EN / RU** switching.

<div align="center">
  <video src="https://github.com/user-attachments/assets/f2975a63-1266-4a86-99fc-d9b1b1ae0409" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

<br/>

### Equalizer & Smart Sound

- **Built-In Equalizer:** Switch between an easy **5-band** view and a detailed **10-band** curve that moves with the track, save custom presets, or bind a preset to a single song.
- **Volume Normalization:** Automatically keeps every track at a comfortable loudness (Spotify-style or YouTube-style) so you don't have to adjust the volume between songs.
- **Clean Volume Boost:** Safely boost quiet tracks above 100% without distortion, plus 5 volume slider curves to match your headphones.

<div align="center">
  <video src="https://github.com/user-attachments/assets/88681981-9d2f-474c-ae8d-60030e4c1fda" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

<br/>

### Sync, Queue & Discord Rich Presence

- **Two-Way YouTube Sync:** Keep your playlists, Liked Songs, covers, and descriptions in sync with YouTube Music, complete with an easy conflict resolver.
- **Smart Queue:** Reorder multiple tracks at once with Drag-and-Drop, filter instantly, and see elapsed / remaining / total queue time at a glance.
- **Discord Rich Presence & Media Keys:** Customize how your status looks with a live preview card in Settings, plus full Windows media overlay and keyboard key support.
- **One-Click Updates & Multi-Account:** Built-in auto-updater and quick switching between channels.

<div align="center">
  <video src="https://github.com/user-attachments/assets/16c2fabd-ce37-4439-8def-7dc1633bb362" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

<br/>

### Fast Streaming & Offline Mode

- **Instant Playback:** Tracks start right away without waiting for full downloads, and you can switch audio quality (`Opus` / `AAC`) on the fly.
- **Flexible Cache & Offline Support:** Set your own RAM and disk limits for audio, covers, and search history — cached and downloaded tracks play smoothly offline.
- **Reliable Connection:** Built-in YouTube stream decryption and automatic link recovery keep music playing without interruptions.

<div align="center">
  <video src="https://github.com/user-attachments/assets/055c3964-0737-4131-9776-e81d14dd6de2" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

---

<details>
<summary><b>Technical Architecture (Under the Hood)</b></summary>

- **Compilation:** Fully trimmed **Native AOT** (`.NET 11`, `PublishAot=true`, `TrimMode=full`) with zero runtime reflection or JIT overhead.
- **Audio & DSP:** Direct Windows **WASAPI** (Shared Paced Zero-Copy), `libopus` (`Concentus`) & `SharpJaad.AAC`, lock-free SPSC `LockFreeRingBuffer<float>`, 16-lane Mid/Side SIMD (`AVX2`/`FMA`/`SSE`) `AudioSpectrumAnalyzer`, Cytomic SVF `EqualizerProcessor`, `EbuR128Analyzer`, `TruePeakLimiter`, and windowed-sinc resampler.
- **Rendering:** Render-thread `CompositionCustomVisualHandler` + SkiaSharp `SKRuntimeEffect` (SkSL) shaders with zero UI-thread wakeups.
- **Data, Sync & Bypass:** Parameterized ADO.NET SQLite (WAL), `MemoryPack` binary cache, `CommunityToolkit.Mvvm` source generators, persistent Cloud Outbox, and embedded `QuickJS` (`quickjs_bridge.dll`) + `Acornima` AST solver for `sig`, `n-token`, and `poToken` (BotGuard).

</details>

---

## Installation & Build

**Portable:** Download `LMP-Release-latest.7z` from [**dev**](https://github.com/paralax034/LMP/releases/dev), extract, and run `LMP.exe`.

**From Source** (Windows 10/11 x64, .NET 11.0 SDK, Python 3.10+, VS Build Tools C++):

```bash
git clone https://github.com/paralax034/LMP.git
cd LMP

python build.py debug
python build.py aot-test --category=Unit run
python build.py publish
```

---

<br/>

# Русский

Большинство десктопных плееров сегодня — это браузер, завёрнутый в Electron. **LMP** написан полностью нативно под Windows: без веб-движков, без лишнего веса, с мгновенным запуском и плавным интерфейсом.

| Параметр                    | Web / Electron плееры | LMP (Native AOT)  |
| :-------------------------- | :-------------------: | :---------------: |
| **Холодный запуск**         |      4.0 – 8.0 с      | **~0.4 – 0.8 с**  |
| **ОЗУ при воспроизведении** |    600 МБ – 1.2 ГБ    | **~150 – 210 МБ** |
| **ОЗУ в фоне / покое**      |     350 – 700 МБ      | **~80 – 160 МБ**  |
| **Размер архива**           |     120 – 250 МБ      | **~17 МБ (.7z)**  |
| **Плавность интерфейса**    |      30 – 60 FPS      | **60 – 144+ FPS** |

---

## Возможности

### Оформление и живые эффекты

- **Фоновая подсветка:** Режимы **Живая волна** и **Аврора** в Очереди и Плейлистах плавно перетекают в шапку окна и откликаются на музыку.
- **Волновая рамка трека:** Волны, бегущие слева направо в такт битам и вокалу (или классическая пульсация), с опциональным свечением.
- **Студия тем:** Готовые и свои темы, **Живой предпросмотр** с откатом в один клик, проверка читаемости текста, экспорт/импорт в JSON и мгновенное переключение **RU / EN**.

<div align="center">
  <video src="https://github.com/user-attachments/assets/f2975a63-1266-4a86-99fc-d9b1b1ae0409" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

<br/>

### Эквалайзер и звук

- **Встроенный эквалайзер:** Простой режим (5 полос) или точный (10 полос) с живой волной от музыки, усилением до эквалайзера, своими пресетами и **профилями для отдельных треков**.
- **Выравнивание громкости:** Автоматически держит комфортный уровень между тихими и громкими треками (как в Spotify или как на YouTube).
- **Чистое усиление звука:** Безопасный буст выше 100% без хрипов и 5 кривых громкости под любые наушники.

<div align="center">
  <video src="https://github.com/user-attachments/assets/88681981-9d2f-474c-ae8d-60030e4c1fda" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

<br/>

### Синхронизация, очередь и Discord

- **Двусторонний синк с YouTube Music:** Синхронизация плейлистов, «Понравившихся», обложек и описаний с удобным окном выбора при конфликтах.
- **Удобная очередь:** Перетаскивание сразу нескольких треков, быстрый фильтр, счётчик времени (`Прослушано / Осталось / Всего`) и автообновление при правках плейлиста.
- **Discord Rich Presence и медиа-клавиши:** Настройка статуса с живым предпросмотром прямо в приложении + полная поддержка системных кнопок Windows.
- **Обновления в один клик и мультиаккаунт:** Автопроверка релизов на GitHub и быстрое переключение каналов.

<div align="center">
  <video src="https://github.com/user-attachments/assets/16c2fabd-ce37-4439-8def-7dc1633bb362" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

<br/>

### Быстрый стриминг и оффлайн

- **Мгновенный старт:** Треки начинают играть сразу, не дожидаясь полной загрузки, а качество (`Opus` / `AAC`) можно менять прямо во время прослушивания.
- **Гибкий кэш и оффлайн-режим:** Свои лимиты памяти и диска для музыки, обложек и поиска — всё сохранённое и кэшированное работает без интернета.
- **Стабильная работа с YouTube:** Встроенная расшифровка потоков и автообновление ссылок при сбоях.

<div align="center">
  <video src="https://github.com/user-attachments/assets/055c3964-0737-4131-9776-e81d14dd6de2" width="90%" autoplay loop muted playsinline style="border-radius: 6px;">
  </video>
</div>

---

<details>
<summary><b>Техническая архитектура (Для разработчиков)</b></summary>

- **Компиляция:** Полный **Native AOT** (`.NET 11`, `PublishAot=true`, `TrimMode=full`) без JIT и рефлексии.
- **Аудио и DSP:** Прямой **WASAPI** (Shared Paced Zero-Copy), `libopus` (`Concentus`) и `SharpJaad.AAC`, неблокирующий `LockFreeRingBuffer<float>`, 16-канальный Mid/Side SIMD (`AVX2`/`FMA`/`SSE`) анализатор спектра, `EqualizerProcessor` (Cytomic SVF), `EbuR128Analyzer`, `TruePeakLimiter` и оконный sinc-ресэмплер.
- **Рендеринг:** Поток композитора `CompositionCustomVisualHandler` + шейдеры SkiaSharp `SKRuntimeEffect` (SkSL) без нагрузки на UI-поток.
- **Данные и сеть:** Параметризованный ADO.NET SQLite (WAL), бинарный кэш `MemoryPack`, `CommunityToolkit.Mvvm` Source Generators, Cloud Outbox и встроенный `QuickJS` (`quickjs_bridge.dll`) + AST-анализатор `Acornima` для `sig`, `n-token` и `poToken` (BotGuard).

</details>

---

## Установка и сборка

**Готовая сборка:** Скачайте `LMP-Release-latest.7z` из [**dev**](https://github.com/paralax034/LMP/releases/dev), распакуйте и запустите `LMP.exe`.

**Из исходников** (Windows 10/11 x64, .NET 11.0 SDK, Python 3.10+, VS Build Tools C++):

```bash
git clone https://github.com/paralax034/LMP.git
cd LMP

python build.py debug
python build.py aot-test --category=Unit run
python build.py publish
```

---

<div align="center">

_Developed by **paralax034** with <3_

</div>
