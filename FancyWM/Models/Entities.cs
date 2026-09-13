using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FancyWM.Models
{
    public interface IObservableFileEntity<T> : IObservable<T>
    {
        [JsonIgnore]
        string FullPath { get; }

        IObservable<T> Value { get; }

        Task SaveAsync(Func<T, T> update);

        // Complete only after all updates accepted before this call are durable.
        Task FlushAsync() => Task.CompletedTask;
    }

    public abstract class ObservableFileEntityBase<T> : IObservableFileEntity<T>
    {
        [JsonIgnore]
        public string FullPath { get; }

        public IObservable<T> Value
        {
            get
            {
                _ = m_initTask.Value;
                return m_saves;
            }
        }

        private readonly ReplaySubject<T> m_saves = new(1);
        private readonly object m_stateLock = new();
        private readonly TimeProvider m_timeProvider;
        private readonly Lazy<Task> m_initTask;
        private T m_currentValue = default!;
        private T m_persistedValue = default!;
        private TaskCompletionSource? m_pendingSave;
        private TaskCompletionSource? m_activeSave;
        private CancellationTokenSource? m_saveDelay;
        private bool m_writerRunning;
        private bool m_flushRequested;
        private bool m_notifying;
        private long m_revision;
        private long m_notifiedRevision;

        protected ObservableFileEntityBase(string fullPath, Func<T> defaultFactory)
            : this(fullPath, defaultFactory, TimeProvider.System)
        {
        }

        protected ObservableFileEntityBase(string fullPath, Func<T> defaultFactory, TimeProvider timeProvider)
        {
            FullPath = fullPath;
            m_timeProvider = timeProvider;
            // Start virtual read/write calls after the derived constructor has finished.
            m_initTask = new Lazy<Task>(() =>
            {
                var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = initialized.Task;
                // Value subscribers receive OnError; also observe the initialization Task
                // when no caller ever invokes SaveAsync.
                _ = task.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                _ = InitializeAsync(initialized);
                return task;
            });

            async Task InitializeAsync(TaskCompletionSource initialized)
            {
                try
                {
                    T value;
                    bool writeDefault = !File.Exists(FullPath);
                    try
                    {
                        if (writeDefault)
                        {
                            value = defaultFactory();
                        }
                        else
                        {
                            using var stream = File.OpenRead(FullPath);
                            value = await ReadAsync(stream).ConfigureAwait(false);
                        }
                    }
                    catch (Exception e) when (IsFileOrFormatException(e))
                    {
                        value = defaultFactory();
                        writeDefault = true;
                        if (File.Exists(FullPath))
                        {
                            File.Copy(FullPath, $"{FullPath}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak");
                        }
                    }

                    if (writeDefault)
                    {
                        await WriteValueAsync(value).ConfigureAwait(false);
                    }
                    lock (m_stateLock)
                    {
                        m_currentValue = m_persistedValue = value;
                        m_revision++;
                    }
                    // Publish the initial value before releasing callers already
                    // waiting for the read. Reentrant observer saves use the ready
                    // in-memory value directly and can synchronously flush it.
                    PublishLatest();
                    initialized.TrySetResult();
                }
                catch (Exception e)
                {
                    initialized.TrySetException(e);
                    m_saves.OnError(e);
                }
            }
        }

        private static bool IsFileOrFormatException(Exception? e) => e != null &&
            (e is FileNotFoundException || e is JsonException || e is FormatException || IsFileOrFormatException(e.InnerException));

        public IDisposable Subscribe(IObserver<T> observer)
        {
            return Value.Subscribe(observer);
        }

        public virtual Task SaveAsync(Func<T, T> update)
        {
            ArgumentNullException.ThrowIfNull(update);
            bool valueReady;
            lock (m_stateLock) valueReady = m_revision != 0;
            if (valueReady) return SaveInitialized(update);
            var initialization = m_initTask.Value;
            return initialization.IsCompletedSuccessfully
                ? SaveInitialized(update)
                : SaveAfterInitializationAsync(initialization, update);
        }

        private async Task SaveAfterInitializationAsync(Task initialization, Func<T, T> update)
        {
            await initialization.ConfigureAwait(false);
            await SaveInitialized(update).ConfigureAwait(false);
        }

        private Task SaveInitialized(Func<T, T> update)
        {
            try
            {
                Task completion;
                bool startWriter;
                lock (m_stateLock)
                {
                    var newValue = update(m_currentValue);
                    if (!Equals(m_currentValue, newValue))
                    {
                        m_currentValue = newValue;
                        m_revision++;
                    }
                    completion = QueueSaveCore(out startWriter);
                }
                if (startWriter) _ = WritePendingAsync();
                PublishLatest();
                // All callers in this batch share its completion. Do not retain one
                // async state machine (and updater closure) per slider notification.
                return completion;
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }

        public Task FlushAsync()
        {
            Task completion;
            bool startWriter;
            lock (m_stateLock)
            {
                // An unopened/initializing entity has no accepted updates to flush.
                if (m_revision == 0) return Task.CompletedTask;
                completion = QueueSaveCore(out startWriter);
                m_flushRequested = m_writerRunning;
                m_saveDelay?.Cancel();
            }
            if (startWriter) _ = WritePendingAsync();
            return completion;
        }

        private Task QueueSaveCore(out bool startWriter)
        {
            startWriter = false;
            if (m_pendingSave == null && m_activeSave == null && Equals(m_currentValue, m_persistedValue))
            {
                return Task.CompletedTask;
            }
            // A matching in-flight value already covers an identical save/flush.
            if (m_pendingSave == null && m_activeSave != null && m_activeRevision == m_revision)
            {
                return m_activeSave.Task;
            }
            m_pendingSave ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!m_writerRunning)
            {
                m_writerRunning = startWriter = true;
                m_saveDelay = new CancellationTokenSource();
            }
            return m_pendingSave.Task;
        }

        private long m_activeRevision;

        private void PublishLatest()
        {
            lock (m_stateLock)
            {
                if (m_notifying) return;
                m_notifying = true;
            }
            try
            {
                while (true)
                {
                    T value;
                    lock (m_stateLock)
                    {
                        if (m_notifiedRevision == m_revision)
                        {
                            m_notifying = false;
                            return;
                        }
                        value = m_currentValue;
                        m_notifiedRevision = m_revision;
                    }
                    // Never call arbitrary observers under the persistence state lock.
                    // Reentrant/concurrent updates retain only the latest pending value.
                    m_saves.OnNext(value);
                }
            }
            catch
            {
                lock (m_stateLock) m_notifying = false;
                throw;
            }
        }

        private async Task WritePendingAsync()
        {
            while (true)
            {
                CancellationTokenSource delay;
                lock (m_stateLock) delay = m_saveDelay!;
                Exception? failure = null;
                try
                {
                    // Fixed window from first admission: continuous edits cannot postpone it.
                    await Task.Delay(TimeSpan.FromMilliseconds(100), m_timeProvider, delay.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
                catch (Exception e) { failure = e; }

                T value;
                TaskCompletionSource completion;
                lock (m_stateLock)
                {
                    m_saveDelay = null;
                    delay.Dispose();
                    value = m_currentValue;
                    m_activeRevision = m_revision;
                    completion = m_activeSave = m_pendingSave!;
                    m_pendingSave = null;
                }
                try
                {
                    if (failure == null) await WriteValueAsync(value).ConfigureAwait(false);
                }
                catch (Exception e) { failure = e; }
                lock (m_stateLock)
                {
                    if (failure == null) m_persistedValue = value;
                    m_activeSave = null;
                    if (failure == null) completion.TrySetResult();
                    else completion.TrySetException(failure);
                    if (m_pendingSave == null)
                    {
                        m_writerRunning = m_flushRequested = false;
                        return;
                    }
                    m_saveDelay = new CancellationTokenSource();
                    if (m_flushRequested) m_saveDelay.Cancel();
                }
            }
        }

        protected abstract Task<T> ReadAsync(Stream stream);
        protected abstract Task WriteAsync(Stream stream, T value);

        private async Task WriteValueAsync(T newValue)
        {
            var tempPath = FullPath + ".tmp";
            try
            {
                using (var stream = File.Create(tempPath))
                {
                    await WriteAsync(stream, newValue).ConfigureAwait(false);
                }

                if (File.Exists(FullPath))
                {
                    var backupPath = FullPath + ".bak";
                    File.Replace(tempPath, FullPath, backupPath);
                    File.Delete(backupPath);
                }
                else
                {
                    File.Move(tempPath, FullPath);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException($"Permission denied writing to {FullPath}.", ex);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"I/O error writing to {FullPath}.", ex);
            }
            finally
            {
                // A failed serialization never replaces the last good file.
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
    }

    public class ObservableJsonEntity<T>(string fullPath, Func<T> defaultFactory, JsonSerializerOptions? options = null) : ObservableFileEntityBase<T>(fullPath, defaultFactory)
    {
        public JsonSerializerOptions Options { get; } = options ?? new JsonSerializerOptions();

        protected override async Task<T> ReadAsync(Stream stream)
        {
            var result = await JsonSerializer.DeserializeAsync<T>(stream, Options).ConfigureAwait(false) ?? throw new JsonException("Deserialized value is null.");
            return result;
        }

        protected override async Task WriteAsync(Stream stream, T value)
        {
            await JsonSerializer.SerializeAsync(stream, value, Options).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
    }

    public class ObservableJsonEntityWithCommentPreservation<T>(string fullPath, Func<T> defaultFactory, JsonSerializerOptions? options = null) : ObservableFileEntityBase<T>(fullPath, defaultFactory)
    {
        public JsonSerializerOptions Options { get; } = options ?? new JsonSerializerOptions();

        private readonly JsonSerializerOptions m_readOptions = new(options ?? new JsonSerializerOptions())
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        protected override async Task<T> ReadAsync(Stream stream)
        {
            var result = await JsonSerializer.DeserializeAsync<T>(stream, m_readOptions).ConfigureAwait(false) ?? throw new JsonException("Deserialized value is null.");
            return result;
        }

        protected override async Task WriteAsync(Stream stream, T value)
        {
            using var newValueStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(newValueStream, value, Options).ConfigureAwait(false);
            newValueStream.Position = 0;

            try
            {
                using var oldValueStream = File.OpenRead(FullPath);
                await MergeJsonPreservingComments(stream, newValueStream, oldValueStream).ConfigureAwait(false);
            }
            catch (Exception)
            {
                stream.Position = 0;
                stream.SetLength(0);
                newValueStream.Position = 0;
                await newValueStream.CopyToAsync(stream).ConfigureAwait(false);
            }

            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadAllAsync(Stream stream)
        {
            byte[] b = new byte[stream.Length - stream.Position];
            await stream.ReadAsync(b).ConfigureAwait(false);
            return b;
        }

        private async Task MergeJsonPreservingComments(Stream outputStream, Stream newValueStream, Stream oldValueStream)
        {
            var newValueBytes = await ReadAllAsync(newValueStream).ConfigureAwait(false);
            var oldValueBytes = await ReadAllAsync(oldValueStream).ConfigureAwait(false);

            var readerOptions = new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true };
            var writerOptions = new JsonWriterOptions { Indented = Options.WriteIndented, Encoder = Options.Encoder };

            using var writer = new Utf8JsonWriter(outputStream, writerOptions);
            void Merge()
            {
                var newReader = new Utf8JsonReader(newValueBytes, readerOptions);
                var oldReader = new Utf8JsonReader(oldValueBytes, readerOptions);
                newReader.Read();
                oldReader.Read();
                MergeWithComments(writer, ref newReader, ref oldReader);
            }
            Merge();
        }

        private void MergeWithComments(Utf8JsonWriter writer, ref Utf8JsonReader newReader, ref Utf8JsonReader oldReader)
        {
            CopyCommentsAndReadMore(writer, ref oldReader);
            CopyCommentsAndReadMore(writer, ref newReader);

            switch (newReader.TokenType)
            {
                case JsonTokenType.Comment:
                    throw new InvalidProgramException("Unexpected Comment");
                case JsonTokenType.None:
                    throw new InvalidProgramException("Unexpected None");
                case JsonTokenType.StartObject:
                    if (oldReader.TokenType == JsonTokenType.StartObject)
                    {
                        MergeObjectWithComments(writer, ref newReader, ref oldReader);
                    }
                    else
                    {
                        oldReader.Skip();
                        CopyValue(writer, ref newReader);
                    }
                    break;
                case JsonTokenType.EndObject:
                    throw new InvalidProgramException("Expected StartObject to eat EndObject");
                case JsonTokenType.StartArray:
                    oldReader.Skip();
                    CopyValue(writer, ref newReader);
                    Debug.Assert(newReader.TokenType == JsonTokenType.EndArray);
                    break;
                case JsonTokenType.EndArray:
                    throw new InvalidProgramException("Expected StartArray to eat EndArray");
                case JsonTokenType.PropertyName:
                    throw new InvalidProgramException("Expected StartObject to eat PropertyName");
                case JsonTokenType.String:
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    oldReader.Skip();
                    CopyToken(writer, ref newReader);
                    break;
            }
        }

        private void CopyToken(Utf8JsonWriter writer, ref Utf8JsonReader newReader)
        {
            switch (newReader.TokenType)
            {
                case JsonTokenType.None:
                    break;
                case JsonTokenType.StartObject:
                    writer.WriteStartObject();
                    break;
                case JsonTokenType.EndObject:
                    writer.WriteEndObject();
                    break;
                case JsonTokenType.StartArray:
                    writer.WriteStartArray();
                    break;
                case JsonTokenType.EndArray:
                    writer.WriteEndArray();
                    break;
                case JsonTokenType.PropertyName:
                    writer.WritePropertyName(newReader.ValueSpan);
                    break;
                case JsonTokenType.Comment:
                    writer.WriteCommentValue(newReader.ValueSpan);
                    break;
                case JsonTokenType.String:
                    writer.WriteStringValue(newReader.ValueSpan);
                    break;
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    writer.WriteRawValue(newReader.ValueSpan);
                    break;
            }
        }

        private void CopyGuts(Utf8JsonWriter writer, ref Utf8JsonReader reader)
        {
            int depth = reader.CurrentDepth;
            while (reader.Read() && (reader.TokenType == JsonTokenType.Comment || reader.CurrentDepth > depth))
            {
                CopyToken(writer, ref reader);
            }
        }

        private void CopyValue(Utf8JsonWriter writer, ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartArray:
                case JsonTokenType.StartObject:
                    CopyToken(writer, ref reader);
                    CopyGuts(writer, ref reader);
                    CopyToken(writer, ref reader);
                    break;
                default:
                    CopyToken(writer, ref reader);
                    break;
            }
        }

        private void CopyCommentsAndReadMore(Utf8JsonWriter writer, ref Utf8JsonReader reader)
        {
            while (reader.TokenType == JsonTokenType.Comment)
            {
                writer.WriteCommentValue(reader.ValueSpan);
                reader.Read();
            }
        }

        private HashSet<string> GetProperties(Utf8JsonReader reader)
        {
            HashSet<string> properties = new();
            int depth = reader.CurrentDepth + 1;
            while (reader.Read())
            {
                if (depth == reader.CurrentDepth && reader.TokenType == JsonTokenType.PropertyName)
                {
                    properties.Add(reader.GetString()!);
                }
            }
            return properties;
        }

        private void Advance(ref Utf8JsonReader reader, string propertyName)
        {
            int depth = reader.CurrentDepth + 1;
            while (reader.Read())
            {
                if (depth == reader.CurrentDepth && reader.TokenType == JsonTokenType.PropertyName && reader.GetString() == propertyName)
                {
                    return;
                }
            }
        }

        private void MergeObjectWithComments(Utf8JsonWriter writer, ref Utf8JsonReader newReader, ref Utf8JsonReader oldReader)
        {
            Debug.Assert(newReader.TokenType == JsonTokenType.StartObject);
            Debug.Assert(oldReader.TokenType == JsonTokenType.StartObject);

            var newReaderKeys = GetProperties(newReader);
            var oldReaderKeys = new HashSet<string>();

            writer.WriteStartObject();

            while (oldReader.Read())
            {
                CopyCommentsAndReadMore(writer, ref oldReader);

                if (oldReader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (oldReader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new InvalidProgramException("Expected PropertyName");
                }

                string propertyName = oldReader.GetString()!;
                oldReaderKeys.Add(propertyName);
                writer.WritePropertyName(oldReader.ValueSpan);

                if (newReaderKeys.Contains(propertyName))
                {
                    var newReaderCopy = newReader;
                    Advance(ref newReaderCopy, propertyName);
                    newReaderCopy.Read();
                    oldReader.Read();
                    MergeWithComments(writer, ref newReaderCopy, ref oldReader);
                    if (oldReader.TokenType == JsonTokenType.EndObject)
                    {
                        break;
                    }
                    continue;
                }

                oldReader.Read();
                CopyValue(writer, ref oldReader);
            }

            var keysToCopy = new HashSet<string>(newReaderKeys.Where(x => !oldReaderKeys.Contains(x)));
            if (keysToCopy.Count > 0)
            {
                int depth = newReader.CurrentDepth;
                while (newReader.Read())
                {
                    if (depth < newReader.CurrentDepth && newReader.TokenType != JsonTokenType.PropertyName)
                    {
                        continue;
                    }

                    if (newReader.TokenType == JsonTokenType.EndObject)
                    {
                        break;
                    }

                    string propertyName = newReader.GetString()!;
                    if (keysToCopy.Contains(propertyName))
                    {
                        writer.WritePropertyName(propertyName);
                        newReader.Read();
                        CopyValue(writer, ref newReader);
                    }
                }
            }

            writer.WriteEndObject();
        }
    }
}
