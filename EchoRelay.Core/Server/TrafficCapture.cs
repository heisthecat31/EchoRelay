namespace EchoRelay.Core.Server
{
    /// <summary>
    /// Appends messages the server could not decode or has no implementation for (and unhandled HTTP requests) to
    /// traffic_capture.log in the working directory, with full payloads, so they can be reverse engineered.
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

        private static readonly object _lock = new object();

        /// <summary>
        /// Appends a line to the capture file.
        /// </summary>
        /// <param name="line">The line to append.</param>
        public static void Log(string line)
        {
            if (!Enabled)
                return;
            try
            {
                lock (_lock)
                    File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
            catch
            {
                // Capturing must never break the server.
            }
        }
    }
}
