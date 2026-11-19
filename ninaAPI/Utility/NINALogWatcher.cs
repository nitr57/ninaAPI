using NINA.Core.Utility;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ninaAPI
{

    /// <summary>
    /// Follows the log NINA writes in this process and hands every new line to the processor. NINA rolls its log every
    /// month and at the size limit, and pins renames this session's logs after a clock correction, so the log is not
    /// told by its name: it is the .log file this process holds open for writing (/proc/self/fd), and the reader keeps
    /// its file open across a rename. When NINA writes another file, the reader reads its file to the end and follows
    /// the new one from its start. Polled every 2 s: a watch on the log folder would wake on the processor's own log
    /// lines.
    /// </summary>
    public class NINALogWatcher
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        private readonly NINALogMessageProcessor processor;
        private readonly string logDirectory;
        private readonly object runLock = new object();
        private CancellationTokenSource run;
        private Thread watcherThread;

        public NINALogWatcher(NINALogMessageProcessor processor)
        {
            this.processor = processor;
            this.logDirectory = LogUtils.GetLogDirectory();
        }

        public void Start()
        {
            lock (runLock)
            {
                StopLocked();

                var cancel = new CancellationTokenSource();
                run = cancel;
                watcherThread = new Thread(() => Run(cancel.Token))
                {
                    Name = "Log watcher thread",
                    IsBackground = true,
                };
                watcherThread.Start();
            }
        }

        public void Stop()
        {
            lock (runLock)
            {
                StopLocked();
            }
        }

        private void StopLocked()
        {
            if (run == null)
            {
                return;
            }

            Logger.Debug("stopping log watcher");
            run.Cancel();

            if (watcherThread != null && watcherThread != Thread.CurrentThread)
            {
                watcherThread.Join(TimeSpan.FromSeconds(5));
            }

            run = null;
            watcherThread = null;
        }

        private void Run(CancellationToken token)
        {
            using var follower = new LogFollower(logDirectory, processor);
            string lastError = null;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    follower.Poll(token);
                    lastError = null;
                }
                catch (Exception e)
                {
                    // the next poll tries again; a lasting failure is logged once
                    if (e.Message != lastError)
                    {
                        Logger.Warning($"failed to process log messages: {e.Message}");
                        lastError = e.Message;
                    }
                }

                token.WaitHandle.WaitOne(PollInterval);
            }

            Logger.Debug("log watcher has been stopped");
        }

        /// <summary>The reader of one run, used by its thread only.</summary>
        private sealed class LogFollower : IDisposable
        {
            private const string OpenFileDescriptors = "/proc/self/fd";
            private const string OpenFileInfo = "/proc/self/fdinfo";
            private const int ChunkSize = 64 * 1024;

            // about 30 s of polls without a log of this process before that is reported
            private const int PollsBeforeNoLogWarning = 15;

            private readonly string logDirectory;
            private readonly NINALogMessageProcessor processor;
            private readonly byte[] chunk = new byte[ChunkSize];
            private FileStream reader;
            private byte[] partialLine = Array.Empty<byte>();
            private string rollSeen;
            private int pollsWithoutLog;

            public LogFollower(string logDirectory, NINALogMessageProcessor processor)
            {
                this.logDirectory = logDirectory;
                this.processor = processor;
            }

            public void Poll(CancellationToken token)
            {
                // the open file first: a failing lookup below must not keep it from being read
                if (reader != null)
                {
                    ReadAppended(token);
                }

                string written = WrittenLog();

                if (reader == null)
                {
                    if (written == null)
                    {
                        // not there yet: tried again at the next poll
                        pollsWithoutLog++;
                        if (pollsWithoutLog == PollsBeforeNoLogWarning)
                        {
                            Logger.Warning(
                                $"failed to find the NINA log file of this process in {logDirectory}, no log events until it is found");
                        }

                        return;
                    }

                    // read from its start, as the watcher always did: it starts with the plugin, early in the session
                    Open(written);
                    pollsWithoutLog = 0;
                    Logger.Info($"watching log file: {written}");
                    ReadAppended(token);
                }

                // Another file than the reader's: NINA started a new log, or pins renamed it between the two lookups;
                // only the same answer on two polls in a row counts as a new log. Without /proc no new log is seen.
                string readerName = ReaderName();
                if (written == null || readerName == null || Path.GetFileName(written) == readerName)
                {
                    rollSeen = null;
                    return;
                }

                if (rollSeen != written)
                {
                    rollSeen = written;
                    return;
                }

                // NINA closed the old log before it opened the new one: what was not read yet is complete
                ReadAppended(token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Close();
                Open(written);
                rollSeen = null;
                Logger.Info($"watching log file: {written}");
                ReadAppended(token);
            }

            public void Dispose()
            {
                Close();
            }

            private void Open(string path)
            {
                reader = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1);
                partialLine = Array.Empty<byte>();
            }

            private void Close()
            {
                reader?.Dispose();
                reader = null;
                partialLine = Array.Empty<byte>();
            }

            // Reads what was appended since the last read, in chunks, and hands over the complete lines of each. Only up
            // to the length the file had when the read began: what the processor logs meanwhile (a Trace line per batch)
            // waits for the next poll, or the read would feed itself.
            private void ReadAppended(CancellationToken token)
            {
                long end = reader.Length;

                while (reader.Position < end && !token.IsCancellationRequested)
                {
                    int read = reader.Read(chunk, 0, (int)Math.Min(chunk.Length, end - reader.Position));
                    if (read <= 0)
                    {
                        break;
                    }

                    byte[] data = new byte[partialLine.Length + read];
                    partialLine.CopyTo(data, 0);
                    Array.Copy(chunk, 0, data, partialLine.Length, read);

                    int lastNewline = Array.LastIndexOf(data, (byte)'\n');
                    if (lastNewline < 0)
                    {
                        // no complete line yet; kept for the next read
                        partialLine = data;
                        continue;
                    }

                    partialLine = data[(lastNewline + 1)..];
                    var lines = Encoding.UTF8.GetString(data, 0, lastNewline)
                        .Split('\n')
                        .Select(line => line.TrimEnd('\r'))
                        .ToList();
                    processor.processLogMessages(lines);
                }
            }

            // The name the reader's file has now (it follows a rename); null without /proc.
            private string ReaderName()
            {
                try
                {
                    int fd = reader.SafeFileHandle.DangerousGetHandle().ToInt32();
                    string descriptor = Path.Combine(OpenFileDescriptors, fd.ToString(CultureInfo.InvariantCulture));
                    string target = new FileInfo(descriptor).LinkTarget;
                    return target == null ? null : Path.GetFileName(target);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            /// <summary>
            /// The .log file in the log folder this process holds open for writing: NINA's log, whatever its name. Without
            /// /proc, the newest log with this process' version and process id in its name.
            /// </summary>
            private string WrittenLog()
            {
                if (!Directory.Exists(OpenFileDescriptors))
                {
                    return NewestLogByName();
                }

                string newest = null;
                var newestTime = DateTime.MinValue;

                foreach (string descriptor in Directory.EnumerateFileSystemEntries(OpenFileDescriptors))
                {
                    string target;
                    try
                    {
                        target = new FileInfo(descriptor).LinkTarget;
                    }
                    catch (Exception)
                    {
                        // descriptors come and go while enumerating
                        continue;
                    }

                    if (target == null
                        || !target.EndsWith(".log", StringComparison.Ordinal)
                        || !OpenForWriting(Path.GetFileName(descriptor)))
                    {
                        continue;
                    }

                    // link targets are fully resolved (a symlinked ~/.local would never match the folder): by name
                    string candidate = Path.Combine(logDirectory, Path.GetFileName(target));
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    var writtenAt = File.GetLastWriteTimeUtc(candidate);
                    if (newest == null || writtenAt > newestTime)
                    {
                        newest = candidate;
                        newestTime = writtenAt;
                    }
                }

                return newest;
            }

            // The access mode in the descriptor's flags (octal): O_WRONLY or O_RDWR, not a reader of an old log
            private static bool OpenForWriting(string descriptor)
            {
                try
                {
                    foreach (string line in File.ReadLines(Path.Combine(OpenFileInfo, descriptor)))
                    {
                        if (line.StartsWith("flags:", StringComparison.Ordinal))
                        {
                            long flags = Convert.ToInt64(line.Substring("flags:".Length).Trim(), 8);
                            return (flags & 3) != 0;
                        }
                    }
                }
                catch (Exception)
                {
                    // gone meanwhile
                }

                return false;
            }

            private string NewestLogByName()
            {
                string marker = $"-{CoreUtil.Version}.{Environment.ProcessId}-";

                return Directory.GetFiles(logDirectory, "*.log")
                    .Where(file => Path.GetFileName(file).Contains(marker, StringComparison.Ordinal))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
        }
    }

    public class LogUtils
    {

        public static string GetLogDirectory()
        {
            return Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Logs");
        }

        private LogUtils()
        {
        }
    }
}
