namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Абстракция над аудио API операционной системы (WaveOut, ALSA, PulseAudio, etc).
///
/// <para><b>Lifecycle:</b></para>
/// <list type="number">
///   <item><see cref="Initialize"/> — первичная инициализация (создаёт устройство, запускает fill loop)</item>
///   <item><see cref="Reinitialize"/> — переинициализация для нового трека (fast/slow path)</item>
///   <item><see cref="ActivateFillLoop"/> — активирует перекачку данных в буфер БЕЗ воспроизведения</item>
///   <item><see cref="WaitForWarmup"/> — ожидает накопления минимума данных в буфере</item>
///   <item><see cref="Start"/> — запускает воспроизведение</item>
///   <item><see cref="Stop"/> — останавливает с micro fade-out</item>
///   <item><see cref="Flush"/> — очищает буферы (для seek)</item>
/// </list>
///
/// <para><b>Warmup Protocol:</b></para>
/// <para>Для предотвращения артефактов при старте трека, вызывающий код должен:</para>
/// <code>
/// backend.ActivateFillLoop();          // Fill loop начинает наполнять BufferedWaveProvider
/// backend.WaitForWarmup(timeoutMs);    // Ждём ≥200ms данных в буфере
/// backend.Start();                     // Теперь безопасно запускать воспроизведение
/// </code>
///
/// <para><b>Device Loss Protocol:</b></para>
/// <para>Backend уведомляет подписчиков о событиях устройства через callbacks:</para>
/// <code>
/// backend.SetDeviceLostCallback(() => /* soft pause */);
/// backend.SetDeviceAvailableCallback(() => /* auto-recovery */);
/// backend.SetStarvationCallback(() => /* диагностика */);
/// </code>
/// <para>Все callbacks вызываются из фонового потока — реализация не должна блокировать.</para>
/// </summary>
public interface IPlaybackBackend : IDisposable
{
    /// <summary>
    /// Первичная инициализация backend.
    /// Создаёт аудио-устройство и запускает fill loop.
    /// </summary>
    /// <param name="sampleRate">Частота дискретизации (Гц).</param>
    /// <param name="channels">Количество каналов.</param>
    /// <param name="dataCallback">Callback поставки PCM данных.</param>
    void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback);

    /// <summary>
    /// Переинициализирует backend для нового трека.
    ///
    /// <para><b>Fast path</b> (формат совпадает): обновляет callback, flush буферов. ~0ms.</para>
    /// <para><b>Slow path</b> (формат изменился): пересоздаёт аудио-устройство. ~50-200ms.</para>
    /// <para><b>First call</b> (Initialize не вызывался): делегирует к Initialize.</para>
    ///
    /// <para>После Reinitialize backend находится в остановленном состоянии.
    /// Вызывающий код должен использовать warmup protocol:
    /// <see cref="ActivateFillLoop"/> → <see cref="WaitForWarmup"/> → <see cref="Start"/>.</para>
    /// </summary>
    /// <param name="sampleRate">Частота дискретизации (Гц).</param>
    /// <param name="channels">Количество каналов.</param>
    /// <param name="dataCallback">Callback поставки PCM данных.</param>
    void Reinitialize(int sampleRate, int channels, AudioDataCallback dataCallback);

    /// <summary>
    /// Активирует fill loop для перекачки данных из callback во внутренний буфер,
    /// БЕЗ запуска воспроизведения.
    ///
    /// <para>Вызывается перед <see cref="WaitForWarmup"/> для наполнения буфера
    /// до того, как <see cref="Start"/> запустит аудио-устройство.</para>
    ///
    /// <para>Если fill loop уже активен — no-op.</para>
    /// </summary>
    void ActivateFillLoop();

    /// <summary>
    /// Ожидает накопления минимального количества данных во внутреннем буфере.
    ///
    /// <para>Блокирует вызывающий поток. Используется между
    /// <see cref="ActivateFillLoop"/> и <see cref="Start"/> для гарантии
    /// что аудио-устройство не начнёт читать из пустого буфера.</para>
    /// </summary>
    /// <param name="timeoutMs">Максимальное время ожидания (мс).</param>
    /// <returns>true если буфер прогрет до минимального уровня, false если таймаут.</returns>
    bool WaitForWarmup(int timeoutMs = 3000);

    /// <summary>Запускает воспроизведение.</summary>
    void Start();

    /// <summary>Останавливает (приостанавливает) воспроизведение с micro fade-out.</summary>
    void Stop();

    /// <summary>
    /// Очищает внутренний буфер. Вызывается при seek для немедленного
    /// перехода к новой позиции без проигрывания устаревших данных.
    /// </summary>
    void Flush();

    //  Device Loss 

    /// <summary>
    /// true если аудиоустройство потеряно (BT disconnect, USB unplug и т.д.).
    /// При <c>true</c> вызовы <see cref="Start"/> недопустимы до восстановления
    /// через <see cref="Reinitialize"/>.
    /// </summary>
    bool IsDeviceLost { get; }

    /// <summary>
    /// Регистрирует callback уведомления о потере устройства во время воспроизведения.
    ///
    /// <para>Callback вызывается из фонового потока (fill loop / timer).
    /// Реализация не должна блокировать — используй <see cref="Task.Run(Action)"/> при необходимости.</para>
    /// <para>null = отписаться.</para>
    /// </summary>
    /// <param name="callback">Обработчик события потери устройства.</param>
    void SetDeviceLostCallback(Action? callback);

    /// <summary>
    /// Регистрирует callback уведомления о появлении устройства после потери.
    ///
    /// <para>Реализация запускает polling watcher при <see cref="IsDeviceLost"/> = true.
    /// Callback вызывается из timer thread при обнаружении устройства.
    /// Реализация не должна блокировать.</para>
    /// <para>null = остановить watcher.</para>
    /// </summary>
    /// <param name="callback">Обработчик события появления устройства.</param>
    void SetDeviceAvailableCallback(Action? callback);

    /// <summary>
    /// Регистрирует callback уведомления о длительном отсутствии PCM данных.
    ///
    /// <para>Вызывается когда fill loop не получает данных более 1 секунды при открытом gate.
    /// Callback вызывается из fill thread — не должен блокировать.</para>
    /// <para>null = отписаться.</para>
    /// </summary>
    /// <param name="callback">Обработчик события starvation.</param>
    void SetStarvationCallback(Action? callback);

    /// <summary>
    /// Устанавливает volume gain, применяемый непосредственно перед передачей
    /// PCM в аудиоустройство (on-read path). Изменение слышно немедленно
    /// (задержка ≤ один waveOut буфер, ~100ms), минуя provider buffer.
    /// </summary>
    /// <remarks>
    /// <para>Используется для управления громкостью пользователем (slider).
    /// Нормализационный gain (<see cref="AudioPipeline"/> → <see cref="GainCrossfader"/>)
    /// применяется отдельно в AudioCallback при записи в provider.</para>
    /// <para>Вызывается из command thread — реализация должна быть thread-safe.</para>
    /// </remarks>
    /// <param name="gain">Volume gain множитель [0, MaxVolumeGain].</param>
    void SetVolumeGain(float gain);

    //  Diagnostics 

    /// <summary>Громкость (0.0 – 1.0).</summary>
    float Volume { get; set; }

    /// <summary>Воспроизводится ли в данный момент.</summary>
    bool IsPlaying { get; }

    /// <summary>
    /// Количество семплов (samples × channels) во внутреннем буфере,
    /// которые были переданы, но ещё не воспроизведены.
    /// </summary>
    int BufferedSamples { get; }

    int BufferedBytes { get; }

    /// <summary>
    /// Монотонный счётчик сэмплов источника (frames × channels), переданных устройству из <see cref="AudioDataCallback"/>
    /// за всё время жизни бэкенда. Не сбрасывается при Flush/Reinitialize: потребитель привязывается к нему через точку отсчёта.
    /// </summary>
    long RenderedSamples { get; }

    /// <summary>
    /// Частота устройства вывода в Гц, в которой считается <see cref="BufferedSamples"/>.
    /// <c>0</c> — бэкенд не ресемплирует, и потребитель берёт частоту источника.
    /// </summary>
    int DeviceSampleRate => 0;

    /// <summary>
    /// Возвращает согласованный снимок часов рендера без блокировок и аллокаций.
    /// Снимок публикует единственный писатель — поток устройства.
    /// </summary>
    RenderClockSnapshot ReadRenderClock();

    /// <summary>Название бэкенда для диагностики.</summary>
    string Name { get; }
}

/// <summary>
/// Callback для запроса PCM данных от бэкенда.
/// </summary>
/// <param name="buffer">Буфер для заполнения (float32, interleaved).</param>
/// <returns>Количество заполненных фреймов (samples / channels).</returns>
public delegate int AudioDataCallback(Span<float> buffer);