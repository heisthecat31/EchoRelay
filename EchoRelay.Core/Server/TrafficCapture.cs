using System.Collections.Concurrent;

namespace EchoRelay.Core.Server
{
    /// <summary>
    /// Appends messages the server could not decode or has no implementation for (and unhandled HTTP requests, failed sends)
    /// to traffic_capture.log in the working directory, with full payloads, so they can be reverse engineered. Every other
    /// message is only captured with <see cref="CaptureAll"/>.
    /// Lines are queued and written by one background thread, so the server's network threads never wait on the disk; the
    /// file is kept under <see cref="MaxFileSize"/> (the previous one is kept as traffic_capture.log.old).
    /// </summary>
    public static class TrafficCapture
    {
        /// <summary>
        /// The path of the capture file.
        /// </summary>
        public static string FilePath { get; set; } = Path.Join(Environment.CurrentDirectory, "traffic_capture.log");

        /// <summary>
        /// Indicates whether capturing is enabled.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// Also capture every decoded message (see <see cref="LogAll"/>). Off by default: with many players that's a lot of disk.
        /// </summary>
        public static bool CaptureAll { get; set; } = false;

        /// <summary>
        /// The size at which the file is rotated.
        /// </summary>
        public const long MaxFileSize = 20 * 1024 * 1024;

        /// <summary>
        /// Lines beyond this many waiting are dropped (the disk can't keep up).
        /// </summary>
        private const int MaxQueued = 10000;

        private static readonly BlockingCollection<string> _queue = new BlockingCollection<string>(new ConcurrentQueue<string>());
        private static readonly Lazy<Thread> _writer = new Lazy<Thread>(() =>
        {
            Thread thread = new Thread(WriteLoop) { IsBackground = true, Name = "TrafficCapture" };
            thread.Start();
            return thread;
        });

        /// <summary>
        /// Queues a line for the capture file. Never waits.
        /// </summary>
        /// <param name="line">The line to append.</param>
        public static void Log(string line)
        {
            if (!Enabled || _queue.Count >= MaxQueued)
                return;
            _ = _writer.Value;
            _queue.Add($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
        }

        /// <summary>
        /// Queues a line only when <see cref="CaptureAll"/> is on (routine traffic).
        /// </summary>
        public static void LogAll(string line)
        {
            if (CaptureAll)
                Log(line);
        }

        private static void WriteLoop()
        {
            StreamWriter? writer = null;
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (writer == null || writer.BaseStream.Length > MaxFileSize)
                    {
                        writer?.Dispose();
                        if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxFileSize)
                            File.Move(FilePath, FilePath + ".old", true);
                        writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
                    }
                    writer.WriteLine(line);
                    if (_queue.Count == 0)
                        writer.Flush();
                }
                catch
                {
                    // Capturing must never break the server.
                    try { writer?.Dispose(); } catch { }
                    writer = null;
                }
            }
        }
    }
}
