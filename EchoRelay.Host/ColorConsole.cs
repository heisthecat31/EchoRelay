using System.Text;

namespace EchoRelay.Host
{
    /// <summary>
    /// Colours console lines alternately blue and white, so each question and message stands apart from the next.
    /// Blank lines keep no colour. Installed over Console.Out; ReadLine must be followed by <see cref="LineEnded"/>, since
    /// the Enter a person types never passes through here.
    /// </summary>
    internal class ColorConsole : TextWriter
    {
        private static readonly ConsoleColor[] Colors = { ConsoleColor.Cyan, ConsoleColor.White };
        private static ColorConsole? _instance;

        private readonly TextWriter _inner;
        private readonly object _lock = new object();
        private bool _atLineStart = true;
        private int _next;

        private ColorConsole(TextWriter inner) => _inner = inner;

        public override Encoding Encoding => _inner.Encoding;

        /// <summary>Starts colouring lines (does nothing when the output isn't a console).</summary>
        public static void Install()
        {
            if (_instance != null || Console.IsOutputRedirected)
                return;
            _instance = new ColorConsole(Console.Out);
            Console.SetOut(_instance);
        }

        /// <summary>A line ended outside this writer (the Enter after typing an answer).</summary>
        public static void LineEnded()
        {
            if (_instance != null)
                lock (_instance._lock)
                    _instance._atLineStart = true;
        }

        public override void Write(char value)
        {
            lock (_lock)
            {
                if (_atLineStart && value != '\r' && value != '\n')
                {
                    _inner.Flush();
                    Console.ForegroundColor = Colors[_next];
                    _next = (_next + 1) % Colors.Length;
                    _atLineStart = false;
                }
                _inner.Write(value);
                if (value == '\n')
                    _atLineStart = true;
            }
        }

        public override void Write(string? value)
        {
            if (value == null)
                return;
            lock (_lock)
                foreach (char c in value)
                    Write(c);
        }

        public override void Flush() => _inner.Flush();
    }
}
