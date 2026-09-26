namespace EchoRelay.Core.Game
{
    /// <summary>
    /// Echo VR's 64-bit symbol hash (CSymbol64). Used for message type identifiers, level names, game types, item names, etc.
    /// </summary>
    public static class Symbol
    {
        private const ulong POLYNOMIAL = 0x95AC9329AC4BC9B5;
        private static readonly ulong[] _table = BuildTable();

        private static ulong[] BuildTable()
        {
            ulong[] table = new ulong[256];
            for (int i = 0; i < 256; i++)
            {
                ulong crc = 0;
                for (int bit = 7; bit >= 0; bit--)
                {
                    crc <<= 1;
                    if (((i >> bit) & 1) != 0)
                        crc ^= POLYNOMIAL;
                }
                table[i] = crc << 1;
            }
            return table;
        }

        /// <summary>
        /// Computes the symbol for a given name (case-insensitive).
        /// </summary>
        /// <param name="name">The name to hash.</param>
        /// <returns>The 64-bit symbol, as a signed integer (the representation used throughout EchoRelay).</returns>
        public static long Hash(string name)
        {
            ulong hash = 0xFFFFFFFFFFFFFFFF;
            foreach (char c in name.ToLowerInvariant())
                hash = (ulong)(byte)c ^ _table[hash >> 56] ^ (hash << 8);
            return unchecked((long)hash);
        }
    }
}
