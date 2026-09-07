using System;
using System.Text;

namespace AS400Automation.Protocol
{
    /// <summary>
    /// Bidirectional Code Page 037 (IBM037 EBCDIC / US-Canada) codec with zero external dependencies.
    /// Provides constant-time O(1) table lookups guaranteeing reliability across all .NET runtime profiles.
    /// </summary>
    public static class EbcdicCodec
    {
        private static readonly char[] EbcdicToAsciiTable = new char[256]
        {
            // 0x00 - 0x0F
            '\0',   '\x01', '\x02', '\x03', '\x9C', '\t',   '\x86', '\x7F',
            '\x97', '\x8D', '\x8E', '\x0B', '\x0C', '\r',   '\x0E', '\x0F',
            // 0x10 - 0x1F
            '\x10', '\x11', '\x12', '\x13', '\x9D', '\x85', '\x08', '\x87',
            '\x18', '\x19', '\x92', '\x8F', '\x1C', '\x1D', '\x1E', '\x1F',
            // 0x20 - 0x2F
            '\x80', '\x81', '\x82', '\x83', '\x84', '\n',   '\x17', '\x1B',
            '\x88', '\x89', '\x8A', '\x8B', '\x8C', '\x05', '\x06', '\x07',
            // 0x30 - 0x3F
            '\x90', '\x91', '\x16', '\x93', '\x94', '\x95', '\x96', '\x04',
            '\x98', '\x99', '\x9A', '\x9B', '\x14', '\x15', '\x9E', '\x1A',
            // 0x40 - 0x4F
            ' ',    '\xA0', '\xE2', '\xE4', '\xE0', '\xE1', '\xE3', '\xE5',
            '\xE7', '\xF1', '\xA2', '.',    '<',    '(',    '+',    '|',
            // 0x50 - 0x5F
            '&',    '\xE9', '\xEA', '\xEB', '\xE8', '\xED', '\xEE', '\xEF',
            '\xEC', '\xDF', '!',    '$',    '*',    ')',    ';',    '^',
            // 0x60 - 0x6F
            '-',    '/',    '\xC2', '\xC4', '\xC0', '\xC1', '\xC3', '\xC5',
            '\xC7', '\xD1', '\xA6', ',',    '%',    '_',    '>',    '?',
            // 0x70 - 0x7F
            '\xF8', '\xC9', '\xCA', '\xCB', '\xC8', '\xCD', '\xCE', '\xCF',
            '\xCC', '`',    ':',    '#',    '@',    '\'',   '=',    '"',
            // 0x80 - 0x8F
            '\xD8', 'a',    'b',    'c',    'd',    'e',    'f',    'g',
            'h',    'i',    '\xAB', '\xBB', '\xF0', '\xFD', '\xFE', '\xB1',
            // 0x90 - 0x9F
            '\xB0', 'j',    'k',    'l',    'm',    'n',    'o',    'p',
            'q',    'r',    '\xAA', '\xBA', '\xE6', '\xB8', '\xC6', '\xA4',
            // 0xA0 - 0xAF
            '\xB5', '~',    's',    't',    'u',    'v',    'w',    'x',
            'y',    'z',    '\xA1', '\xBF', '\xD0', '\xAD', '\xDE', '\xAE',
            // 0xB0 - 0xBF
            '\xAC', '\xA3', '\xA5', '\xB7', '\xA9', '\xA7', '\xB6', '\xBC',
            '\xBD', '\xBE', '[',    ']',    '\xDA', '\xDB', '\xDC', '\xD9',
            // 0xC0 - 0xCF
            '{',    'A',    'B',    'C',    'D',    'E',    'F',    'G',
            'H',    'I',    '\xAD', '\xF4', '\xF6', '\xF2', '\xF3', '\xF5',
            // 0xD0 - 0xDF
            '}',    'J',    'K',    'L',    'M',    'N',    'O',    'P',
            'Q',    'R',    '\xB9', '\xFB', '\xFC', '\xF9', '\xFA', '\xFF',
            // 0xE0 - 0xEF
            '\\',   '\xF7', 'S',    'T',    'U',    'V',    'W',    'X',
            'Y',    'Z',    '\xB2', '\xD4', '\xD6', '\xD2', '\xD3', '\xD5',
            // 0xF0 - 0xFF
            '0',    '1',    '2',    '3',    '4',    '5',    '6',    '7',
            '8',    '9',    '\xB3', '\xDB', '\xDC', '\xD9', '\xDA', '\x9F'
        };

