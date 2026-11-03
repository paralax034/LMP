using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    // --- ActiveRangeDownload ---

    /// <summary>
    /// Дескриптор активной HTTP range-загрузки, используемый для дедупликации
    /// в реестре <see cref="_activeDownloads"/>.
    /// <para>
    /// Фоновая загрузка ждёт слот семафора. Критический читатель, присоединившийся к ней,
    /// повышает её приоритет через <see cref="PromoteToCritical"/>, и ожидание слота прекращается сразу.
    /// </para>
    /// </summary>
    private sealed class ActiveRangeDownload
    {
        private readonly TaskCompletionSource? _promotion;

        /// <summary>Начало загружаемого диапазона (байт).</summary>
        public long Start { get; }

        /// <summary>Длина загружаемого диапазона (байт).</summary>
        public int Length { get; }

        /// <summary>Исключительная правая граница диапазона.</summary>
        public long EndExclusive => Start + Length;

        /// <summary>Загрузка запущена критическим читателем и не занимает слот семафора.</summary>
        public bool IsCritical { get; }

        /// <summary>
        /// Сигнал повышения приоритета: завершается, когда к фоновой загрузке присоединяется критический читатель.
        /// Для критической загрузки уже завершён.
        /// </summary>
        public Task PromotionTask => _promotion?.Task ?? Task.CompletedTask;

        /// <summary>
        /// Ленивая задача загрузки. Запускается при первом обращении к <see cref="Lazy{T}.Value"/>.
        /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> гарантирует единственный запуск.
        /// </summary>
        public Lazy<Task<RangeDownloadResult>> LazyTask { get; }

        /// <summary>
        /// Создаёт дескриптор активной загрузки.
        /// </summary>
        /// <param name="start">Начало диапазона.</param>
        /// <param name="length">Длина диапазона.</param>
        /// <param name="isCritical">Приоритет загрузки на момент регистрации.</param>
        /// <param name="lazyTask">Фабрика задачи загрузки.</param>
        public ActiveRangeDownload(long start, int length, bool isCritical, Lazy<Task<RangeDownloadResult>> lazyTask)
        {
            Start = start;
            Length = length;
            IsCritical = isCritical;
            LazyTask = lazyTask;
            if (!isCritical)
                _promotion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Повышает фоновую загрузку до критической: ожидание слота семафора прекращается немедленно.
        /// Повторные вызовы безопасны.
        /// </summary>
        public void PromoteToCritical() => _promotion?.TrySetResult();
    }

    // --- Coordination Methods ---

    /// <summary>
    /// Возвращает количество зарегистрированных активных загрузок.
    /// </summary>
    private int GetActiveDownloadCount()
    {
        lock (_activeDownloadsLock) return _activeDownloads.Count;
    }

    /// <summary>
    /// Строгая защита от пересекающихся (overlapping) загрузок.
    /// Критически важна на слабой сети: исключает двойную загрузку одних и тех же байт.
    /// </summary>
    /// <param name="start">Начало проверяемого диапазона.</param>
    /// <param name="length">Длина проверяемого диапазона.</param>
    /// <param name="active">Первая найденная перекрывающаяся загрузка; <c>null</c> если нет.</param>
    /// <returns><c>true</c> если найдено перекрытие.</returns>
    private bool TryGetOverlappingActiveDownload(long start, int length, out ActiveRangeDownload? active)
    {
        long endExclusive = start + length;
        lock (_activeDownloadsLock)
        {
            foreach (var current in _activeDownloads.Values)
            {
                if (start < current.EndExclusive && endExclusive > current.Start)
                {
                    active = current;
                    return true;
                }
            }
        }
        active = null;
        return false;
    }

    /// <summary>
    /// Регистрирует кандидата в реестре активных загрузок или возвращает
    /// уже существующую перекрывающуюся загрузку (dedup).
    /// </summary>
    /// <param name="candidate">Кандидат на регистрацию.</param>
    /// <returns>
    /// Возвращает <paramref name="candidate"/> если регистрация успешна;
    /// иначе — уже существующую загрузку, перекрывающую тот же диапазон.
    /// </returns>
    private ActiveRangeDownload RegisterOrGetActiveDownload(ActiveRangeDownload candidate)
    {
        long candidateEnd = candidate.EndExclusive;
        lock (_activeDownloadsLock)
        {
            foreach (var current in _activeDownloads.Values)
            {
                if (candidate.Start < current.EndExclusive && candidateEnd > current.Start)
                    return current;
            }

            if (_activeDownloads.TryGetValue(candidate.Start, out var sameStart))
                return sameStart;

            _activeDownloads.Add(candidate.Start, candidate);
            return candidate;
        }
    }

    /// <summary>
    /// Удаляет загрузку из реестра только если текущий caller является её владельцем.
    /// Предотвращает удаление чужой загрузки при race condition.
    /// </summary>
    /// <param name="key">Ключ загрузки (смещение начала диапазона).</param>
    /// <param name="owner">Ожидаемый владелец записи.</param>
    private void RemoveActiveDownloadIfOwner(long key, ActiveRangeDownload owner)
    {
        lock (_activeDownloadsLock)
        {
            if (_activeDownloads.TryGetValue(key, out var current) && ReferenceEquals(current, owner))
                _activeDownloads.Remove(key);
        }
    }

    /// <summary>
    /// Ожидает завершения чужой задачи загрузки, поглощая все исключения кроме
    /// <see cref="ChunkDownloadFatalException"/> и внешней отмены.
    /// </summary>
    /// <param name="task">Задача владельца диапазона.</param>
    /// <param name="ct">Токен отмены caller'а.</param>
    private static async Task WaitForActiveDownloadAsync(Task task, CancellationToken ct)
    {
        try { await task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (ChunkDownloadFatalException) { throw; }
        catch { }
    }

    /// <summary>
    /// Присоединяется к чужой загрузке диапазона. Критический читатель предварительно повышает
    /// её приоритет, чтобы не ждать слот семафора за фоновым preload (priority inversion).
    /// </summary>
    /// <param name="active">Загрузка-владелец перекрывающегося диапазона.</param>
    /// <param name="isCritical">Приоритет присоединяющегося читателя.</param>
    /// <param name="ct">Токен отмены caller'а.</param>
    private static Task JoinActiveDownloadAsync(ActiveRangeDownload active, bool isCritical, CancellationToken ct)
    {
        if (isCritical)
            active.PromoteToCritical();

        return WaitForActiveDownloadAsync(active.LazyTask.Value, ct);
    }

    /// <summary>Исход ожидания слота фоновой загрузки.</summary>
    internal enum SlotWaitOutcome : byte
    {
        /// <summary>Слот получен; вызывающий код обязан вернуть его в семафор.</summary>
        Acquired,

        /// <summary>Приоритет повышен до критического: загрузка идёт без слота.</summary>
        Promoted,

        /// <summary>Слот не освободился за отведённое время.</summary>
        TimedOut,

        /// <summary>Ожидание отменено токеном.</summary>
        Cancelled
    }

    /// <summary>
    /// Ожидает слот семафора либо сигнал повышения приоритета без генерации исключений.
    /// Если приоритет повышен раньше, брошенное ожидание само вернёт слот, когда получит его.
    /// </summary>
    /// <param name="slots">Семафор параллельных фоновых загрузок.</param>
    /// <param name="timeoutMs">Предельное время ожидания слота (мс).</param>
    /// <param name="promotion">Сигнал повышения приоритета.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Исход ожидания.</returns>
    internal static async Task<SlotWaitOutcome> WaitForSlotOrPromotionAsync(
        SemaphoreSlim slots, int timeoutMs, Task promotion, CancellationToken ct)
    {
        if (promotion.IsCompleted)
            return SlotWaitOutcome.Promoted;

        Task<bool> slotTask = slots.WaitAsync(timeoutMs, ct);
        if (!slotTask.IsCompleted)
            _ = await Task.WhenAny(slotTask, promotion).ConfigureAwait(false);

        if (!slotTask.IsCompleted)
        {
            ReturnSlotWhenGranted(slotTask, slots);
            return SlotWaitOutcome.Promoted;
        }

        if (!slotTask.IsCompletedSuccessfully)
        {
            // Наблюдаем исключение отменённого ожидания, чтобы не получить UnobservedTaskException.
            _ = slotTask.Exception;
            return SlotWaitOutcome.Cancelled;
        }

        if (slotTask.Result)
            return SlotWaitOutcome.Acquired;

        return promotion.IsCompleted ? SlotWaitOutcome.Promoted : SlotWaitOutcome.TimedOut;
    }

    /// <summary>
    /// Возвращает слот в семафор, если брошенное ожидание всё же его получило.
    /// </summary>
    /// <param name="slotTask">Брошенное ожидание слота.</param>
    /// <param name="slots">Семафор, которому принадлежит слот.</param>
    private static void ReturnSlotWhenGranted(Task<bool> slotTask, SemaphoreSlim slots)
    {
        _ = slotTask.ContinueWith(
            static (task, state) =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _ = task.Exception;
                    return;
                }

                if (!task.Result)
                    return;

                try { ((SemaphoreSlim)state!).Release(); }
                catch (ObjectDisposedException) { }
            },
            slots,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // --- Helpers ---

    /// <summary>
    /// Определяет, является ли <see cref="HttpRequestException"/> следствием отмены
    /// операции (dispose клиента, epoch cancel, socket abort) а не настоящей сетевой ошибкой.
    /// </summary>
    /// <param name="exception">Перехваченное исключение.</param>
    /// <param name="ct">Токен отмены caller'а.</param>
    /// <param name="disposed">Флаг dispose source'а.</param>
    /// <returns><c>true</c> если исключение является артефактом отмены.</returns>
    private static bool IsCancelledSendFailure(HttpRequestException exception, CancellationToken ct, bool disposed)
    {
        if (disposed || ct.IsCancellationRequested) return true;
        Exception? current = exception;
        while (current != null)
        {
            switch (current)
            {
                case ObjectDisposedException:
                    return true;
                case System.Net.Sockets.SocketException socketEx
                    when socketEx.SocketErrorCode is System.Net.Sockets.SocketError.OperationAborted
                                                  or System.Net.Sockets.SocketError.Interrupted:
                    return true;
            }
            current = current.InnerException;
        }
        return false;
    }
}