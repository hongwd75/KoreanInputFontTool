using System.Buffers.Binary;
using System.Text;

namespace KoreanInputFontTool;

internal sealed class ChatTranslationService : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<int, TranslationSession> sessions = [];
    private TranslationOptions currentOptions = TranslationOptions.Default;
    private bool disposed;

    public event Action<string>? StatusChanged;

    public void Update(TranslationOptions options, IEnumerable<int> processIds)
    {
        lock (gate)
        {
            if (disposed)
                return;

            var requestedIds = processIds.Distinct().ToHashSet();
            if (currentOptions != options)
            {
                DisposeSessions();
                currentOptions = options;
            }

            foreach (var processId in sessions.Keys.Where(id => !requestedIds.Contains(id)).ToArray())
            {
                sessions[processId].Dispose();
                sessions.Remove(processId);
            }

            if (!options.Enabled)
            {
                DisposeSessions();
                return;
            }

            foreach (var processId in requestedIds)
            {
                if (!sessions.ContainsKey(processId))
                {
                    sessions.Add(processId, new TranslationSession(
                        processId,
                        options,
                        message => StatusChanged?.Invoke(message)));
                }
            }
        }
    }

    public void Stop()
    {
        lock (gate)
            DisposeSessions();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            DisposeSessions();
        }
    }

    private void DisposeSessions()
    {
        foreach (var session in sessions.Values)
            session.Dispose();
        sessions.Clear();
    }

    private sealed class TranslationSession : IDisposable
    {
        private const int MaxRecordCharacters = 4096;
        private const int MaxReadBytes = 1024 * 1024;
        private const int MaxRequestsPerPass = 32;
        private const int MaxRememberedRequests = 4096;
        private readonly TranslationOptions options;
        private readonly Action<string> reportStatus;
        private readonly string requestPath;
        private readonly string responsePath;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Queue<string> pending = new();
        private readonly HashSet<string> known = new(StringComparer.Ordinal);
        private readonly Task worker;
        private long requestOffset;
        private bool reportedRunning;

        public TranslationSession(
            int processId,
            TranslationOptions options,
            Action<string> reportStatus)
        {
            this.options = options;
            this.reportStatus = reportStatus;
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KoreanInputFontTool");
            requestPath = Path.Combine(directory, $"translation-requests-v1-{processId}.bin");
            responsePath = Path.Combine(directory, $"translation-responses-v1-{processId}.bin");
            worker = Task.Run(RunAsync);
        }

        public void Dispose()
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        private async Task RunAsync()
        {
            var token = cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    foreach (var line in ReadNewRequests())
                    {
                        if (known.Count >= MaxRememberedRequests)
                            known.Clear();
                        if (known.Add(line))
                            pending.Enqueue(line);
                    }

                    var batch = new List<ParsedChatMessage>(MaxRequestsPerPass);
                    while (batch.Count < MaxRequestsPerPass && pending.TryPeek(out var line))
                    {
                        var fullTranslation = options.Channels.HasFlag(
                            TranslationChannel.FullTranslation);
                        if (!ChatMessageParser.TryParse(line, fullTranslation, out var message) ||
                            !fullTranslation && !options.Channels.HasFlag(message.Channel))
                        {
                            pending.Dequeue();
                            continue;
                        }

                        pending.Dequeue();
                        batch.Add(message);
                    }

                    if (batch.Count > 0)
                    {
                        IReadOnlyList<string> translated;
                        try
                        {
                            translated = await TranslationClient.TranslateBatchAsync(
                                batch.Select(message => message.Message).ToArray(),
                                options,
                                token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // Empty responses clear native pending entries so a
                            // failed batch cannot be retried forever.
                            foreach (var message in batch)
                                WriteResponse(message.OriginalLine, string.Empty);
                            reportStatus($"번역 오류: {ex.Message}");
                            await Task.Delay(2_000, token).ConfigureAwait(false);
                            continue;
                        }

                        for (var index = 0; index < batch.Count; index++)
                            WriteResponse(batch[index].OriginalLine, translated[index]);

                        if (!reportedRunning)
                        {
                            reportedRunning = true;
                            var fullTranslation = options.Channels.HasFlag(
                                TranslationChannel.FullTranslation);
                            reportStatus(fullTranslation
                                ? "게임 전체 번역 실행 중"
                                : "게임 채팅 자동 번역 실행 중");
                        }
                    }

                    await Task.Delay(250, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    reportStatus($"번역 오류: {ex.Message}");
                    try
                    {
                        await Task.Delay(2_000, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }

        private IReadOnlyList<string> ReadNewRequests()
        {
            if (!File.Exists(requestPath))
                return [];

            using var stream = new FileStream(
                requestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < requestOffset)
                requestOffset = 0;
            if (stream.Length == requestOffset)
                return [];

            stream.Position = requestOffset;
            var requestedLength = (int)Math.Min(stream.Length - requestOffset, MaxReadBytes);
            var bytes = new byte[requestedLength];
            var read = stream.Read(bytes, 0, bytes.Length);
            var result = new List<string>();
            var cursor = 0;
            while (read - cursor >= sizeof(uint) * 2)
            {
                var keyCharacters = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, sizeof(uint)));
                var valueCharacters = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + sizeof(uint), sizeof(uint)));
                if (keyCharacters > MaxRecordCharacters || valueCharacters > MaxRecordCharacters)
                {
                    requestOffset = stream.Length;
                    return result;
                }

                var recordBytes = checked(sizeof(uint) * 2 + (int)(keyCharacters + valueCharacters) * sizeof(char));
                if (read - cursor < recordBytes)
                    break;

                var keyOffset = cursor + sizeof(uint) * 2;
                result.Add(Encoding.Unicode.GetString(bytes, keyOffset, (int)keyCharacters * sizeof(char)));
                cursor += recordBytes;
            }

            requestOffset += cursor;
            return result;
        }

        private void WriteResponse(string originalLine, string translated)
        {
            var keyBytes = Encoding.Unicode.GetBytes(originalLine);
            var valueBytes = Encoding.Unicode.GetBytes(translated);
            if (keyBytes.Length / sizeof(char) > MaxRecordCharacters ||
                valueBytes.Length / sizeof(char) > MaxRecordCharacters)
            {
                return;
            }

            var record = new byte[sizeof(uint) * 2 + keyBytes.Length + valueBytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(
                record.AsSpan(0, sizeof(uint)),
                (uint)(keyBytes.Length / sizeof(char)));
            BinaryPrimitives.WriteUInt32LittleEndian(
                record.AsSpan(sizeof(uint), sizeof(uint)),
                (uint)(valueBytes.Length / sizeof(char)));
            keyBytes.CopyTo(record, sizeof(uint) * 2);
            valueBytes.CopyTo(record, sizeof(uint) * 2 + keyBytes.Length);

            Directory.CreateDirectory(Path.GetDirectoryName(responsePath)!);
            using var stream = new FileStream(
                responsePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Write(record, 0, record.Length);
            stream.Flush(flushToDisk: false);
        }
    }
}