        private static readonly byte[] AsciiToEbcdicTable = new byte[256];

        static EbcdicCodec()
        {
            // Initialize inverse lookup table default to space (0x40)
            for (int i = 0; i < 256; i++)
            {
                AsciiToEbcdicTable[i] = 0x40;
            }

            for (int i = 0; i < 256; i++)
            {
                char c = EbcdicToAsciiTable[i];
                if (c < 256 && c != '\0' && c != ' ')
                {
                    AsciiToEbcdicTable[(byte)c] = (byte)i;
                }
            }

            // Ensure standard printable ASCII characters are explicitly mapped
            AsciiToEbcdicTable[' ']  = 0x40;
            AsciiToEbcdicTable['.']  = 0x4B;
            AsciiToEbcdicTable['<']  = 0x4C;
            AsciiToEbcdicTable['(']  = 0x4D;
            AsciiToEbcdicTable['+']  = 0x4E;
            AsciiToEbcdicTable['|']  = 0x4F;
            AsciiToEbcdicTable['&']  = 0x50;
            AsciiToEbcdicTable['!']  = 0x5A;
            AsciiToEbcdicTable['$']  = 0x5B;
            AsciiToEbcdicTable['*']  = 0x5C;
            AsciiToEbcdicTable[')']  = 0x5D;
            AsciiToEbcdicTable[';']  = 0x5E;
            AsciiToEbcdicTable['^']  = 0x5F;
            AsciiToEbcdicTable['-']  = 0x60;
            AsciiToEbcdicTable['/']  = 0x61;
            AsciiToEbcdicTable[',']  = 0x6B;
            AsciiToEbcdicTable['%']  = 0x6C;
            AsciiToEbcdicTable['_']  = 0x6D;
            AsciiToEbcdicTable['>']  = 0x6E;
            AsciiToEbcdicTable['?']  = 0x6F;
            AsciiToEbcdicTable['`']  = 0x79;
            AsciiToEbcdicTable[':']  = 0x7A;
            AsciiToEbcdicTable['#']  = 0x7B;
            AsciiToEbcdicTable['@']  = 0x7C;
            AsciiToEbcdicTable['\''] = 0x7D;
            AsciiToEbcdicTable['=']  = 0x7E;
            AsciiToEbcdicTable['"']  = 0x7F;
            AsciiToEbcdicTable['~']  = 0xA1;
            AsciiToEbcdicTable['[']  = 0xBA;
            AsciiToEbcdicTable[']']  = 0xBB;
            AsciiToEbcdicTable['{']  = 0xC0;
            AsciiToEbcdicTable['}']  = 0xD0;
            AsciiToEbcdicTable['\\'] = 0xE0;
        }

        /// <summary>
        /// Translates a single EBCDIC byte to its corresponding ASCII / Unicode character.
        /// </summary>
        public static char ToChar(byte ebcdicByte)
        {
            return EbcdicToAsciiTable[ebcdicByte];
        }

        /// <summary>
        /// Translates a single ASCII character to an EBCDIC byte.
        /// </summary>
        public static byte ToEbcdic(char asciiChar)
        {
            if (asciiChar < 256)
            {
                return AsciiToEbcdicTable[(byte)asciiChar];
            }
            return 0x40; // Fallback to space
        }

        /// <summary>
        /// Translates an ASCII string into an EBCDIC byte array.
        /// </summary>
        public static byte[] ToEbcdicBytes(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new byte[0];
            }

            byte[] result = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                result[i] = ToEbcdic(text[i]);
            }
            return result;
        }

        /// <summary>
        /// Translates an EBCDIC byte array segment into an ASCII string.
        /// </summary>
        public static string ToString(byte[] ebcdicBytes, int offset, int count)
        {
            if (ebcdicBytes == null || count <= 0 || offset >= ebcdicBytes.Length)
            {
                return string.Empty;
            }

            int actualCount = Math.Min(count, ebcdicBytes.Length - offset);
            char[] chars = new char[actualCount];
            for (int i = 0; i < actualCount; i++)
            {
                chars[i] = EbcdicToAsciiTable[ebcdicBytes[offset + i]];
            }
            return new string(chars);
        }
    }
}
