using System;

namespace AS400Automation.Protocol
{
    /// <summary>
    /// Represents an individual field definition on an IBM 5250 terminal screen.
    /// </summary>
    public class Tn5250Field
    {
        public int StartRow { get; }
        public int StartCol { get; }
        public int Length { get; internal set; }
        public byte AttributeByte { get; }
        public byte Ffw1 { get; }
        public byte Ffw2 { get; }

        public bool IsBypass => (Ffw1 & 0x20) != 0;
        public bool IsProtected => (Ffw1 & 0x40) != 0;
        public bool IsHidden => (AttributeByte == 0x27 || AttributeByte == 0x37);
        public bool IsModified { get; set; }

        public Tn5250Field(int startRow, int startCol, byte ffw1, byte ffw2, byte attributeByte, int length = 0)
        {
            StartRow = startRow;
            StartCol = startCol;
            Ffw1 = ffw1;
            Ffw2 = ffw2;
            AttributeByte = attributeByte;
            Length = length;
            IsModified = false;
        }

        /// <summary>
        /// Checks if given 1-based (row, col) is within this field's span.
        /// </summary>
        public bool Contains(int row, int col)
        {
            if (Length <= 0) return false;

            int fieldStartIndex = ((StartRow - 1) * Tn5250Constants.SCREEN_COLS) + (StartCol - 1);
            int targetIndex = ((row - 1) * Tn5250Constants.SCREEN_COLS) + (col - 1);

            return targetIndex >= fieldStartIndex && targetIndex < (fieldStartIndex + Length);
        }
    }
}
